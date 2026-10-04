using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Rendering;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Presentation;

/// <summary>세션의 불변 모델과 일회 검사 결과. ROI 문서/편집 이력은 변경하지 않는다.</summary>
public sealed class ShapeMatchingViewModel : ObservableObject, IDisposable
{
    private readonly IShapeMatchingService _service;
    private ImageReference? _image;
    private RoiGeometry? _selection;
    private long _generation;
    private bool _live;
    private bool _canExecute;
    private bool _disposed;
    private CancellationTokenSource? _cancellation;
    private long _epoch;
    private ShapeModel? _model;
    private string _status = "ROI 하나를 선택해 모델을 학습하세요. 모델은 이 세션에서 이미지 교체 후에도 유지됩니다.";
    private bool _useSelection;
    private bool _training;
    private double _minimumScore = 0.72;
    private int _maximumMatches = 16;
    private bool _showFeatures;
    private bool _showOverlay = true;
    private ResultOverlay? _result;

    public event Action<BoundsD>? LocateRequested;

    public event Action? Changed;
    public AsyncCommand Train
    {
        get;
    }
    public AsyncCommand Search
    {
        get;
    }
    public RelayCommand Cancel
    {
        get;
    }
    public RelayCommand Clear
    {
        get;
    }
    public bool IsIdle => !_disposed && _cancellation is null;
    public ShapeModel? Model => _model;
    public ImmutableArray<MatchMarker> Markers => _result?.Matches ?? [];
    public ResultOverlay? Overlay => ShowOverlay ? _result : null;
    public InspectionVerdict? Verdict
    {
        get; private set;
    }
    public string VerdictText => Verdict is { } verdict ? InspectionResultPresentation.Name(verdict) : IsIdle ? "검사 대기" : "처리 중";
    public string VerdictColor => Verdict is { } verdict ? InspectionResultPresentation.Hex(verdict) : "#94A3B8";
    public RelayCommand Locate
    {
        get;
    }
    public string ModelSummary => _model is null ? "학습된 모델 없음" : $"특징 {_model.Levels[0].Features.Length}개 · {_model.Levels.Length}단계 · 템플릿 {_model.TemplateCount}개";
    public string Status
    {
        get => _status; private set => Set(ref _status, value);
    }
    public string Results { get; private set; } = "검사 결과 없음";
    public double MinimumAngle { get; set; } = -180;
    public double MaximumAngle { get; set; } = 180;
    public double AngleStep { get; set; } = 10;
    public bool EnableScale
    {
        get; set;
    }
    public double MinimumScale { get; set; } = 0.9;
    public double MaximumScale { get; set; } = 1.1;
    public bool IgnorePolarity
    {
        get; set;
    }
    public double MinimumScore
    {
        get => _minimumScore;
        set
        {
            if (Set(ref _minimumScore, value))
            {
                Invalidate();
            }
        }
    }

    public int MaximumMatches
    {
        get => _maximumMatches;
        set
        {
            if (Set(ref _maximumMatches, value))
            {
                Invalidate();
            }
        }
    }

