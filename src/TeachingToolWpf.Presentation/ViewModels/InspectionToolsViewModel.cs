using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Media;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.Models;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public enum StructuredLogFilterKind
{
    All,
    PassOnly,
    FailOrWarnOnly,
    SelectedRoiOnly
}

/// <summary>검사 도구 선택, ROI별 적용 알고리즘 동기화, 다중/전체 ROI 일괄 개별 실행 및 구조화 검사 로그를 관리한다.</summary>
public sealed class InspectionToolsViewModel : ObservableObject, IDisposable
{
    private const int MaxStructuredLogCount = 512;

    private readonly IReadOnlyDictionary<InspectionToolKind, IVisionAlgorithmPreview> _previews;
    private readonly Dictionary<Guid, RoiAlgorithmAssignment> _roiAssignments = [];
    private readonly List<StructuredInspectionLogEntry> _structuredLogs = [];
    private InspectionToolKind _selected;
    private DocumentState? _state;
    private long _generation;
    private bool _live;
    private bool _canExecute;
    private RoiTransformPreview? _transformPreview;
    private Guid? _lastSynchronizedSingleRoiId;
    private StructuredLogFilterKind _logFilter = StructuredLogFilterKind.All;
    private StructuredInspectionLogEntry? _selectedLogEntry;
    private bool _updating;
    private bool _disposed;

    public InspectionToolsViewModel(
        ShapeMatchingViewModel pattern,
        PrimitiveShapeDetectionViewModel shapes,
        EdgeFewShotViewModel? fewShot = null,
        InspectionSuiteViewModel? suite = null)
    {
        Pattern = pattern;
        Shapes = shapes;
        FewShot = fewShot;
        Suite = suite;
        var map = new Dictionary<InspectionToolKind, IVisionAlgorithmPreview>
        {
            [InspectionToolKind.Pattern] = pattern,
            [InspectionToolKind.PrimitiveShapes] = shapes
        };
        var tools = new List<InspectionToolOption>
        {
            new(InspectionToolKind.Pattern, "학습 패턴 찾기"),
            new(InspectionToolKind.PrimitiveShapes, "일반 도형 찾기 · 학습 없음")
        };
        if (fewShot is not null)
        {
            map[InspectionToolKind.EdgeFewShot] = fewShot;
            tools.Add(new(InspectionToolKind.EdgeFewShot, "소량 샘플 분류 · 이상 탐지 (Few-Shot)"));
        }
        if (suite is not null)
        {
            map[InspectionToolKind.InspectionSuite] = suite;
            tools.Add(new(InspectionToolKind.InspectionSuite, "상용 정밀 검사 스위트 7종 (Inspection Suite)"));
        }
        _previews = map;
        Tools = tools;

        foreach (var preview in _previews.Values)
        {
            preview.Changed += OnChanged;
            preview.PropertyChanged += OnExecutionChanged;
        }

        pattern.RoiAssignmentRecorded += OnRoiAssignmentRecorded;
        pattern.StructuredLogRecorded += OnStructuredLogRecorded;
        shapes.RoiAssignmentRecorded += OnRoiAssignmentRecorded;
        shapes.StructuredLogRecorded += OnStructuredLogRecorded;
        if (fewShot is not null)
        {
            fewShot.RoiAssignmentRecorded += OnRoiAssignmentRecorded;
            fewShot.StructuredLogRecorded += OnStructuredLogRecorded;
        }
        if (suite is not null)
        {
            suite.RoiAssignmentRecorded += OnRoiAssignmentRecorded;
            suite.StructuredLogRecorded += OnStructuredLogRecorded;
        }

        AssignCurrentAlgorithmToSelectedRois = new RelayCommand(
            AssignActiveToolToTargetRois,
            () => IsIdle && HasAssignableTargetRois());
        ExecuteActiveToolOnRois = new AsyncCommand(
            ExecuteActiveToolOnTargetRoisAsync,
            _ => { },
            CanExecuteActiveToolOnTargetRois);
        FilterAllLogs = new RelayCommand(() => SetLogFilter(StructuredLogFilterKind.All));
        FilterPassLogs = new RelayCommand(() => SetLogFilter(StructuredLogFilterKind.PassOnly));
        FilterFailWarnLogs = new RelayCommand(() => SetLogFilter(StructuredLogFilterKind.FailOrWarnOnly));
        FilterSelectedRoiLogs = new RelayCommand(() => SetLogFilter(StructuredLogFilterKind.SelectedRoiOnly));
        ClearStructuredLogs = new RelayCommand(ClearAllStructuredLogs, () => _structuredLogs.Count > 0);
        LocateSelectedLogEntry = new RelayCommand(
            () =>
            {
                if (_selectedLogEntry?.TargetBounds is { } bounds)
                {
                    LocateBoundsRequested?.Invoke(bounds);
                }
            },
            () => _selectedLogEntry?.TargetBounds is not null);
    }

