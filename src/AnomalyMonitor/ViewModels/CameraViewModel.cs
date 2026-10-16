using System.Globalization;
using AnomalyMonitor.Infrastructure;
using AnomalyMonitor.Services;
using VisionAnomaly;
using VisionAnomaly.Monitoring;

namespace AnomalyMonitor.ViewModels;

public sealed class CameraViewModel : ObservableObject, IDisposable
{
    private readonly CameraSession _session;
    private readonly IMonitorFiles _files;
    private readonly Action<Action> _dispatch;
    private readonly List<MonitorCommand> _commands = [];
    private string _address = "", _status = "영상을 연결하세요.", _detail = "연결 안 됨", _renderer = "DX12 대기";
    private string _regionX = "0", _regionY = "0", _regionWidth = "100", _regionHeight = "100", _sampleCount = "60", _threshold = "1.0";
    private bool _selected, _busy, _disposed;
    private string? _commandError;
    private int _sourceKind;
    private SessionSnapshot _snapshot = new(MonitorMode.Disconnected, "");

    public CameraViewModel(string name, CameraSession session, IMonitorFiles files, Action<Action> dispatch, Action<CameraViewModel> select)
    {
        Name = name;
        _session = session;
        _files = files;
        _dispatch = dispatch;
        SelectCommand = Command(() => { select(this); return Task.CompletedTask; }, () => true, false);
        OpenCommand = Command(async () =>
        {
            if (_files.OpenVideo() is not { } path)
            {
                return;
            }

            SourceKind = 0;
            Address = path;
            await ConnectAsync();
        });
        ConnectCommand = Command(ConnectAsync);
        DisconnectCommand = Command(_session.StopAsync, () => _snapshot.Mode != MonitorMode.Disconnected);
        LearnCommand = Command(async () =>
        {
            if (!int.TryParse(SampleCount, out var count))
            {
                throw new ArgumentException("정상 프레임 수를 정수로 입력하세요.");
            }

            var region = new AnalysisRegion(Parse(RegionX) / 100, Parse(RegionY) / 100, Parse(RegionWidth) / 100, Parse(RegionHeight) / 100);
            await _session.BeginLearningAsync(region, count);
        }, () => HasVideo && _snapshot.Mode is not (MonitorMode.Learning or MonitorMode.Fitting));
        MonitorCommand = Command(_session.ToggleMonitoringAsync, () => HasVideo && _snapshot.ModelId is not null && _snapshot.Mode is not (MonitorMode.Learning or MonitorMode.Fitting));
        SaveCommand = Command(async () => { if (_files.SaveModel() is { } path) { await _session.SaveModelAsync(path); } }, () => _snapshot.ModelId is not null);
        LoadCommand = Command(async () => { if (_files.OpenModel() is { } path) { await _session.LoadModelAsync(path); } }, () => HasVideo && _snapshot.Mode is not (MonitorMode.Learning or MonitorMode.Fitting));
        ThresholdCommand = Command(async () => { await _session.SetThresholdAsync((float)Parse(Threshold)); Status = "알람 배율 적용됨 · 1.0은 학습 기준"; }, () => HasVideo);
        _session.Updated += OnUpdated;
    }