    public bool ShowFeatures
    {
        get => _showFeatures;
        set
        {
            if (Set(ref _showFeatures, value))
            {
                if (_result is not null)
                {
                    _result = _result with
                    {
                        ShowFeatures = value
                    };
                }
                Changed?.Invoke();
            }
        }
    }

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            if (Set(ref _showOverlay, value))
            {
                Changed?.Invoke();
            }
        }
    }

    public bool UseSelection
    {
        get => _useSelection;
        set
        {
            if (Set(ref _useSelection, value))
            {
                Invalidate();
                RefreshCommands();
            }
        }
    }

    public ShapeMatchingViewModel(IShapeMatchingService service)
    {
        _service = service;
        Train = new(TrainAsync, Failed, () => IsIdle && _canExecute && _image is not null && _selection is not null);
        Search = new(SearchAsync, Failed, () => IsIdle && _canExecute && _image is not null && _model is not null && (!UseSelection || _selection is not null));
        Cancel = new(() => _cancellation?.Cancel(), () => _cancellation is not null);
        Clear = new(() => { _model = null; Invalidate(); Notify(nameof(ModelSummary)); RefreshCommands(); }, () => IsIdle && _model is not null);
        Locate = new(() =>
        {
            var points = Markers.SelectMany(marker => marker.Bounds).ToArray();
            LocateRequested?.Invoke(new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)));
        }, () => _canExecute && IsIdle && !Markers.IsEmpty);
    }

    public void Update(DocumentState state, long generation, bool live, bool canExecute)
    {
        var selection = state.Selection.Count == 1 ? state.Rois[state.Selection.Single()].Geometry : null;
        if (_image != state.Image || _generation != generation || _live != live ||
            ((UseSelection || _training) && _selection != selection) ||
            (_live && _canExecute && !canExecute))
        {
            Invalidate();
        }
        _image = state.Image;
        _generation = generation;
        _selection = selection;
        _live = live;
        _canExecute = canExecute;
        RefreshCommands();
    }

    private async Task TrainAsync()
    {
        _training = true;
        var image = _image!;
        var roi = _selection!;
        var live = _live;
        var options = new TrainingOptions
        {
            MinimumAngleDegrees = MinimumAngle,
            MaximumAngleDegrees = MaximumAngle,
            AngleStepDegrees = AngleStep,
            MinimumScale = EnableScale ? MinimumScale : 1,
            MaximumScale = EnableScale ? MaximumScale : 1,
            Polarity = IgnorePolarity ? ContrastPolarity.IgnoreLocal : ContrastPolarity.Preserve
        };
        await RunAsync(async (token, epoch) =>
        {
            Status = "원본 ROI 특징으로 모델 생성 중…";
            var output = await _service.TrainAsync(image, live, roi, options, token);
            if (!Current(epoch, token))
            {
                return;
            }
            _model = output.Model;
            Results = "학습 완료 · 검사할 이미지와 범위를 선택하세요.";
            Status = $"읽기/변환/마스크 {output.ReadMilliseconds:F1} ms · 학습 {output.TrainingMilliseconds:F1} ms";
            Notify(nameof(ModelSummary));
            Notify(nameof(Results));
            Changed?.Invoke();
        });
    }

    private async Task SearchAsync()
    {
        var image = _image!;
        var model = _model!;
        var roi = UseSelection ? _selection : null;
        var live = _live;
        var options = new SearchOptions { MinimumScore = MinimumScore, MaximumMatches = MaximumMatches };
        await RunAsync(async (token, epoch) =>
        {
            Status = "원본 해상도 검색 중… 취소 가능";
            var output = await _service.SearchAsync(image, live, model, roi, options, token);
            if (!Current(epoch, token) || !ReferenceEquals(model, _model))
            {
                return;
            }
            var decision = InspectionDecision.Evaluate(output);
            var boundary = roi?.Outline.ToImmutableArray() ?? [new(0, 0), new(image.Width, 0), new(image.Width, image.Height), new(0, image.Height)];
            Verdict = decision.Verdict;
            _result = new($"{InspectionResultPresentation.Name(decision.Verdict)} · 검출 {output.Matches.Length}개",
                InspectionResultPresentation.Color(decision.Verdict), boundary, InspectionResultPresentation.Markers(model, output), ShowFeatures);
            Results = decision.Reason + (output.Matches.IsEmpty ? "\n검출 위치 없음" : "\n\n" + string.Join("\n\n", output.Matches.Select((m, i) =>
                $"{i + 1} · X {m.Pose.X:F3} / Y {m.Pose.Y:F3}\n각도 {RoiGeometry.NormalizeAngle(m.Pose.Angle * 180 / Math.PI):F3}° · 배율 {m.Pose.Scale:F4}\n점수 {m.Score:F3} · 지지 {m.Support:P0}\n정밀화 {m.Refinement.Status} · 회전 관측 {(m.Refinement.AngleObservable ? "가능" : "불가")}")));
            Results += output.Matches.IsEmpty ? "" : "\n\n좌표: 원본 이미지 픽셀 · 각도: 이미지 기준 시계 방향";
            Status = $"{output.Matches.Length}개 · {output.TotalMilliseconds:F1} ms (읽기/변환 {output.ReadMilliseconds:F1}) · 창 {output.Windows}개" +
                (output.Truncated ? " · 후보 예산 초과: 검색 불완전" : "");
            PublishResult();
        });
    }

    private async Task RunAsync(Func<CancellationToken, long, Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var epoch = ++_epoch;
        ResetResult("처리 중 · 이전 결과를 지웠습니다.");
        RefreshCommands();
        try
        {
            await action(cancellation.Token, epoch);
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning("작업 취소 · 판정하지 않았습니다.");
            }
        }
        catch (Exception error)
        {
            if (Current(epoch, cancellation.Token))
            {
                PresentWarning($"{(_training ? "학습" : "검사")} 오류 · " + error.Message);
            }
        }
        finally
        {
            _training = false;
            _cancellation = null;
            RefreshCommands();
        }
    }

    private bool Current(long epoch, CancellationToken token) => !_disposed && !token.IsCancellationRequested && epoch == _epoch;

    private void Invalidate()
    {
        _epoch++;
        _cancellation?.Cancel();
        if (_result is not null || _cancellation is not null)
        {
            ResetResult("입력 변경 · 다시 검사하세요.");
        }
    }

    private void ResetResult(string message)
    {
        _result = null;
        Verdict = null;
        Results = message;
        PublishResult();
    }

    private void PresentWarning(string message)
    {
        Verdict = InspectionVerdict.Warning;
        Results = message;
        Status = message;
        _result = new("WARN · 경고 · 판정 미완료", InspectionResultPresentation.Color(InspectionVerdict.Warning), [], []);
        PublishResult();
    }

    private void PublishResult()
    {
        Notify(nameof(Results));
        Notify(nameof(VerdictText));
        Notify(nameof(VerdictColor));
        Locate.Refresh();
        Changed?.Invoke();
    }

    private void Failed(Exception error)
    {
        if (!_disposed)
        {
            PresentWarning(error.Message);
        }
    }

    private void RefreshCommands()
    {
        Notify(nameof(IsIdle));
        Notify(nameof(VerdictText));
        Train.Refresh();
        Search.Refresh();
        Cancel.Refresh();
        Clear.Refresh();
        Locate.Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        Invalidate();
    }
}