    public event Action? Changed;
    public event Action? AssignmentsChanged;
    public event Action<BoundsD>? LocateBoundsRequested;

    public ShapeMatchingViewModel Pattern { get; }
    public PrimitiveShapeDetectionViewModel Shapes { get; }
    public EdgeFewShotViewModel? FewShot { get; }
    public InspectionSuiteViewModel? Suite { get; }
    public IVisionAlgorithmPreview ActivePreview => _previews[_selected];
    public IReadOnlyList<InspectionToolOption> Tools { get; }
    public IReadOnlyDictionary<Guid, RoiAlgorithmAssignment> RoiAssignments => _roiAssignments;
    public ObservableCollection<StructuredInspectionLogEntry> FilteredLogs { get; } = [];

    public RelayCommand AssignCurrentAlgorithmToSelectedRois { get; }
    public AsyncCommand ExecuteActiveToolOnRois { get; }
    public RelayCommand FilterAllLogs { get; }
    public RelayCommand FilterPassLogs { get; }
    public RelayCommand FilterFailWarnLogs { get; }
    public RelayCommand FilterSelectedRoiLogs { get; }
    public RelayCommand ClearStructuredLogs { get; }
    public RelayCommand LocateSelectedLogEntry { get; }

    public bool IsIdle => _previews.Values.All(preview => preview.IsIdle);
    public bool ShowPatterns => Selected == InspectionToolKind.Pattern;
    public bool ShowShapes => Selected == InspectionToolKind.PrimitiveShapes;
    public bool ShowFewShot => Selected == InspectionToolKind.EdgeFewShot;
    public bool ShowSuite => Selected == InspectionToolKind.InspectionSuite;
    public ResultOverlay? Overlay => ActivePreview.Overlay;
    public FilterPreviewResult? FilterPreview => ActivePreview.FilterPreview;

    public StructuredLogFilterKind LogFilter => _logFilter;
    public bool IsLogFilterAll => _logFilter == StructuredLogFilterKind.All;
    public bool IsLogFilterPass => _logFilter == StructuredLogFilterKind.PassOnly;
    public bool IsLogFilterFailWarn => _logFilter == StructuredLogFilterKind.FailOrWarnOnly;
    public bool IsLogFilterSelectedRoi => _logFilter == StructuredLogFilterKind.SelectedRoiOnly;

    public int TotalLogCount => _structuredLogs.Count;
    public int PassLogCount => _structuredLogs.Count(x => x.Verdict is InspectionVerdict.Pass or InspectionVerdict.Review);
    public int FailWarnLogCount => _structuredLogs.Count(x => x.Verdict is InspectionVerdict.Fail or InspectionVerdict.Warning);
    public string LogSummaryText => _structuredLogs.Count == 0
        ? "검사 실행 시 ROI별 판정·점수·좌표·소요시간이 실시간 기록됩니다."
        : $"총 {TotalLogCount}건 · PASS {PassLogCount} · NG/WARN {FailWarnLogCount} (표시 {FilteredLogs.Count}건)";

    public StructuredInspectionLogEntry? SelectedLogEntry
    {
        get => _selectedLogEntry;
        set
        {
            if (Set(ref _selectedLogEntry, value))
            {
                LocateSelectedLogEntry.Refresh();
                if (value?.TargetBounds is { } bounds)
                {
                    LocateBoundsRequested?.Invoke(bounds);
                }
            }
        }
    }

