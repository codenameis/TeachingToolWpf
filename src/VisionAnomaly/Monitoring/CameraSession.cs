using System.Diagnostics;
using System.Threading.Channels;
using VisionAnomaly.Imaging;
using VisionImaging.Abstractions;

namespace VisionAnomaly.Monitoring;

/// <summary>수집/분석 worker와 네이티브 자원 수명을 소유한다. UI에는 중립 스냅샷만 알린다.</summary>
public sealed class CameraSession(string cameraName, string featurePath, AlarmStore alarms) : IAsyncDisposable
{
    private readonly SemaphoreSlim _processorGate = new(1);
    private CancellationTokenSource? _lifetime;
    private Task _running = Task.CompletedTask;
    private AnomalyProcessor? _processor;
    private readonly AlarmGate _alarmGate = new();
    private MonitorMode _mode;
    private int _width, _height, _targetFrames;
    private long _skipped;
    private bool _disposed;
    private AnalysisRegion _region = AnalysisRegion.Full;
    private float _threshold = 1;

    public event Action<SessionSnapshot>? Updated;
    // 구독자는 콜백이 끝난 뒤 사용할 프레임을 Retain해야 한다. 원본은 세션이 해제한다.
    public event Action<VideoFrame, AnomalyPrediction?>? FrameReady;
    public event Action<AlarmRecord>? AlarmRaised;
    public SessionSnapshot Snapshot { get; private set; } = new(MonitorMode.Disconnected, "영상을 연결하세요.");

    public async Task ConnectAsync(VideoSourceSpec source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        source.Validate();
        await StopAsync();
        _width = _height = 0;
        _skipped = 0;
        _region = AnalysisRegion.Full;
        Snapshot = new(MonitorMode.Disconnected, "영상을 연결하세요.");
        _lifetime = new();
        Publish(MonitorMode.Connecting, "영상 연결 중…");
        _running = Task.Run(() => RunAsync(source, _lifetime.Token));
    }

    public async Task StopAsync()
    {
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync();
        }

