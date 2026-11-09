using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using VisionCanvas.Core.Alignment;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace VisionCanvas.Presentation;

public sealed record AlignmentScalePreset(string Name, int GridSidePerComponent, long ApproximateTotalRois);

public sealed record AlignmentScopeItem(Guid? ScopeId, string Name, Guid? ParentScopeId);

/// <summary>
/// DX12, Vulkan, Wgpu, Skia 모든 샘플 앱이 공유하는 계층 보정(전체 → 모듈 → 컴포넌트) 테스트 ViewModel.
/// ROI가 1억 개든 100억 개든 O(G) 스코프 변환만 갱신하여 즉시 보정한다.
/// </summary>
public sealed class AlignmentTestViewModel : INotifyPropertyChanged
{
    private readonly EditorSession _editor;
    private readonly InteractionController _input;
    private readonly ViewportLayout _layout;
    private readonly ImageCoordinator _images;
    private readonly Action _onAlignmentChanged;
    private readonly Action<string> _reportStatus;
    private readonly List<RelayCommand> _commands = [];

    private HierarchicalAlignmentSession? _session;
    private DocumentState? _sessionDocument;
    private bool _isAlignmentActive;
    private AlignmentScalePreset _selectedScalePreset;
    private AlignmentScopeItem? _selectedScope;
    private bool _isPanelExpanded;
    private bool _isMarkEnabled = true;
    private string _deltaXText = "0";
    private string _deltaYText = "0";
    private string _deltaThetaText = "0";
    private string _transformSummary = "계층 보정 준비 전 · [계층 보정 예제 로드]를 누르거나 현재 문서에서 바로 보정하세요.";
    private string _sampleCellsSummary = "보정 전후 샘플 좌표가 여기에 표시됩니다.";
    private double _lastUpdateMilliseconds;
    private bool _suppressScopeSync;

    public AlignmentTestViewModel(
        EditorSession editor,
        InteractionController input,
        ViewportLayout layout,
        ImageCoordinator images,
        Action onAlignmentChanged,
        Action<string> reportStatus)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(onAlignmentChanged);
        ArgumentNullException.ThrowIfNull(reportStatus);

        _editor = editor;
        _input = input;
        _layout = layout;
        _images = images;
        _onAlignmentChanged = onAlignmentChanged;
        _reportStatus = reportStatus;

        ScalePresets =
        [
            new("1억 ROI (100,040,004개)", 5001, 100_040_004L),
            new("10억 ROI (1,000,077,376개)", 15812, 1_000_077_376L),
            new("100억 ROI (10,000,000,000개)", 50000, 10_000_000_000L)
        ];
        _selectedScalePreset = ScalePresets[0];

        LoadPresetCommand = CreateCommand(async () => await LoadPresetAsync(), () => CanOperate);
        FocusScopeCommand = CreateCommand(FocusSelectedScope, () => CanOperate && _editor.State.Image is not null);
        ApplyCommand = CreateCommand(ApplyInput, () => CanOperate && _editor.State.Image is not null);
        ResetScopeCommand = CreateCommand(ResetSelectedScope, () => CanOperate && _editor.State.Image is not null);
        ResetAllCommand = CreateCommand(ResetAllScopes, () => CanOperate && _editor.State.Image is not null);

        StepXMinus10Command = CreateCommand(() => Nudge(deltaX: -10), () => CanOperate && _editor.State.Image is not null);
        StepXMinus1Command = CreateCommand(() => Nudge(deltaX: -1), () => CanOperate && _editor.State.Image is not null);
        StepXPlus1Command = CreateCommand(() => Nudge(deltaX: 1), () => CanOperate && _editor.State.Image is not null);
        StepXPlus10Command = CreateCommand(() => Nudge(deltaX: 10), () => CanOperate && _editor.State.Image is not null);

        StepYMinus10Command = CreateCommand(() => Nudge(deltaY: -10), () => CanOperate && _editor.State.Image is not null);
        StepYMinus1Command = CreateCommand(() => Nudge(deltaY: -1), () => CanOperate && _editor.State.Image is not null);
        StepYPlus1Command = CreateCommand(() => Nudge(deltaY: 1), () => CanOperate && _editor.State.Image is not null);
        StepYPlus10Command = CreateCommand(() => Nudge(deltaY: 10), () => CanOperate && _editor.State.Image is not null);