    public string Name
    {
        get;
    }
    public int SourceKind
    {
        get => _sourceKind; set => Set(ref _sourceKind, value);
    }
    public string Address
    {
        get => _address; set => Set(ref _address, value);
    }
    public string Status
    {
        get => _status; private set => Set(ref _status, value);
    }
    public string Detail
    {
        get => _detail; private set => Set(ref _detail, value);
    }
    public string Renderer
    {
        get => _renderer; set => Set(ref _renderer, value);
    }
    public bool IsSelected
    {
        get => _selected; set => Set(ref _selected, value);
    }
    public string RegionX
    {
        get => _regionX; set => Set(ref _regionX, value);
    }
    public string RegionY
    {
        get => _regionY; set => Set(ref _regionY, value);
    }
    public string RegionWidth
    {
        get => _regionWidth; set => Set(ref _regionWidth, value);
    }
    public string RegionHeight
    {
        get => _regionHeight; set => Set(ref _regionHeight, value);
    }
    public string SampleCount
    {
        get => _sampleCount; set => Set(ref _sampleCount, value);
    }
    public string Threshold
    {
        get => _threshold; set => Set(ref _threshold, value);
    }
    public bool HasVideo => _snapshot.Width > 0 && _snapshot.Mode is not (MonitorMode.Disconnected or MonitorMode.Connecting or MonitorMode.Ended or MonitorMode.Faulted);
    public string MonitorCaption => _snapshot.Mode == MonitorMode.Monitoring ? "감시 일시정지" : "감시 시작";
    public string ModelDescription => _snapshot.ModelId is { } id ? $"정상 모델 {id.ToString()[..8]} · 수집{_snapshot.TrainingFrames}프레임" : "정상 기준 없음 · 영상 연결 후 정상 수집";
    public string ScoreText => _snapshot.Prediction is { } score
        ? $"외관 {score.AppearanceRatio:F2}× / 움직임 {(score.MotionAvailable ? score.MotionRatio.ToString("F2") + "×" : "준비 중")}" : "현재 검사 결과 없음";
    public MonitorCommand SelectCommand
    {
        get;
    }
    public MonitorCommand OpenCommand
    {
        get;
    }
    public MonitorCommand ConnectCommand
    {
        get;
    }
    public MonitorCommand DisconnectCommand
    {
        get;
    }
    public MonitorCommand LearnCommand
    {
        get;
    }
    public MonitorCommand MonitorCommand
    {
        get;
    }
    public MonitorCommand SaveCommand
    {
        get;
    }
    public MonitorCommand LoadCommand
    {
        get;
    }
    public MonitorCommand ThresholdCommand
    {
        get;
    }

    private Task ConnectAsync() => _session.ConnectAsync(new((VideoSourceKind)SourceKind, Address));
    private static double Parse(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
        ? number : throw new ArgumentException("숫자 입력을 확인하세요. 소수점은 점(.)을 사용합니다.");

    private MonitorCommand Command(Func<Task> action, Func<bool>? available = null, bool exclusive = true)
    {
        var command = new MonitorCommand(async () =>
        {
            if (exclusive)
            {
                _commandError = null;
                _busy = true;
                Refresh();
            }
            try
            {
                await action();
            }
            finally { if (exclusive) { _busy = false; Refresh(); } }
        }, () => !_disposed && (!exclusive || !_busy) && (available?.Invoke() ?? true), error => Status = _commandError = "실행 불가: " + error.Message);
        _commands.Add(command);
        return command;
    }

    private void OnUpdated(SessionSnapshot snapshot) => _dispatch(() =>
    {
        if (_disposed)
        {
            return;
        }

        var modelChanged = _snapshot.ModelId != snapshot.ModelId;
        _snapshot = snapshot;
        Status = snapshot.Message;
        Detail = snapshot.Width == 0 ? "연결 안 됨" : $"{snapshot.Width}×{snapshot.Height} · #{snapshot.Sequence} · 건너뜀{snapshot.SkippedFrames} · 분석{snapshot.Prediction?.ProcessingMilliseconds ?? 0:F0}ms";
        if (snapshot.Mode == MonitorMode.Learning)
        {
            Status = $"정상 수집 {snapshot.TrainingFrames}/{SampleCount} · 이상이 없는 영상만 사용하세요.";
        }
        if (_commandError is not null)
        {
            Status = _commandError;
        }

        if (modelChanged && snapshot.Region is { } region && snapshot.ModelId is not null)
        {
            RegionX = (region.X * 100).ToString("0.##", CultureInfo.InvariantCulture);
            RegionY = (region.Y * 100).ToString("0.##", CultureInfo.InvariantCulture);
            RegionWidth = (region.Width * 100).ToString("0.##", CultureInfo.InvariantCulture);
            RegionHeight = (region.Height * 100).ToString("0.##", CultureInfo.InvariantCulture);
        }
        Changed(nameof(HasVideo));
        Changed(nameof(MonitorCaption));
        Changed(nameof(ModelDescription));
        Changed(nameof(ScoreText));
        Refresh();
    });

    private void Refresh()
    {
        foreach (var command in _commands)
        {
            command.Refresh();
        }
    }
    public void Dispose()
    {
        _disposed = true;
        _session.Updated -= OnUpdated;
        Refresh();
    }
}
