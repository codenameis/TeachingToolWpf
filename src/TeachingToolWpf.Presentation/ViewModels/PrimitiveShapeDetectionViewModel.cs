using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

public sealed class PrimitiveShapeDetectionViewModel : ObservableObject, IVisionAlgorithmPreview
{
    private const int ResultPageSize = 256;
    private readonly IPrimitiveShapeDetectionService _service;
    private PrimitiveDetectionOptions _options = new();
    private ImageReference? _image;
    private object? _lastRois;
    private object? _lastSelection;
    private RoiGeometry? _selection;
    private RoiExclusion? _selectionExclusion;
    private Guid? _selectedRoiId;
    private string? _selectedRoiName;
    private ImmutableArray<InspectionSelection> _targetRois = [];
    private bool _hasMultipleSelection;
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
    private int _resultPage;

    public PrimitiveShapeDetectionViewModel(IPrimitiveShapeDetectionService service)
    {
        _service = service;
        Detect = new(DetectAsync, error => PresentWarning(error.Message),
            () => IsIdle && _canExecute && _image is not null && (!UseSelection || _selection is not null || _targetRois.Length > 0) && OptionsError.Length == 0);
        Cancel = new(() => _cancellation?.Cancel(), () => IsBusy);
        Locate = new(() => LocateRequested?.Invoke(BoundsD.FromPoints(_overlay!.Matches.SelectMany(marker =>
            marker.Bounds.Concat(marker.AdditionalContours.SelectMany(component => component))))),
            () => IsIdle && _canExecute && _overlay?.Matches.IsEmpty == false);
        PreviousResults = new(() => ChangeResultPage(-1), () => IsIdle && LastOutput is not null && _resultPage > 0);
        NextResults = new(() => ChangeResultPage(1), () => IsIdle && LastOutput is { } output &&
            ((long)_resultPage + 1) * ResultPageSize < output.Shapes.Length);
    }