    public string SelectedRoiHeaderText { get; private set; } = "선택된 ROI 없음 · 검사 시 전체 리프 ROI 개별 실행";
    public bool SelectedRoiHasAlgorithm { get; private set; }
    public string SelectedRoiAlgorithmBadge { get; private set; } = "미지정 (우측 알고리즘 선택 후 할당/실행)";
    public string SelectedRoiAlgorithmAccent { get; private set; } = "#94A3B8";
    public string SelectedRoiParameterSummary { get; private set; } = "ROI를 클릭하면 해당 ROI에 설정된 검사 알고리즘과 최근 판정이 즉시 동기화됩니다.";
    public string SelectedRoiVerdictBadge { get; private set; } = "";
    public string SelectedRoiVerdictAccent { get; private set; } = "#94A3B8";
    public bool SelectedRoiHasVerdict => !string.IsNullOrEmpty(SelectedRoiVerdictBadge);
    public string SelectedRoiLastResultSummary { get; private set; } = "";
    public ImageSource? SelectedRoiPatternThumbnail { get; private set; }
    public bool SelectedRoiHasPatternThumbnail => SelectedRoiPatternThumbnail is not null;

    /// <summary>검사 자체의 영역 범위 옵션 (true: 선택/대상 ROI 영역 검사, false: 전체 이미지 검사).</summary>
    public bool UseSelection
    {
        get => _selected == InspectionToolKind.PrimitiveShapes ? Shapes.UseSelection : Pattern.UseSelection;
        set
        {
            Pattern.UseSelection = value;
            Shapes.UseSelection = value;
            Notify(nameof(UseSelection));
            Notify(nameof(SearchFullImage));
            Notify(nameof(SearchScopeSummaryText));
            RefreshQuickActions();
        }
    }

    /// <summary>검사 자체의 전체 이미지 검사 옵션 (true: 전체 이미지 전역 검사, false: ROI 영역 검사).</summary>
    public bool SearchFullImage
    {
        get => !UseSelection;
        set => UseSelection = !value;
    }

    public string SearchScopeSummaryText
    {
        get
        {
            if (SearchFullImage)
            {
                return _state?.Image is { } image
                    ? $"현재 범위: 전체 이미지 전역 검사 ({image.Width:N0} × {image.Height:N0} px)"
                    : "현재 범위: 전체 이미지 전역 검사 (이미지를 열어 주세요)";
            }

            if (_state is null)
            {
                return "현재 범위: 선택 ROI 검색 영역";
            }

            var resolved = InspectionSelection.Resolve(_state, _transformPreview);
            if (resolved.SearchSelection is { } roi)
            {
                string roiTitle = string.IsNullOrWhiteSpace(resolved.RoiName) ? roi.ShapeName : resolved.RoiName!;
                return $"현재 범위: 선택 ROI 검색 영역 ({roiTitle} · {roi.Width:F0} × {roi.Height:F0} px)";
            }

            var targets = InspectionSelection.ResolveTargetLeafRois(_state, _transformPreview);
            if (targets.Length > 0)
            {
                return $"현재 범위: 대상 리프 ROI {targets.Length}개 각자 개별 영역 검사";
            }

            return "현재 범위: ROI 영역 검사 (검사할 ROI를 선택하거나 상단 ‘전체 이미지 검사’를 켜세요)";
        }
    }

    public string QuickInspectLabel
    {
        get
        {
            if (_state is null)
            {
                return "⚡ ROI 개별 검사 실행";
            }
            if (SearchFullImage && _selected is InspectionToolKind.Pattern or InspectionToolKind.PrimitiveShapes)
            {
                return "⚡ 전체 이미지 검사 실행";
            }
            var targets = InspectionSelection.ResolveTargetLeafRois(_state, _transformPreview);
            if (_state.Selection.Count == 1 && targets.Length == 1)
            {
                return $"⚡ 선택 ROI ({targets[0].RoiName}) 검사 실행";
            }
            if (_state.Selection.Count > 1 && targets.Length > 0)
            {
                return $"⚡ 선택 ROI {targets.Length}개 각자 검사 실행";
            }
            if (_state.Selection.Count == 0 && targets.Length > 0)
            {
                return $"⚡ 전체 ROI {targets.Length}개 각자 검사 실행";
            }
            return "⚡ 검사 실행";
        }
    }