        StepThetaMinus5Command = CreateCommand(() => Nudge(deltaTheta: -5), () => CanOperate && _editor.State.Image is not null);
        StepThetaMinus1Command = CreateCommand(() => Nudge(deltaTheta: -1), () => CanOperate && _editor.State.Image is not null);
        StepThetaPlus1Command = CreateCommand(() => Nudge(deltaTheta: 1), () => CanOperate && _editor.State.Image is not null);
        StepThetaPlus5Command = CreateCommand(() => Nudge(deltaTheta: 5), () => CanOperate && _editor.State.Image is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RoiAlignmentMap? CurrentAlignment => _isAlignmentActive ? _session?.CurrentMap : null;

    public HierarchicalAlignmentSession? Session => _session;

    public long TotalLogicalRoiCount => _session?.TotalLogicalRoiCount ?? _editor.State.RoiCount;

    public bool IsPanelExpanded
    {
        get => _isPanelExpanded;
        set
        {
            if (_isPanelExpanded != value)
            {
                _isPanelExpanded = value;
                Notify(nameof(IsPanelExpanded));
            }
        }
    }

    public IReadOnlyList<AlignmentScalePreset> ScalePresets { get; }

    public AlignmentScalePreset SelectedScalePreset
    {
        get => _selectedScalePreset;
        set
        {
            if (value is not null && _selectedScalePreset != value)
            {
                _selectedScalePreset = value;
                Notify(nameof(SelectedScalePreset));
            }
        }
    }

    public IReadOnlyList<AlignmentScopeItem> Scopes { get; private set; } = [];

    public AlignmentScopeItem? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (_selectedScope == value)
            {
                return;
            }

            _selectedScope = value;
            Notify(nameof(SelectedScope));
            if (!_suppressScopeSync)
            {
                LoadSelectedScopeValues();
            }
        }
    }

    public bool IsMarkEnabled
    {
        get => _isMarkEnabled;
        set
        {
            if (_isMarkEnabled == value)
            {
                return;
            }

            _isMarkEnabled = value;
            Notify(nameof(IsMarkEnabled));
            if (!_suppressScopeSync && _editor.State.Image is not null)
            {
                ApplyInput();
            }
        }
    }

    public string DeltaXText
    {
        get => _deltaXText;
        set
        {
            if (_deltaXText != value)
            {
                _deltaXText = value;
                Notify(nameof(DeltaXText));
            }
        }
    }

    public string DeltaYText
    {
        get => _deltaYText;
        set
        {
            if (_deltaYText != value)
            {
                _deltaYText = value;
                Notify(nameof(DeltaYText));
            }
        }
    }

    public string DeltaThetaText
    {
        get => _deltaThetaText;
        set
        {
            if (_deltaThetaText != value)
            {
                _deltaThetaText = value;
                Notify(nameof(DeltaThetaText));
            }
        }
    }

    public string TransformSummary => _transformSummary;

    public string SampleCellsSummary => _sampleCellsSummary;

    public ICommand LoadPresetCommand { get; }

    public ICommand FocusScopeCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand ResetScopeCommand { get; }

    public ICommand ResetAllCommand { get; }

    public ICommand StepXMinus10Command { get; }

    public ICommand StepXMinus1Command { get; }

    public ICommand StepXPlus1Command { get; }

    public ICommand StepXPlus10Command { get; }

    public ICommand StepYMinus10Command { get; }

    public ICommand StepYMinus1Command { get; }

    public ICommand StepYPlus1Command { get; }

    public ICommand StepYPlus10Command { get; }

    public ICommand StepThetaMinus5Command { get; }

    public ICommand StepThetaMinus1Command { get; }

    public ICommand StepThetaPlus1Command { get; }

    public ICommand StepThetaPlus5Command { get; }

    private bool CanOperate => _input.IsEnabled && !_images.IsBusy && !_input.IsDragging && !_editor.IsPreviewing;

    public async Task LoadPresetAsync()
    {
        _input.Cancel();
        var (image, rois, scopes) = AlignmentTestPreset.Create(_selectedScalePreset.GridSidePerComponent);
        await _images.LoadDocumentAsync(image, rois);
        if (_editor.State.Image != image)
        {
            return;
        }

        _sessionDocument = _editor.State;
        _session = new HierarchicalAlignmentSession(_editor.State, scopes);
        _isAlignmentActive = true;
        RebuildScopeItems();
        UpdateSummaries();
        _onAlignmentChanged();
        _reportStatus($"계층 보정 프리셋 로드 완료 · 총 논리 ROI {_session.TotalLogicalRoiCount:N0}개 (전체 → 모듈 A/B → 컴포넌트 4개)");
    }

    public void OnDocumentChanged()
    {
        var state = _editor.State;
        if (state.Image is null)
        {
            _session = null;
            _sessionDocument = null;
            Scopes = [];
            _selectedScope = null;
            Notify(nameof(Scopes));
            Notify(nameof(SelectedScope));
            RefreshCommands();
            return;
        }

        if (_session is null || _sessionDocument is null || !_sessionDocument.SameContent(state) || _sessionDocument.Image != state.Image)
        {
            _isAlignmentActive = false;
            _sessionDocument = state;
            _session = new HierarchicalAlignmentSession(state, BuildScopesFromDocument(state, _selectedScalePreset.GridSidePerComponent));
            RebuildScopeItems();
            UpdateSummaries();
        }

        RefreshCommands();
    }

    public void RefreshCommands()
    {
        foreach (var command in _commands)
        {
            command.Refresh();
        }
    }

    public void ApplyInput()
    {
        EnsureSession();
        if (_session is null || _selectedScope is null)
        {
            return;
        }

        if (!TryParseDouble(_deltaXText, out var dx) || !TryParseDouble(_deltaYText, out var dy) || !TryParseDouble(_deltaThetaText, out var dTheta))
        {
            _reportStatus("보정 입력 오류: X(px), Y(px), Theta(°)에 유한한 숫자를 입력하세요.");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var offset = new FiducialScopeOffset(dx, dy, dTheta, _isMarkEnabled);
        if (!_session.TrySetOffset(_selectedScope.ScopeId, offset, out var error))
        {
            _reportStatus($"보정 거부: {error}");
            return;
        }

        _isAlignmentActive = true;
        _lastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        UpdateSummaries();
        _onAlignmentChanged();
        _reportStatus($"{_selectedScope.Name} 보정 적용 완료 ({_lastUpdateMilliseconds:F3} ms) · 로컬 ΔX={dx:F1}px, ΔY={dy:F1}px, Δθ={dTheta:F2}° (보정마크={(_isMarkEnabled ? "사용" : "미사용/상위상속")})");
    }

    public void Nudge(double deltaX = 0, double deltaY = 0, double deltaTheta = 0)
    {
        EnsureSession();
        if (_session is null || _selectedScope is null)
        {
            return;
        }

        var current = _session.GetOffset(_selectedScope.ScopeId);
        var nextX = current.DeltaX + deltaX;
        var nextY = current.DeltaY + deltaY;
        var nextTheta = Math.IEEERemainder(current.DeltaThetaDegrees + deltaTheta, 360);
        _suppressScopeSync = true;
        try
        {
            DeltaXText = nextX.ToString("0.##", CultureInfo.InvariantCulture);
            DeltaYText = nextY.ToString("0.##", CultureInfo.InvariantCulture);
            DeltaThetaText = nextTheta.ToString("0.##", CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressScopeSync = false;
        }

        ApplyInput();
    }

    public void ResetSelectedScope()
    {
        EnsureSession();
        if (_session is null || _selectedScope is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        _session.ResetScope(_selectedScope.ScopeId);
        _lastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LoadSelectedScopeValues();
        _onAlignmentChanged();
        _reportStatus($"{_selectedScope.Name} 오프셋을 (0, 0, 0°)로 초기화했습니다.");
    }

    public void ResetAllScopes()
    {
        EnsureSession();
        if (_session is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        _session.ResetAll();
        _lastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LoadSelectedScopeValues();
        _onAlignmentChanged();
        _reportStatus("전체 · 모듈 · 컴포넌트 모든 계층 보정을 초기화했습니다.");
    }

    private void FocusSelectedScope()
    {
        if (_selectedScope?.ScopeId is { } scopeId && _editor.State.Rois.ContainsKey(scopeId))
        {
            _editor.FocusRoi(scopeId, Math.Max(1, _layout.Width), Math.Max(1, _layout.Height));
            return;
        }

        _editor.FitImage(Math.Max(1, _layout.Width), Math.Max(1, _layout.Height));
    }

    private void EnsureSession()
    {
        if (_session is null && _editor.State.Image is not null)
        {
            OnDocumentChanged();
        }
    }

    private void RebuildScopeItems()
    {
        if (_session is null)
        {
            Scopes = [];
            return;
        }

        var priorId = _selectedScope?.ScopeId;
        Scopes = _session.Scopes
            .Select(s => new AlignmentScopeItem(s.ScopeId, s.Name, s.ParentScopeId))
            .ToArray();
        Notify(nameof(Scopes));

        _suppressScopeSync = true;
        try
        {
            _selectedScope = Scopes.FirstOrDefault(s => s.ScopeId == priorId) ?? Scopes.FirstOrDefault();
            Notify(nameof(SelectedScope));
        }
        finally
        {
            _suppressScopeSync = false;
        }

        LoadSelectedScopeValues();
    }

    private void LoadSelectedScopeValues()
    {
        if (_session is null || _selectedScope is null)
        {
            return;
        }

        var offset = _session.GetOffset(_selectedScope.ScopeId);
        _suppressScopeSync = true;
        try
        {
            _isMarkEnabled = offset.IsEnabled;
            _deltaXText = offset.DeltaX.ToString("0.##", CultureInfo.InvariantCulture);
            _deltaYText = offset.DeltaY.ToString("0.##", CultureInfo.InvariantCulture);
            _deltaThetaText = offset.DeltaThetaDegrees.ToString("0.##", CultureInfo.InvariantCulture);
            Notify(nameof(IsMarkEnabled));
            Notify(nameof(DeltaXText));
            Notify(nameof(DeltaYText));
            Notify(nameof(DeltaThetaText));
        }
        finally
        {
            _suppressScopeSync = false;
        }

        UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        if (_session is null || _selectedScope is null)
        {
            return;
        }

        var map = _session.CurrentMap;
        var scopeId = _selectedScope.ScopeId;
        var local = _session.GetOffset(scopeId);
        var alignment = map.GetScopeAlignment(scopeId);
        var effective = map.GetScopeTransform(scopeId);
        var pivot = ResolveScopeCenter(_editor.State, scopeId);
        var pivotShift = effective.Apply(pivot) - pivot;
        var residual = alignment?.MaxErrorPixels ?? 0;

        _transformSummary =
            $"[{_selectedScope.Name}] 보정마크={(_isMarkEnabled ? "ON" : "OFF(상위 상속)")} · " +
            $"로컬 입력(ΔX={local.DeltaX:F2}px, ΔY={local.DeltaY:F2}px, Δθ={local.DeltaThetaDegrees:F2}°) → " +
            $"최종 합성 변환(중심 이동 X={pivotShift.X:F2}px, Y={pivotShift.Y:F2}px, θ={effective.AngleDegrees:F2}°, 마크 최대잔차={residual:F4}px) · " +
            $"총 ROI {_session.TotalLogicalRoiCount:N0}개 · 갱신 {_lastUpdateMilliseconds:F3}ms";

        _sampleCellsSummary = BuildSampleCellReadout(map, scopeId);
        Notify(nameof(TransformSummary));
        Notify(nameof(SampleCellsSummary));
    }

    private string BuildSampleCellReadout(RoiAlignmentMap map, Guid? scopeId)
    {
        ComponentRoiGrid? grid = null;
        if (scopeId is { } id && map.Grids.TryGetValue(id, out var exactGrid))
        {
            grid = exactGrid;
        }
        else if (scopeId is { } parentId)
        {
            grid = map.Grids.Values.FirstOrDefault(g =>
                _editor.State.Rois.TryGetValue(g.ScopeId, out var roi) && RoiHierarchy.IsDescendant(_editor.State, roi.Id, parentId));
        }

        grid ??= map.Grids.Values.FirstOrDefault();
        if (grid is not null)
        {
            var firstTeach = grid.GetTeachingCell(0, 0);
            var firstAlign = grid.GetAlignedCell(map, 0, 0);
            var midR = grid.Rows / 2;
            var midC = grid.Columns / 2;
            var midTeach = grid.GetTeachingCell(midR, midC);
            var midAlign = grid.GetAlignedCell(map, midR, midC);
            var lastTeach = grid.GetTeachingCell(grid.Rows - 1, grid.Columns - 1);
            var lastAlign = grid.GetAlignedCell(map, grid.Rows - 1, grid.Columns - 1);

            return
                $"[{grid.Name} · {grid.Columns:N0}×{grid.Rows:N0} = {grid.TotalCount:N0}개]  " +
                $"첫 셀[0,0]: ({firstTeach.Center.X:F1}, {firstTeach.Center.Y:F1}) → ({firstAlign.Center.X:F1}, {firstAlign.Center.Y:F1}, {firstAlign.Angle:F1}°)  |  " +
                $"중앙 셀[{midR},{midC}]: ({midTeach.Center.X:F1}, {midTeach.Center.Y:F1}) → ({midAlign.Center.X:F1}, {midAlign.Center.Y:F1})  |  " +
                $"끝 셀[{grid.Rows - 1},{grid.Columns - 1}]: ({lastTeach.Center.X:F1}, {lastTeach.Center.Y:F1}) → ({lastAlign.Center.X:F1}, {lastAlign.Center.Y:F1})";
        }

        var sampleLeaf = _editor.State.Rois.Values
            .Where(r => ! _editor.State.IsGroupRoi(r.Id) && (scopeId is null || r.ParentId == scopeId || RoiHierarchy.IsDescendant(_editor.State, r.Id, scopeId.Value)))
            .OrderBy(r => r.Order)
            .FirstOrDefault() ?? _editor.State.Rois.Values.OrderBy(r => r.Order).FirstOrDefault();

        if (sampleLeaf is null)
        {
            return "현재 문서에 표시할 ROI가 없습니다.";
        }

        var aligned = map.ApplyGeometry(sampleLeaf);
        return $"샘플 [{sampleLeaf.Name}]: 티칭 ({sampleLeaf.Geometry.Center.X:F1}, {sampleLeaf.Geometry.Center.Y:F1}, {sampleLeaf.Geometry.Angle:F1}°) → 보정 후 ({aligned.Center.X:F1}, {aligned.Center.Y:F1}, {aligned.Angle:F1}°)";
    }

    private static PointD ResolveScopeCenter(DocumentState state, Guid? scopeId)
    {
        if (scopeId is { } id && state.Rois.TryGetValue(id, out var roi))
        {
            return roi.Geometry.Center;
        }

        return state.Image is { } image ? new(image.Width / 2d, image.Height / 2d) : new(4096, 4096);
    }

    private static ImmutableArray<FiducialScopeDefinition> BuildScopesFromDocument(DocumentState state, int gridSide)
    {
        var width = state.Image?.Width ?? 8192;
        var height = state.Image?.Height ?? 8192;
        var builder = ImmutableArray.CreateBuilder<FiducialScopeDefinition>();
        builder.Add(new FiducialScopeDefinition(
            null,
            "전체 보정 (Global · 전체 화면)",
            null,
            [
                new(width * 0.1, height * 0.1),
                new(width * 0.9, height * 0.1),
                new(width * 0.9, height * 0.9),
                new(width * 0.1, height * 0.9)
            ]));

        foreach (var roi in state.Rois.Values.Where(r => state.IsGroupRoi(r.Id)).OrderBy(r => RoiHierarchy.Depth(state.Rois, r.Id)).ThenBy(r => r.Order))
        {
            var depth = RoiHierarchy.Depth(state.Rois, roi.Id);
            var rolePrefix = depth switch
            {
                1 => "보드 그룹 보정",
                2 => "모듈 보정",
                _ => "컴포넌트 보정"
            };
            var bounds = roi.Geometry.Bounds;
            var dx = Math.Max(4, bounds.Width * 0.35);
            var dy = Math.Max(4, bounds.Height * 0.35);
            var c = roi.Geometry.Center;
            ImmutableArray<PointD> marks =
            [
                new(c.X - dx, c.Y - dy),
                new(c.X + dx, c.Y + dy)
            ];

            ComponentRoiGrid? grid = null;
            if (depth >= 2)
            {
                var pitch = Math.Max(bounds.Width * 0.6 / Math.Max(1, gridSide), 0.02);
                grid = new ComponentRoiGrid(roi.Id, $"{roi.Name} 반복 셀 배치", c, gridSide, gridSide, pitch, pitch, Math.Max(pitch * 0.65, 0.01), Math.Max(pitch * 0.65, 0.01));
            }

            builder.Add(new FiducialScopeDefinition(roi.Id, $"{rolePrefix} · {roi.Name}", roi.ParentId, marks, grid));
        }

        return builder.ToImmutable();
    }

    private static bool TryParseDouble(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private RelayCommand CreateCommand(Action execute, Func<bool> canExecute)
    {
        var command = new RelayCommand(execute, canExecute);
        _commands.Add(command);
        return command;
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));

    private sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                execute();
            }
        }

        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