    public event Action? Changed;
    public event Action<BoundsD>? LocateRequested;
    public event Action<RoiAlgorithmAssignment>? RoiAssignmentRecorded;
    public event Action<StructuredInspectionLogEntry>? StructuredLogRecorded;
    public AsyncCommand Detect { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand Locate { get; }
    public bool IsBusy => _cancellation is not null;
    public RelayCommand PreviousResults { get; }
    public RelayCommand NextResults { get; }
    public string ResultPage => LastOutput is { Shapes.IsEmpty: false } output
        ? $"{_resultPage + 1} / {((output.Shapes.Length - 1) / ResultPageSize) + 1} 페이지 · 전체 {output.Shapes.Length:N0}개"
        : "검출 결과 없음";
    public bool IsIdle => !_disposed && !IsBusy;
    public ResultOverlay? Overlay => ShowOverlay ? _overlay : null;
    public string ParameterPreviewSummary
    {
        get
        {
            if (OptionsError.Length > 0)
            {
                return "설정 범위 확인 필요: " + OptionsError;
            }
            var thresholdText = AutomaticThreshold ? "창별 자동 Otsu 이진화" : $"수동 이진화 임계값 {Threshold}";
            var deviceText = UseGpu ? "CPU + CUDA" : "CPU 전용";
            var scopeText = UseSelection
                ? (_selection is not null ? "선택 ROI 내부" : _targetRois.Length > 0 ? $"대상 리프 ROI {_targetRois.Length}개 개별 검출" : "선택 ROI 내부")
                : "전체 이미지";
            return $"[실시간 설정 미리보기] 크기 {MinimumSize:F0}~{MaximumSize:F0} px · {thresholdText} · 근사 허용 {ApproximationTolerance:F3} · 최소 점수 {MinimumScore:F2} · {scopeText} ({deviceText})";
        }
    }
    public string Status { get; private set; } = "학습 없이 기본 도형과 PCB 정렬 마크를 검출합니다.";
    public string Results { get; private set; } = "검출 결과 없음";
    public PrimitiveDetectionOutput? LastOutput { get; private set; }
    public IReadOnlyList<PrimitivePolarityOption> Polarities { get; } =
    [
        new(ForegroundPolarity.Auto, "자동 · 작은 명암 영역"),
        new(ForegroundPolarity.Bright, "밝은 도형 / 어두운 배경"),
        new(ForegroundPolarity.Dark, "어두운 도형 / 밝은 배경")
    ];
    public bool AutomaticThreshold
    {
        get => _options.AutomaticThreshold; set => Change(_options with { AutomaticThreshold = value });
    }
    public bool UseGpu
    {
        get => _options.UseGpu; set => Change(_options with { UseGpu = value });
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
    public bool FindRings
    {
        get => Has(PrimitiveShapeKind.Ring); set => SetShape(PrimitiveShapeKind.Ring, value);
    }
    public bool FindRectangleFrames
    {
        get => Has(PrimitiveShapeKind.RectangleFrame); set => SetShape(PrimitiveShapeKind.RectangleFrame, value);
    }
    public bool FindDiamonds
    {
        get => Has(PrimitiveShapeKind.Diamond); set => SetShape(PrimitiveShapeKind.Diamond, value);
    }
    public bool FindCrossHashes
    {
        get => Has(PrimitiveShapeKind.CrossHash); set => SetShape(PrimitiveShapeKind.CrossHash, value);
    }
    public bool FindDoubleSquares
    {
        get => Has(PrimitiveShapeKind.DoubleSquare); set => SetShape(PrimitiveShapeKind.DoubleSquare, value);
    }
    public bool FindStars
    {
        get => Has(PrimitiveShapeKind.Star); set => SetShape(PrimitiveShapeKind.Star, value);
    }
    public bool FindAsterisks
    {
        get => Has(PrimitiveShapeKind.Asterisk); set => SetShape(PrimitiveShapeKind.Asterisk, value);
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

    /// <summary>문서·선택이 바뀌었으면 결과를 버리고, 아니면 화면만 다시 계산한다.</summary>
    /// <remarks>라이브 프레임마다 호출된다. 선택 해석은 <see cref="InspectionSelection"/>이 맡고 여기서는 할당을 만들지 않는다.</remarks>
    public void Update(InspectionUpdate update)
    {
        var state = update.State;
        var canExecute = update.CanExecute && update.ToolAvailable;
        var inputTransition = _inputAvailable != update.CanExecute || _canExecute != canExecute;
        if (ReferenceEquals(_lastRois, state.Rois) && ReferenceEquals(_lastSelection, state.Selection) &&
            _image == state.Image && _generation == update.Generation && _live == update.Live && !inputTransition)
        {
            return;
        }

        if (!update.CanExecute && !_live && !inputTransition && _image == state.Image && _generation == update.Generation)
        {
            var dragSelection = InspectionSelection.Resolve(state);
            _lastRois = state.Rois;
            _lastSelection = state.Selection;
            _selection = dragSelection.SearchSelection;
            _selectionExclusion = dragSelection.Exclusion;
            _selectedRoiId = dragSelection.RoiId;
            _selectedRoiName = dragSelection.RoiName;
            _targetRois = InspectionSelection.ResolveTargetLeafRois(state, update.TransformPreview);
            _hasMultipleSelection = state.Selection.Count > 1;
            if (UseSelection && (_overlay is not null || _cancellation is not null))
            {
                Invalidate();
            }
            return;
        }

        var selection = InspectionSelection.Resolve(state);
        var targetRois = InspectionSelection.ResolveTargetLeafRois(state, update.TransformPreview);
        var changed = _image != state.Image || _generation != update.Generation || _live != update.Live ||
            (UseSelection && (_selection != selection.SearchSelection || _selectionExclusion != selection.Exclusion || !ReferenceEquals(_lastSelection, state.Selection))) ||
            (_live && _inputAvailable && !update.CanExecute);
        _lastRois = state.Rois;
        _lastSelection = state.Selection;
        _image = state.Image;
        _generation = update.Generation;
        _selection = selection.SearchSelection;
        _selectionExclusion = selection.Exclusion;
        _selectedRoiId = selection.RoiId;
        _selectedRoiName = selection.RoiName;
        _targetRois = targetRois;
        _hasMultipleSelection = state.Selection.Count > 1;
        _live = update.Live;
        _inputAvailable = update.CanExecute;
        _canExecute = canExecute;
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
        if ((UseSelection && _selection is null && _targetRois.Length > 0) ||
            (!UseSelection && _hasMultipleSelection && _targetRois.Length > 1))
        {
            await DetectMultipleRoisAsync();
            return;
        }

        var image = _image!;
        var region = UseSelection ? _selection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;
        var roiId = UseSelection ? _selectedRoiId : null;
        var roiName = UseSelection ? _selectedRoiName : null;
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
            var output = await _service.DetectAsync(image, live, region, options, combined.Token, exclusion);
            combined.Token.ThrowIfCancellationRequested();
            if (_disposed || epoch != _epoch)
            {
                return;
            }
            LastOutput = output;
            var verdict = output.Incomplete
                ? InspectionVerdict.Warning
                : output.Shapes.IsEmpty ? InspectionVerdict.Fail : InspectionVerdict.Pass;
            var color = InspectionResultPresentation.Color(output.Incomplete ? InspectionVerdict.Warning : InspectionVerdict.Pass);
            Status = $"도형 {output.Shapes.Length}개 · {(output.Gpu is null ? "CPU" : "CPU + CUDA")} · {output.Milliseconds:F1} ms · 원본 창 {output.Windows}개" +
                (output.Incomplete ? $" · 검색 불완전 (경계 잘림 {output.ClippedContours}개 또는 지정 결과 한도)" : "");
            _resultPage = 0;
            UpdateResultPage();
            _overlay = new(Status, color, boundary, output.Shapes.Select((shape, index) => new MatchMarker(
                shape.Contour.Select(point => new PointD(point.X, point.Y)).ToImmutableArray(), new(shape.Center.X, shape.Center.Y),
                shape.AngleRadians, $"#{index + 1} {Name(shape.Kind)}", color, [])
            {
                AdditionalContours = shape.AdditionalContours.Select(component => component.Select(point =>
                    new PointD(point.X, point.Y)).ToImmutableArray()).ToImmutableArray(),
                InnerContours = shape.InnerContours.Select(hole => hole.Select(point =>
                    new PointD(point.X, point.Y)).ToImmutableArray()).ToImmutableArray()
            }).ToImmutableArray());
            RecordShapeDetectionResult(roiId, roiName, region, output, verdict);
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

    private async Task DetectMultipleRoisAsync()
    {
        var image = _image!;
        var targets = _targetRois;
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
        var boundary = ImmutableArray.Create(new PointD(0, 0), new PointD(image.Width, 0), new PointD(image.Width, image.Height), new PointD(0, image.Height));
        Status = $"대상 리프 ROI {targets.Length}개 개별 도형 검출 중…";
        Results = Status;
        _overlay = new(Status, new(.58f, .64f, .72f, 1), boundary, []);
        Publish();
        try
        {
            var allShapes = ImmutableArray.CreateBuilder<PrimitiveShapeDetection>();
            var allMarkers = ImmutableArray.CreateBuilder<MatchMarker>();
            var reportLines = new List<string>(targets.Length);
            double totalMs = 0.0;
            int totalWindows = 0;
            int totalClipped = 0;
            bool anyIncomplete = false;
            int passCount = 0;
            int failCount = 0;

            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                var roiId = target.RoiId;
                var roiName = string.IsNullOrWhiteSpace(target.RoiName)
                    ? (roiId is { } id ? $"ROI-{id.ToString()[..4]}" : $"ROI #{i + 1}")
                    : target.RoiName!;
                var region = target.SearchSelection ?? target.Selection;
                var exclusion = target.Exclusion;
                var output = await _service.DetectAsync(image, live, region, options, combined.Token, exclusion);
                combined.Token.ThrowIfCancellationRequested();
                if (_disposed || epoch != _epoch)
                {
                    return;
                }

                totalMs += output.Milliseconds;
                totalWindows += output.Windows;
                totalClipped += output.ClippedContours;
                anyIncomplete |= output.Incomplete;
                allShapes.AddRange(output.Shapes);

                var verdict = output.Incomplete
                    ? InspectionVerdict.Warning
                    : output.Shapes.IsEmpty ? InspectionVerdict.Fail : InspectionVerdict.Pass;
                if (verdict == InspectionVerdict.Pass)
                {
                    passCount++;
                }
                else
                {
                    failCount++;
                }

                var color = InspectionResultPresentation.Color(verdict);
                foreach (var (shape, index) in output.Shapes.Select((s, idx) => (s, idx)))
                {
                    allMarkers.Add(new MatchMarker(
                        shape.Contour.Select(point => new PointD(point.X, point.Y)).ToImmutableArray(),
                        new(shape.Center.X, shape.Center.Y),
                        shape.AngleRadians,
                        $"{roiName} #{index + 1} {Name(shape.Kind)}",
                        color,
                        [])
                    {
                        AdditionalContours = shape.AdditionalContours.Select(component => component.Select(point =>
                            new PointD(point.X, point.Y)).ToImmutableArray()).ToImmutableArray(),
                        InnerContours = shape.InnerContours.Select(hole => hole.Select(point =>
                            new PointD(point.X, point.Y)).ToImmutableArray()).ToImmutableArray()
                    });
                }

                if (output.Shapes.IsEmpty && region is not null)
                {
                    allMarkers.Add(new MatchMarker(
                        region.Outline.ToImmutableArray(),
                        region.Center,
                        region.Angle * Math.PI / 180.0,
                        $"{roiName}: 미검출",
                        color,
                        []));
                }

                var topShapeText = output.Shapes.IsEmpty
                    ? "도형 미검출"
                    : $"{Name(output.Shapes[0].Kind)} 외 {output.Shapes.Length}개 (최고점수 {output.Shapes[0].Score:F3})";
                reportLines.Add($"[{roiName}] {(output.Shapes.IsEmpty ? "NG" : "PASS")} · {topShapeText} · {output.Milliseconds:F1} ms");
                RecordShapeDetectionResult(roiId, roiName, region, output, verdict);
            }

            var mergedShapes = allShapes.ToImmutable();
            LastOutput = new PrimitiveDetectionOutput(mergedShapes, totalWindows, anyIncomplete, totalClipped, totalMs);
            var summaryColor = InspectionResultPresentation.Color(failCount > 0 ? InspectionVerdict.Fail : anyIncomplete ? InspectionVerdict.Warning : InspectionVerdict.Pass);
            Status = $"개별 ROI 도형 검출 ({targets.Length}개) · PASS {passCount} / NG {failCount} · 총 도형 {mergedShapes.Length}개 · {totalMs:F1} ms";
            Results = string.Join("\n", reportLines);
            _resultPage = 0;
            _overlay = new(Status, summaryColor, boundary, allMarkers.ToImmutable());
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
                PresentWarning("도형 검출 실패 · " + error.Message);
            }
        }
        finally
        {
            _cancellation = null;
            Publish();
        }
    }

    private void RecordShapeDetectionResult(
        Guid? roiId,
        string? roiName,
        RoiGeometry? region,
        PrimitiveDetectionOutput output,
        InspectionVerdict verdict)
    {
        var badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.PrimitiveShapes);
        var accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.PrimitiveShapes);
        string verdictBadge = verdict == InspectionVerdict.Pass ? "PASS" : verdict == InspectionVerdict.Warning ? "WARN" : "NG";
        string verdictAccent = verdict == InspectionVerdict.Pass ? "#34D399" : verdict == InspectionVerdict.Warning ? "#FBBF24" : "#F87171";
        string resolvedName = string.IsNullOrWhiteSpace(roiName)
            ? (roiId is { } id ? $"ROI-{id.ToString()[..4]}" : "전체 영상")
            : roiName;
        string scoreSummary = output.Shapes.IsEmpty
            ? "도형 0개"
            : $"{Name(output.Shapes[0].Kind)} 등 {output.Shapes.Length}개 (점수 {output.Shapes[0].Score:F3})";
        string poseSummary = output.Shapes.IsEmpty
            ? "검출 없음"
            : $"X={output.Shapes[0].Center.X:F1}, Y={output.Shapes[0].Center.Y:F1}";
        BoundsD? bounds = region is not null ? BoundsD.FromPoints(region.Outline) : null;

        if (roiId is { } targetId)
        {
            RoiAssignmentRecorded?.Invoke(new RoiAlgorithmAssignment(
                RoiId: targetId,
                RoiName: resolvedName,
                Tool: InspectionToolKind.PrimitiveShapes,
                PatternMethod: null,
                SuiteAlgorithm: null,
                AlgorithmBadge: badge,
                AlgorithmAccent: accent,
                ParameterSummary: $"크기 {MinimumSize:F0}~{MaximumSize:F0}px · 최소점수 {MinimumScore:F2}",
                VerdictBadge: verdictBadge,
                VerdictAccent: verdictAccent,
                LastResultSummary: $"{verdictBadge} · {scoreSummary} ({output.Milliseconds:F1} ms)"));
        }

        StructuredLogRecorded?.Invoke(new StructuredInspectionLogEntry(
            EntryId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.Now,
            RoiId: roiId,
            RoiName: resolvedName,
            Tool: InspectionToolKind.PrimitiveShapes,
            AlgorithmBadge: badge,
            Verdict: verdict,
            VerdictBadge: verdictBadge,
            VerdictAccent: verdictAccent,
            ScoreSummary: scoreSummary,
            PoseSummary: poseSummary,
            ElapsedMilliseconds: output.Milliseconds,
            TargetBounds: bounds));
    }