        await _running;
        _lifetime?.Dispose();
        _lifetime = null;
        await _processorGate.WaitAsync();
        try
        {
            _processor?.Dispose();
            _processor = null;
            _alarmGate.Reset();
        }
        finally { _processorGate.Release(); }
        Publish(MonitorMode.Disconnected, "연결 종료 · 검사하지 않음");
    }

    public Task BeginLearningAsync(AnalysisRegion region, int frames) => WithProcessorAsync(processor =>
    {
        if (_width == 0 || _mode is MonitorMode.Ended or MonitorMode.Faulted or MonitorMode.Learning or MonitorMode.Fitting)
        {
            throw new InvalidOperationException("영상을 연결하고 수집 가능한 상태인지 확인하세요.");
        }

        if (frames is < 30 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "정상 수집은30~200프레임입니다.");
        }

        region.Validate();
        _region = region;
        _targetFrames = frames;
        processor.BeginLearning(_width, _height, region);
        _alarmGate.Reset();
        Publish(MonitorMode.Learning, $"정상 모습 수집 중 · 목표{frames}프레임");
    });

    public Task LoadModelAsync(string path) => WithProcessorAsync(processor =>
    {
        if (_mode is MonitorMode.Learning or MonitorMode.Fitting)
        {
            throw new InvalidOperationException("정상 수집 중에는 모델을 교체할 수 없습니다.");
        }

        var model = NormalModel.Load(path);
        if (model.Width != _width || model.Height != _height)
        {
            throw new InvalidDataException("모델과 현재 영상의 크기가 다릅니다.");
        }

        processor.UseModel(model);
        _region = model.Region;
        _alarmGate.Reset();
        Publish(MonitorMode.Ready, "정상 모델 준비됨 · 같은 촬영 장면인지 확인 후 감시하세요.");
    });

    public Task SaveModelAsync(string path) => WithProcessorAsync(processor =>
        (processor.Model ?? throw new InvalidOperationException("저장할 정상 기준이 없습니다.")).Save(path));

    public Task ToggleMonitoringAsync() => WithProcessorAsync(processor =>
    {
        if (processor.Model is null || _mode is MonitorMode.Ended or MonitorMode.Faulted or MonitorMode.Learning or MonitorMode.Fitting)
        {
            throw new InvalidOperationException("연결된 영상과 정상 모델이 필요합니다.");
        }

        processor.ResetTemporal();
        _alarmGate.Reset();
        Publish(_mode == MonitorMode.Monitoring ? MonitorMode.Paused : MonitorMode.Monitoring,
            _mode == MonitorMode.Monitoring ? "감시 일시정지 · 새 검사 없음" : "감시 중 · 분석 프레임의 이상 후보를 표시합니다.");
    });

    public Task SetThresholdAsync(float multiplier) => WithProcessorAsync(_ =>
    {
        if (!float.IsFinite(multiplier) || multiplier is < .5f or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }

        _threshold = multiplier;
        _alarmGate.Reset();
    });

    private Task WithProcessorAsync(Action<AnomalyProcessor> action) => Task.Run(async () =>
    {
        await _processorGate.WaitAsync();
        try
        {
            action(_processor ?? throw new InvalidOperationException("영상 연결을 먼저 완료하세요."));
        }
        finally { _processorGate.Release(); }
    });

    private async Task RunAsync(VideoSourceSpec source, CancellationToken token)
    {
        var frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        },
            frame => { frame.Dispose(); Interlocked.Increment(ref _skipped); });
        var capture = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                using var reader = new VideoReader(source);
                var clock = Stopwatch.StartNew();
                while (reader.Read(token) is { } frame)
                {
                    if (!frames.Writer.TryWrite(frame))
                    {
                        frame.Dispose();
                    }

                    if (source.Kind == VideoSourceKind.File)
                    {
                        var wait = reader.MediaSeconds + (1 / reader.FramesPerSecond) - clock.Elapsed.TotalSeconds;
                        if (wait > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 1)), token);
                        }
                    }
                }
            }
            catch (Exception error) { failure = error; }
            finally { frames.Writer.TryComplete(failure); }
        }, CancellationToken.None);
        try
        {
            await _processorGate.WaitAsync(token);
            try
            {
                _processor = new(featurePath);
            }
            finally { _processorGate.Release(); }
            await foreach (var frame in frames.Reader.ReadAllAsync(token))
            {
                using (frame)
                {
                    await _processorGate.WaitAsync(token);
                    try
                    {
                        Process(frame, token);
                    }
                    finally { _processorGate.Release(); }
                }
                // 검사 간격을 명시한다. 밀린 프레임을 누적하지 않고 다음 최신 프레임을 사용한다.
                await Task.Delay(200, token);
            }
            Publish(MonitorMode.Ended, _mode == MonitorMode.Learning
                ? "영상 끝 · 정상 수집 미완료. 더 긴 정상 영상을 연결하세요."
                : "영상 재생 끝 · 현재 검사하지 않음");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Publish(MonitorMode.Faulted, $"검사 중단: {error.Message}");
            if (_lifetime is not null)
            {
                await _lifetime.CancelAsync();
            }
        }
        finally
        {
            await capture;
            while (frames.Reader.TryRead(out var frame))
            {
                frame.Dispose();
            }
        }
    }

    private void Process(VideoFrame frame, CancellationToken token)
    {
        var processor = _processor!;
        if (_width == 0)
        {
            _width = frame.Pixels.Width;
            _height = frame.Pixels.Height;
            Publish(MonitorMode.Preview, "미리보기 · 정상 기준을 준비하세요.");
        }
        if (_width != frame.Pixels.Width || _height != frame.Pixels.Height)
        {
            throw new InvalidDataException("입력 영상의 해상도가 바뀌었습니다. 다시 연결·학습하세요.");
        }

        AnomalyPrediction? prediction = null;
        if (_mode == MonitorMode.Learning)
        {
            processor.Learn(frame, token);
            if (processor.TrainingFrames >= _targetFrames)
            {
                Publish(MonitorMode.Fitting, "정상 패치 메모리와 임계값 계산 중…");
                processor.CompleteLearning(token);
                Publish(MonitorMode.Ready, "정상 기준 준비됨 · 저장하거나 감시를 시작하세요.");
            }
        }
        else if (_mode == MonitorMode.Monitoring)
        {
            prediction = processor.Predict(frame, token);
            prediction = prediction with
            {
                Areas = prediction.Areas.Where(area => area.Ratio >= _threshold).ToArray(),
                MotionAreas = prediction.MotionAreas.Where(area => area.Ratio >= _threshold).ToArray()
            };
            foreach (var kind in _alarmGate.Update(prediction, frame.Timestamp / (double)Stopwatch.Frequency, _threshold))
            {
                var alarm = alarms.Add(cameraName, processor.Model!, frame, kind, prediction);
                AlarmRaised?.Invoke(alarm);
            }
        }
        FrameReady?.Invoke(frame, prediction);
        Snapshot = new(_mode, Snapshot.Message, _width, _height, frame.Sequence, processor.TrainingFrames,
            Interlocked.Read(ref _skipped), prediction, processor.Model?.Id, _region);
        Updated?.Invoke(Snapshot);
    }

    private void Publish(MonitorMode mode, string message)
    {
        _mode = mode;
        Snapshot = Snapshot with
        {
            Mode = mode,
            Message = message,
            ModelId = _processor?.Model?.Id,
            Region = _region,
            Prediction = null
        };
        Updated?.Invoke(Snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _processorGate.Dispose();
    }
}
