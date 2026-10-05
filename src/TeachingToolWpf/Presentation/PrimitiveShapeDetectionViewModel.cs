using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Presentation;

public sealed record PrimitivePolarityOption(ForegroundPolarity Value, string Name);

public sealed class PrimitiveShapeDetectionViewModel : ObservableObject, IDisposable
{
    private readonly IPrimitiveShapeDetectionService _service;
    private PrimitiveDetectionOptions _options = new();
    private ImageReference? _image;
    private RoiGeometry? _selection;
    private long _generation;
    private long _epoch;
    private bool _live;
    private bool _canExecute;
    private bool _inputAvailable;
    private bool _disposed;
    private bool _useSelection;
    private bool _hasInputErrors;
    private bool _showOverlay = true;
    private double _searchTimeoutSeconds;
    private CancellationTokenSource? _cancellation;
    private ResultOverlay? _overlay;

    public PrimitiveShapeDetectionViewModel(IPrimitiveShapeDetectionService service)
    {
        _service = service;
        Detect = new(DetectAsync, error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && (!UseSelection || _selection is not null) && OptionsError.Length == 0);
        Cancel = new(() => _cancellation?.Cancel(), () => IsBusy);
        Locate = new(() => LocateRequested?.Invoke(BoundsD.FromPoints(_overlay!.Matches.SelectMany(marker => marker.Bounds))),
            () => IsIdle && _canExecute && _overlay?.Matches.IsEmpty == false);
    }