    private void ChangeResultPage(int delta)
    {
        _resultPage += delta;
        UpdateResultPage();
        Publish();
    }

    private void UpdateResultPage()
    {
        Results = LastOutput is not { Shapes.IsEmpty: false } output ? "조건에 맞는 도형이 없습니다."
            : string.Join("\n", output.Shapes.Skip(_resultPage * ResultPageSize).Take(ResultPageSize).Select((shape, index) =>
                $"#{(_resultPage * ResultPageSize) + index + 1} {Name(shape.Kind)} · {shape.Score:F3} · X{shape.Center.X:F2} Y{shape.Center.Y:F2} · 면적 {shape.AreaPixels:F1}px²"));
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
        Notify(nameof(ParameterPreviewSummary));
        Detect.Refresh();
        Cancel.Refresh();
        Locate.Refresh();
        PreviousResults.Refresh();
        NextResults.Refresh();
    }
    private void Publish()
    {
        Notify(nameof(Status));
        Notify(nameof(Results));
        Notify(nameof(ResultPage));
        Refresh();
        Changed?.Invoke();
    }
    public static string Name(PrimitiveShapeKind kind) => kind switch
    {
        PrimitiveShapeKind.Triangle => "삼각형",
        PrimitiveShapeKind.Rectangle => "사각형",
        PrimitiveShapeKind.Circle => "원",
        PrimitiveShapeKind.Cross => "십자가",
        PrimitiveShapeKind.Ring => "도넛",
        PrimitiveShapeKind.RectangleFrame => "사각 테두리",
        PrimitiveShapeKind.Diamond => "마름모",
        PrimitiveShapeKind.CrossHash => "# 마크",
        PrimitiveShapeKind.DoubleSquare => "더블스퀘어",
        PrimitiveShapeKind.Star => "별 (5각)",
        PrimitiveShapeKind.Asterisk => "별표 (6·8방향)",
        _ => "도형"
    };
    public void Dispose()
    {
        _disposed = true;
        Invalidate();
    }
}