    public InspectionToolKind Selected
    {
        get => _selected;
        set
        {
            if (!Enum.IsDefined(value) || !_previews.ContainsKey(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (IsIdle && Set(ref _selected, value))
            {
                Notify(nameof(ShowPatterns));
                Notify(nameof(ShowShapes));
                Notify(nameof(ShowFewShot));
                Notify(nameof(ShowSuite));
                Notify(nameof(ActivePreview));
                Notify(nameof(UseSelection));
                Notify(nameof(SearchFullImage));
                Notify(nameof(SearchScopeSummaryText));
                UpdateAvailability();
                RefreshQuickActions();
                OnChanged();
            }
        }
    }

    public void Update(DocumentState state, long generation, bool live, bool canExecute, RoiTransformPreview? transformPreview = null)
    {
        bool roisChanged = _state is null || !ReferenceEquals(_state.Rois, state.Rois);
        bool selectionChanged = _state is null || !ReferenceEquals(_state.Selection, state.Selection);
        _state = state;
        _generation = generation;
        _live = live;
        _canExecute = canExecute;
        _transformPreview = transformPreview;

        if (roisChanged)
        {
            PruneDeletedRoiAssignments(state);
        }

        if (selectionChanged || roisChanged)
        {
            SynchronizeSelectedRoiInspectionState(state, selectionChanged);
        }

        UpdateAvailability();
        RefreshQuickActions();
    }

    public void RecordPresetSuiteAssignments(
        DocumentState state,
        IReadOnlyList<Guid> targetIds,
        InspectionSuiteAlgorithmKind algorithm)
    {
        string badge = RoiAlgorithmAssignment.FormatAlgorithmBadge(InspectionToolKind.InspectionSuite, null, algorithm);
        string accent = RoiAlgorithmAssignment.GetAlgorithmAccent(InspectionToolKind.InspectionSuite, null, algorithm);
        string paramSummary = Suite?.ParameterPreviewSummary ?? InspectionSuiteService.GetAlgorithmShortName(algorithm);
        bool anyAdded = false;
        foreach (var id in targetIds)
        {
            if (state.Rois.TryGetValue(id, out var roi) && !state.IsGroupRoi(id))
            {
                _roiAssignments[id] = new RoiAlgorithmAssignment(
                    RoiId: id,
                    RoiName: roi.Name,
                    Tool: InspectionToolKind.InspectionSuite,
                    PatternMethod: null,
                    SuiteAlgorithm: algorithm,
                    AlgorithmBadge: badge,
                    AlgorithmAccent: accent,
                    ParameterSummary: paramSummary,
                    VerdictBadge: "대기",
                    VerdictAccent: "#38BDF8",
                    LastResultSummary: $"{badge} 검사 대상 ROI");
                anyAdded = true;
            }
        }

        if (anyAdded)
        {
            SynchronizeSelectedRoiInspectionState(state, selectionChanged: false);
            AssignmentsChanged?.Invoke();
        }
    }

    public string FormatLogsForClipboard()
    {
        if (FilteredLogs.Count == 0)
        {
            return "기록된 검사 로그가 없습니다.";
        }
        var sb = new StringBuilder();
        foreach (var entry in FilteredLogs)
        {
            sb.AppendLine(entry.OneLineSummary);
        }
        return sb.ToString().TrimEnd();
    }

    private void SynchronizeSelectedRoiInspectionState(DocumentState state, bool selectionChanged)
    {
        if (state.Selection.TryGetOnly(out var singleRoiId) && state.Rois.TryGetValue(singleRoiId, out var roi))
        {
            string roiTitle = string.IsNullOrWhiteSpace(roi.Name) ? $"ROI-{singleRoiId.ToString()[..4]}" : roi.Name;
            if (_roiAssignments.TryGetValue(singleRoiId, out var assignment))
            {
                if (selectionChanged && singleRoiId != _lastSynchronizedSingleRoiId && IsIdle && _previews.ContainsKey(assignment.Tool))
                {
                    _lastSynchronizedSingleRoiId = singleRoiId;
                    if (_selected != assignment.Tool)
                    {
                        Selected = assignment.Tool;
                    }
                    if (assignment.Tool == InspectionToolKind.Pattern && assignment.PatternMethod is { } pm && Pattern.Method != pm)
                    {
                        Pattern.Method = pm;
                    }
                    else if (assignment.Tool == InspectionToolKind.InspectionSuite && Suite is not null && assignment.SuiteAlgorithm is { } sa && Suite.SelectedAlgorithm != sa)
                    {
                        Suite.SelectedAlgorithm = sa;
                    }

                    if (assignment.Tool == InspectionToolKind.Pattern &&
                        Pattern.ActiveMethodPatterns.LastOrDefault(p => p.TeachingRoiId == singleRoiId) is { } matchedPattern &&
                        !ReferenceEquals(Pattern.SelectedRegisteredPattern, matchedPattern))
                    {
                        Pattern.SelectedRegisteredPattern = matchedPattern;
                    }
                }

                SelectedRoiHeaderText = $"선택 ROI: {roiTitle}";
                SelectedRoiHasAlgorithm = true;
                SelectedRoiAlgorithmBadge = assignment.AlgorithmBadge;
                SelectedRoiAlgorithmAccent = assignment.AlgorithmAccent;
                SelectedRoiParameterSummary = assignment.ParameterSummary;
                SelectedRoiVerdictBadge = assignment.VerdictBadge;
                SelectedRoiVerdictAccent = assignment.VerdictAccent;
                SelectedRoiLastResultSummary = assignment.LastResultSummary;
                SelectedRoiPatternThumbnail = assignment.PatternThumbnail ??
                    Pattern.ActiveMethodPatterns.LastOrDefault(p => p.TeachingRoiId == singleRoiId)?.ThumbnailImage;
            }
            else
            {
                _lastSynchronizedSingleRoiId = singleRoiId;
                string activeBadge = GetActiveAlgorithmBadge();
                SelectedRoiHeaderText = $"선택 ROI: {roiTitle}";
                SelectedRoiHasAlgorithm = false;
                SelectedRoiAlgorithmBadge = $"미할당 (현재 도구: {activeBadge})";
                SelectedRoiAlgorithmAccent = "#94A3B8";
                SelectedRoiParameterSummary = state.IsGroupRoi(singleRoiId)
                    ? "그룹 ROI입니다. 하위 리프 ROI를 선택하거나 전체/다중 검사를 실행하세요."
                    : "아직 이 ROI에 지정된 알고리즘이 없습니다. '현재 알고리즘 할당' 또는 '검사 실행' 시 자동 기록됩니다.";
                SelectedRoiVerdictBadge = "";
                SelectedRoiVerdictAccent = "#94A3B8";
                SelectedRoiLastResultSummary = "";
                SelectedRoiPatternThumbnail = Pattern.ActiveMethodPatterns.LastOrDefault(p => p.TeachingRoiId == singleRoiId)?.ThumbnailImage;
            }
        }
        else
        {
            _lastSynchronizedSingleRoiId = null;
            SelectedRoiPatternThumbnail = null;
            var targets = InspectionSelection.ResolveTargetLeafRois(state, _transformPreview);
            int assignedCount = targets.Count(t => t.RoiId is { } id && _roiAssignments.ContainsKey(id));
            if (state.Selection.Count > 1)
            {
                SelectedRoiHeaderText = $"다중 선택 ROI: 리프 {targets.Length}개 (알고리즘 지정 {assignedCount}개)";
                SelectedRoiHasAlgorithm = assignedCount > 0;
                SelectedRoiAlgorithmBadge = $"다중 ROI {targets.Length}개 개별 검사 모드";
                SelectedRoiAlgorithmAccent = "#38BDF8";
                SelectedRoiParameterSummary = "검사 실행 버튼을 누르면 선택된 각 리프 ROI마다 개별 검사를 수행하고 판정을 표시합니다.";
                SelectedRoiVerdictBadge = assignedCount > 0 ? $"{assignedCount}/{targets.Length} 지정됨" : "";
                SelectedRoiVerdictAccent = "#38BDF8";
                SelectedRoiLastResultSummary = "";
            }
            else if (targets.Length > 0)
            {
                SelectedRoiHeaderText = $"선택된 ROI 없음 · 문서 리프 ROI {targets.Length}개 (알고리즘 지정 {assignedCount}개)";
                SelectedRoiHasAlgorithm = assignedCount > 0;
                SelectedRoiAlgorithmBadge = $"전체 ROI {targets.Length}개 개별 검사 대기";
                SelectedRoiAlgorithmAccent = "#2DD4BF";
                SelectedRoiParameterSummary = "아무 ROI도 선택하지 않고 검사 실행을 누르면 문서의 모든 리프 ROI를 각자 검사합니다.";
                SelectedRoiVerdictBadge = assignedCount > 0 ? $"{assignedCount}/{targets.Length} 지정됨" : "";
                SelectedRoiVerdictAccent = "#2DD4BF";
                SelectedRoiLastResultSummary = "";
            }
            else
            {
                SelectedRoiHeaderText = "등록된 리프 ROI 없음";
                SelectedRoiHasAlgorithm = false;
                SelectedRoiAlgorithmBadge = "ROI 없음";
                SelectedRoiAlgorithmAccent = "#64748B";
                SelectedRoiParameterSummary = "캔버스 상단 도구로 사각형·원·다각형 ROI를 먼저 그려 주세요.";
                SelectedRoiVerdictBadge = "";
                SelectedRoiVerdictAccent = "#64748B";
                SelectedRoiLastResultSummary = "";
            }
        }

        if (_logFilter == StructuredLogFilterKind.SelectedRoiOnly)
        {
            RebuildFilteredLogs();
        }

        NotifySelectedRoiProperties();
    }

    private void PruneDeletedRoiAssignments(DocumentState state)
    {
        if (_roiAssignments.Count == 0)
        {
            return;
        }
        var removed = _roiAssignments.Keys.Where(id => !state.Rois.ContainsKey(id)).ToList();
        if (removed.Count == 0)
        {
            return;
        }
        foreach (var id in removed)
        {
            _roiAssignments.Remove(id);
        }
        AssignmentsChanged?.Invoke();
    }

    private void AssignActiveToolToTargetRois()
    {
        if (_state is null)
        {
            return;
        }
        var targets = InspectionSelection.ResolveTargetLeafRois(_state, _transformPreview);
        if (targets.IsEmpty)
        {
            return;
        }

        string badge = GetActiveAlgorithmBadge();
        string accent = GetActiveAlgorithmAccent();
        string paramSummary = GetActiveParameterSummary();
        PatternMatchingMethod? patternMethod = _selected == InspectionToolKind.Pattern ? Pattern.Method : null;
        InspectionSuiteAlgorithmKind? suiteAlgorithm = _selected == InspectionToolKind.InspectionSuite ? Suite?.SelectedAlgorithm : null;

        foreach (var target in targets)
        {
            if (target.RoiId is not { } id)
            {
                continue;
            }
            string name = string.IsNullOrWhiteSpace(target.RoiName) ? $"ROI-{id.ToString()[..4]}" : target.RoiName!;
            _roiAssignments[id] = new RoiAlgorithmAssignment(
                RoiId: id,
                RoiName: name,
                Tool: _selected,
                PatternMethod: patternMethod,
                SuiteAlgorithm: suiteAlgorithm,
                AlgorithmBadge: badge,
                AlgorithmAccent: accent,
                ParameterSummary: paramSummary,
                VerdictBadge: "할당됨",
                VerdictAccent: "#38BDF8",
                LastResultSummary: $"{badge} 수동 할당 완료 · 검사 대기");
        }

        SynchronizeSelectedRoiInspectionState(_state, selectionChanged: false);
        AssignmentsChanged?.Invoke();
    }

    private bool HasAssignableTargetRois()
    {
        if (_state is null)
        {
            return false;
        }
        return !InspectionSelection.ResolveTargetLeafRois(_state, _transformPreview).IsEmpty;
    }

    private bool CanExecuteActiveToolOnTargetRois()
    {
        if (!IsIdle || !_canExecute || _state?.Image is null)
        {
            return false;
        }
        var targets = InspectionSelection.ResolveTargetLeafRois(_state, _transformPreview);
        bool requiresRoiTargets = UseSelection || _selected is InspectionToolKind.EdgeFewShot or InspectionToolKind.InspectionSuite;
        if (requiresRoiTargets && targets.IsEmpty)
        {
            return false;
        }

        return _selected switch
        {
            InspectionToolKind.Pattern => Pattern.HasModel && Pattern.HasEnabledRegisteredPatterns && !Pattern.IsTrainingDirty && Pattern.OptionsError.Length == 0,
            InspectionToolKind.PrimitiveShapes => Shapes.OptionsError.Length == 0,
            InspectionToolKind.EdgeFewShot => FewShot is { HasTrainedModel: true } && FewShot.OptionsError.Length == 0,
            InspectionToolKind.InspectionSuite => Suite is not null && Suite.OptionsError.Length == 0,
            _ => false
        };
    }

    private async Task ExecuteActiveToolOnTargetRoisAsync()
    {
        if (_state is null)
        {
            return;
        }

        switch (_selected)
        {
            case InspectionToolKind.Pattern:
                if (Pattern.Search.CanExecute(null))
                {
                    await Pattern.Search.ExecuteAsync();
                }
                break;

            case InspectionToolKind.PrimitiveShapes:
                if (Shapes.Detect.CanExecute(null))
                {
                    await Shapes.Detect.ExecuteAsync();
                }
                break;

            case InspectionToolKind.EdgeFewShot:
                if (FewShot is not null)
                {
                    if (UseSelection && _state.Selection.Count > 0 && FewShot.ClassifySelected.CanExecute(null))
                    {
                        await FewShot.ClassifySelected.ExecuteAsync();
                    }
                    else if (FewShot.ClassifyAll.CanExecute(null))
                    {
                        await FewShot.ClassifyAll.ExecuteAsync();
                    }
                }
                break;

            case InspectionToolKind.InspectionSuite:
                if (Suite is not null)
                {
                    if (UseSelection && _state.Selection.Count > 0 && Suite.ExecuteSelected.CanExecute(null))
                    {
                        await Suite.ExecuteSelected.ExecuteAsync();
                    }
                    else if (Suite.ExecuteAllRois.CanExecute(null))
                    {
                        await Suite.ExecuteAllRois.ExecuteAsync();
                    }
                }
                break;
        }
    }

    private void OnRoiAssignmentRecorded(RoiAlgorithmAssignment assignment)
    {
        _roiAssignments[assignment.RoiId] = assignment;
        if (_state is not null)
        {
            SynchronizeSelectedRoiInspectionState(_state, selectionChanged: false);
        }
        AssignmentsChanged?.Invoke();
    }

    private void OnStructuredLogRecorded(StructuredInspectionLogEntry entry)
    {
        if (_structuredLogs.Count >= MaxStructuredLogCount)
        {
            _structuredLogs.RemoveAt(0);
        }
        _structuredLogs.Add(entry);
        RebuildFilteredLogs();
        ClearStructuredLogs.Refresh();
        NotifyLogCounters();
    }

    private void SetLogFilter(StructuredLogFilterKind filter)
    {
        if (Set(ref _logFilter, filter, nameof(LogFilter)))
        {
            Notify(nameof(IsLogFilterAll));
            Notify(nameof(IsLogFilterPass));
            Notify(nameof(IsLogFilterFailWarn));
            Notify(nameof(IsLogFilterSelectedRoi));
            RebuildFilteredLogs();
            NotifyLogCounters();
        }
    }

    private void ClearAllStructuredLogs()
    {
        _structuredLogs.Clear();
        FilteredLogs.Clear();
        SelectedLogEntry = null;
        ClearStructuredLogs.Refresh();
        NotifyLogCounters();
    }

    private void RebuildFilteredLogs()
    {
        FilteredLogs.Clear();
        for (int i = _structuredLogs.Count - 1; i >= 0; i--)
        {
            var entry = _structuredLogs[i];
            if (MatchesLogFilter(entry))
            {
                FilteredLogs.Add(entry);
            }
        }
    }

    private bool MatchesLogFilter(StructuredInspectionLogEntry entry) => _logFilter switch
    {
        StructuredLogFilterKind.PassOnly => entry.Verdict is InspectionVerdict.Pass or InspectionVerdict.Review,
        StructuredLogFilterKind.FailOrWarnOnly => entry.Verdict is InspectionVerdict.Fail or InspectionVerdict.Warning,
        StructuredLogFilterKind.SelectedRoiOnly => _state is not null && entry.RoiId is { } id && _state.Selection.Contains(id),
        _ => true
    };

    private string GetActiveAlgorithmBadge() => RoiAlgorithmAssignment.FormatAlgorithmBadge(
        _selected,
        _selected == InspectionToolKind.Pattern ? Pattern.Method : null,
        _selected == InspectionToolKind.InspectionSuite ? Suite?.SelectedAlgorithm : null);

    private string GetActiveAlgorithmAccent() => RoiAlgorithmAssignment.GetAlgorithmAccent(
        _selected,
        _selected == InspectionToolKind.Pattern ? Pattern.Method : null,
        _selected == InspectionToolKind.InspectionSuite ? Suite?.SelectedAlgorithm : null);

    private string GetActiveParameterSummary() => _selected switch
    {
        InspectionToolKind.Pattern => Pattern.ParameterPreviewSummary,
        InspectionToolKind.PrimitiveShapes => Shapes.ParameterPreviewSummary,
        InspectionToolKind.EdgeFewShot => FewShot?.ParameterPreviewSummary ?? "",
        InspectionToolKind.InspectionSuite => Suite?.ParameterPreviewSummary ?? "",
        _ => ""
    };

    private void NotifySelectedRoiProperties()
    {
        Notify(nameof(SelectedRoiHeaderText));
        Notify(nameof(SelectedRoiHasAlgorithm));
        Notify(nameof(SelectedRoiAlgorithmBadge));
        Notify(nameof(SelectedRoiAlgorithmAccent));
        Notify(nameof(SelectedRoiParameterSummary));
        Notify(nameof(SelectedRoiVerdictBadge));
        Notify(nameof(SelectedRoiVerdictAccent));
        Notify(nameof(SelectedRoiHasVerdict));
        Notify(nameof(SelectedRoiLastResultSummary));
        Notify(nameof(SelectedRoiPatternThumbnail));
        Notify(nameof(SelectedRoiHasPatternThumbnail));
        Notify(nameof(SearchScopeSummaryText));
        Notify(nameof(QuickInspectLabel));
    }

    private void NotifyLogCounters()
    {
        Notify(nameof(TotalLogCount));
        Notify(nameof(PassLogCount));
        Notify(nameof(FailWarnLogCount));
        Notify(nameof(LogSummaryText));
    }

    private void RefreshQuickActions()
    {
        AssignCurrentAlgorithmToSelectedRois.Refresh();
        ExecuteActiveToolOnRois.Refresh();
        Notify(nameof(QuickInspectLabel));
    }

    private void UpdateAvailability()
    {
        if (_disposed || _updating || _state is null)
        {
            return;
        }
        _updating = true;
        try
        {
            foreach (var (kind, preview) in _previews)
            {
                var otherIdle = true;
                foreach (var (otherKind, otherPreview) in _previews)
                {
                    if (otherKind != kind && !otherPreview.IsIdle)
                    {
                        otherIdle = false;
                        break;
                    }
                }
                preview.Update(new(_state, _generation, _live, _canExecute, ToolAvailable: _selected == kind && otherIdle, TransformPreview: _transformPreview));
            }
            Notify(nameof(IsIdle));
            RefreshQuickActions();
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnExecutionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IsIdle) or nameof(IVisionAlgorithmPreview.IsBusy))
        {
            UpdateAvailability();
        }
        else if (args.PropertyName is nameof(ShapeMatchingViewModel.UseSelection) or nameof(ShapeMatchingViewModel.SearchFullImage) or nameof(ShapeMatchingViewModel.SearchScopeText))
        {
            Notify(nameof(UseSelection));
            Notify(nameof(SearchFullImage));
            Notify(nameof(SearchScopeSummaryText));
            RefreshQuickActions();
        }
    }

    private void OnChanged()
    {
        RefreshQuickActions();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        Pattern.RoiAssignmentRecorded -= OnRoiAssignmentRecorded;
        Pattern.StructuredLogRecorded -= OnStructuredLogRecorded;
        Shapes.RoiAssignmentRecorded -= OnRoiAssignmentRecorded;
        Shapes.StructuredLogRecorded -= OnStructuredLogRecorded;
        if (FewShot is not null)
        {
            FewShot.RoiAssignmentRecorded -= OnRoiAssignmentRecorded;
            FewShot.StructuredLogRecorded -= OnStructuredLogRecorded;
        }
        if (Suite is not null)
        {
            Suite.RoiAssignmentRecorded -= OnRoiAssignmentRecorded;
            Suite.StructuredLogRecorded -= OnStructuredLogRecorded;
        }
        foreach (var preview in _previews.Values)
        {
            preview.Changed -= OnChanged;
            preview.PropertyChanged -= OnExecutionChanged;
            preview.Dispose();
        }
    }
}