    public event Action? Changed;
    public event Action<BoundsD>? LocateRequested;
    public AsyncCommand Detect
    {
        get;
    }
    public RelayCommand Cancel
    {
        get;
    }
    public RelayCommand Locate
    {
        get;
    }
    public bool IsBusy => _cancellation is not null;
    public bool IsIdle => !_disposed && !IsBusy;
    public ResultOverlay? Overlay => ShowOverlay ? _overlay : null;
    public string Status { get; private set; } = "학습 없이 채워진 삼각형·사각형·원·십자가를 검출합니다.";
    public string Results { get; private set; } = "검출 결과 없음";
    public PrimitiveDetectionOutput? LastOutput
    {
        get; private set;
    }
    public IReadOnlyList<PrimitivePolarityOption> Polarities
    {
        get;
    } =
    [
        new(ForegroundPolarity.Auto, "자동 · 작은 명암 영역"),
        new(ForegroundPolarity.Bright, "밝은 도형 / 어두운 배경"),
        new(ForegroundPolarity.Dark, "어두운 도형 / 밝은 배경")
    ];
    public bool AutomaticThreshold
    {
        get => _options.AutomaticThreshold; set => Change(_options with { AutomaticThreshold = value });
    }
    public byte Threshold
    {
        get => _options.Threshold; set => Change(_options with { Threshold = value });
    }
    public ForegroundPolarity Polarity
    {
        get => _options.Polarity; set => Change(_options with { Polarity = value });
    }
    public double MinimumSize
    {
        get => _options.MinimumSize; set => Change(_options with { MinimumSize = value });
    }
    public double MaximumSize
    {
        get => _options.MaximumSize; set => Change(_options with { MaximumSize = value });
    }
    public double ApproximationTolerance
    {
        get => _options.ApproximationTolerance; set => Change(_options with { ApproximationTolerance = value });
    }
    public double MinimumScore
    {
        get => _options.MinimumScore; set => Change(_options with { MinimumScore = value });
    }
    public int MaximumResults
    {
        get => _options.MaximumResults; set => Change(_options with { MaximumResults = value });
    }
    public bool FindTriangles
    {
        get => Has(PrimitiveShapeKind.Triangle); set => SetShape(PrimitiveShapeKind.Triangle, value);
    }
    public bool FindRectangles
    {
        get => Has(PrimitiveShapeKind.Rectangle); set => SetShape(PrimitiveShapeKind.Rectangle, value);
    }
    public bool FindCircles
    {
        get => Has(PrimitiveShapeKind.Circle); set => SetShape(PrimitiveShapeKind.Circle, value);
    }
    public bool FindCrosses
    {
        get => Has(PrimitiveShapeKind.Cross); set => SetShape(PrimitiveShapeKind.Cross, value);
    }
    public double SearchTimeoutSeconds
    {
        get => _searchTimeoutSeconds;
        set
        {
            if (Set(ref _searchTimeoutSeconds, value))
            {
                Invalidate();
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
            }
        }
    }
    public bool HasInputErrors
    {
        get => _hasInputErrors;
        set
        {
            if (Set(ref _hasInputErrors, value))
            {
                Invalidate();
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
    public string OptionsError
    {
        get
        {
            if (HasInputErrors)
            {
                return "숫자 입력을 확인하세요.";
            }
            if (!double.IsFinite(SearchTimeoutSeconds) || SearchTimeoutSeconds is < 0 or > 600)
            {
                return "검사 시간 한도는 0~600초입니다. 0은 무제한입니다.";
            }
            try
            {
                _options.Validate();
                return "";
            }
            catch (ArgumentException error)
            {
                return error.Message;
            }
        }
    }

    public void Update(DocumentState state, long generation, bool live, bool canExecute, bool toolAvailable = true)
    {
        var selection = state.Selection.Count == 1 ? state.Rois[state.Selection.Single()].Geometry : null;
        var changed = _image != state.Image || _generation != generation || _live != live || (UseSelection && _selection != selection) ||
            (_live && _inputAvailable && !canExecute);
        _image = state.Image;
        _generation = generation;
        _selection = selection;
        _live = live;
        _inputAvailable = canExecute;
        _canExecute = canExecute && toolAvailable;
        if (changed)
        {
            Invalidate();
        }
        else
        {
            Refresh();
        }
    }

    private async Task DetectAsync()
    {
        var image = _image!;
        var region = UseSelection ? _selection : null;
        var options = _options;
        var live = _live;
        using var cancellation = new CancellationTokenSource();
        using var timeLimit = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, timeLimit.Token);
        if (SearchTimeoutSeconds > 0)
        {
            timeLimit.CancelAfter(TimeSpan.FromSeconds(SearchTimeoutSeconds));
        }
        _cancellation = cancellation;
        var epoch = ++_epoch;
        LastOutput = null;
        var boundary = region?.Outline.ToImmutableArray() ?? [new(0, 0), new(image.Width, 0), new(image.Width, image.Height), new(0, image.Height)];
        Status = "원본 도형 검출 중 · 네이티브 창 처리 후 취소할 수 있습니다.";
        Results = Status;
        _overlay = new(Status, new(.58f, .64f, .72f, 1), boundary, []);
        Publish();
        try
        {
            var output = await _service.DetectAsync(image, live, region, options, combined.Token);
            combined.Token.ThrowIfCancellationRequested();
            if (_disposed || epoch != _epoch)
            {
                return;
            }
            LastOutput = output;
            var color = InspectionResultPresentation.Color(output.Incomplete ? InspectionVerdict.Warning : InspectionVerdict.Pass);
            Status = $"도형 {output.Shapes.Length}개 · {output.Milliseconds:F1} ms · 원본 창 {output.Windows}개" +
                (output.Incomplete ? $" · 검색 불완전 (경계 잘림 {output.ClippedContours}개 또는 결과/윤곽 한도)" : "");
            Results = output.Shapes.IsEmpty ? "조건에 맞는 도형이 없습니다." : string.Join("\n", output.Shapes.Select((shape, index) =>
                $"#{index + 1} {Name(shape.Kind)} · {shape.Score:F3} · X{shape.Center.X:F2} Y{shape.Center.Y:F2} · 면적 {shape.AreaPixels:F1}px²"));
            _overlay = new(Status, color, boundary, output.Shapes.Select((shape, index) => new MatchMarker(
                shape.Contour.Select(point => new PointD(point.X, point.Y)).ToImmutableArray(), new(shape.Center.X, shape.Center.Y),
                shape.AngleRadians, $"#{index + 1} {Name(shape.Kind)}", color, [])).ToImmutableArray());
            Publish();
        }
        catch (OperationCanceledException) when (combined.IsCancellationRequested)
        {
            if (!_disposed && epoch == _epoch)
            {
                PresentWarning(timeLimit.IsCancellationRequested && !cancellation.IsCancellationRequested
                    ? "시간 한도 도달 · 도형 검색 불완전 · 판정하지 않았습니다."
                    : "도형 검출 취소 · 판정하지 않았습니다.");
            }
        }
        catch (Exception error)
        {
            if (!_disposed && epoch == _epoch)
            {
                var interruption = timeLimit.IsCancellationRequested ? "시간 한도 도달 · 검색 불완전 · "
                    : cancellation.IsCancellationRequested ? "도형 검출 취소 · " : "";
                PresentWarning(interruption + "도형 검출 실패 · " + error.Message);
            }
        }
        finally
        {
            _cancellation = null;
            Publish();
        }
    }

    private bool Has(PrimitiveShapeKind kind) => (_options.Shapes & kind) != 0;
    private void SetShape(PrimitiveShapeKind kind, bool value, [CallerMemberName] string? property = null)
        => Change(_options with
        {
            Shapes = value ? _options.Shapes | kind : _options.Shapes & ~kind
        }, property);
    private void Change(PrimitiveDetectionOptions options, [CallerMemberName] string? property = null)
    {
        if (_options == options)
        {
            return;
        }
        _options = options;
        Notify(property);
        Invalidate();
    }
    private void Invalidate()
    {
        _epoch++;
        _cancellation?.Cancel();
        _overlay = null;
        LastOutput = null;
        Status = "입력 변경 · 다시 검출하세요.";
        Results = Status;
        Publish();
    }
    private void PresentWarning(string message)
    {
        Status = message;
        Results = message;
        LastOutput = null;
        _overlay = new(message, InspectionResultPresentation.Color(InspectionVerdict.Warning), [], []);
        Publish();
    }
    private void Refresh()
    {
        Notify(nameof(IsBusy));
        Notify(nameof(IsIdle));
        Notify(nameof(OptionsError));
        Detect.Refresh();
        Cancel.Refresh();
        Locate.Refresh();
    }
    private void Publish()
    {
        Notify(nameof(Status));
        Notify(nameof(Results));
        Refresh();
        Changed?.Invoke();
    }
    public static string Name(PrimitiveShapeKind kind) => kind switch
    {
        PrimitiveShapeKind.Triangle => "삼각형",
        PrimitiveShapeKind.Rectangle => "사각형",
        PrimitiveShapeKind.Circle => "원",
        PrimitiveShapeKind.Cross => "십자가",
        _ => "도형"
    };
    public void Dispose()
    {
        _disposed = true;
        Invalidate();
    }
}
