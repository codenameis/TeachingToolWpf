using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using VisionAlgorithm.OpenSrc.Preprocessing;
using VisionCanvas.Commands;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionImaging.Abstractions;

namespace VisionCanvas.Filters.Wpf.ViewModels;

/// <summary>UI-owned preview session, with one worker, one replaceable pending request, and an ordered multi-step filter pipeline list.</summary>
public sealed class FilterPreviewViewModel : ObservableObject, IDisposable
{
    private const string RequestedCpuOnlyReason = "CpuOnly was requested.";
    private const int PreviewDebounceMilliseconds = 100;
    private const int MinimumSummaryRegionDimension = 5;
    private const double BytesPerMebibyte = 1024d * 1024;

    private readonly IRoiFilterPreview _processor;
    private readonly EditorSession _editor;
    private readonly FilterParameterDraft _draft = new();
    private readonly LatestRequestPump<FilterRequest> _pump = new(TimeSpan.FromMilliseconds(PreviewDebounceMilliseconds));
    private DocumentState _document = DocumentState.Empty;
    private bool _live;
    private bool _active;
    private bool _busy;
    private bool _disposed;
    private bool _previewEnabled = true;
    private bool _preFilterBeforeInspection = true;
    private bool _isHelpManualOpen;
    private bool _suppressPipelineSync;
    private bool _editing;
    private string _status = "필터를 선택한 뒤 미리보기를 누르세요.";
    private string _pipelineSummaryText = "전처리 파이프라인: 0단계 (필터 없음 — 원본 직접 검사)";
    private string _roiHistogramSummaryText = "히스토그램(256-Bin): ROI 필터 미리보기 시 Otsu 임계값·평균·표준편차 자동 계산";
    private string _roiLineProfileSummaryText = "1D 중심선 프로파일: 수평/수직 평균 밝기 및 최대 에지 기울기(ΔI) 대기";
    private FilterPipelineStepItem? _selectedPipelineStep;
    private FilterPreviewResult? _result;
    private RoiTransformPreview? _transformPreview;

    public ObservableCollection<FilterPipelineStepItem> PipelineSteps { get; } = [];

    public event Action<ImmutableArray<FilterSettings>, bool>? ActivePipelineChanged;

    public IReadOnlyList<FilterOption> Options => FilterCatalog.Options;

    public IReadOnlyList<RoiFilterExecution> ExecutionOptions => FilterCatalog.ExecutionOptions;

    public bool PreFilterBeforeInspection
    {
        get => _preFilterBeforeInspection;
        set
        {
            if (Set(ref _preFilterBeforeInspection, value))
            {
                UpdatePipelineSummary();
                RaiseActivePipelineChanged();
            }
        }
    }

    public bool IsHelpManualOpen
    {
        get => _isHelpManualOpen;
        set => Set(ref _isHelpManualOpen, value);
    }

    public string PipelineSummaryText
    {
        get => _pipelineSummaryText;
        private set => Set(ref _pipelineSummaryText, value);
    }

    public string RoiHistogramSummaryText
    {
        get => _roiHistogramSummaryText;
        private set => Set(ref _roiHistogramSummaryText, value);
    }

    public string RoiLineProfileSummaryText
    {
        get => _roiLineProfileSummaryText;
        private set => Set(ref _roiLineProfileSummaryText, value);
    }

    public FilterPipelineStepItem? SelectedPipelineStep
    {
        get => _selectedPipelineStep;
        set
        {
            if (Set(ref _selectedPipelineStep, value) && value is not null && !_suppressPipelineSync)
            {
                _suppressPipelineSync = true;
                try
                {
                    LoadSettings(value.Settings);
                }
                finally
                {
                    _suppressPipelineSync = false;
                }
            }
        }
    }

    public RoiFilterKind SelectedFilter
    {
        get => _draft.SelectedFilter;
        set
        {
            if (_draft.SetSelectedFilter(value))
            {
                Notify(nameof(SelectedFilter));
                NotifyFilterFields();
                DraftChanged();
            }
        }
    }

    public string Threshold
    {
        get => _draft.Threshold;
        set
        {
            if (_draft.SetThreshold(value))
            {
                Notify(nameof(Threshold));
                DraftChanged();
            }
        }
    }

    public string EdgeLow
    {
        get => _draft.EdgeLow;
        set
        {
            if (_draft.SetEdgeLow(value))
            {
                Notify(nameof(EdgeLow));
                DraftChanged();
            }
        }
    }

    public string EdgeHigh
    {
        get => _draft.EdgeHigh;
        set
        {
            if (_draft.SetEdgeHigh(value))
            {
                Notify(nameof(EdgeHigh));
                DraftChanged();
            }
        }
    }

    public bool IsBinarize => _draft.IsBinarize;
    public bool IsEdge => _draft.IsEdge;
    public bool IsIndustrial => _draft.IsIndustrial;
    public bool UsesRadius => _draft.UsesRadius;
    public bool UsesSigma => _draft.UsesSigma;
    public bool UsesThreshold => _draft.UsesThreshold;
    public string ThresholdCaption => _draft.ThresholdCaption;

    public string Radius
    {
        get => _draft.Radius;
        set
        {
            if (_draft.SetRadius(value))
            {
                Notify(nameof(Radius));
                DraftChanged();
            }
        }
    }

    public string Sigma
    {
        get => _draft.Sigma;
        set
        {
            if (_draft.SetSigma(value))
            {
                Notify(nameof(Sigma));
                DraftChanged();
            }
        }
    }

    public RoiFilterExecution Execution
    {
        get => _draft.Execution;
        set
        {
            if (_draft.SetExecution(value))
            {
                Notify(nameof(Execution));
                DraftChanged();
            }
        }
    }

    public string ValidationMessage => _draft.ValidationMessage;

    public double RadiusValue
    {
        get => _draft.RadiusValue;
        set => Radius = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }

    public double SigmaValue
    {
        get => _draft.SigmaValue;
        set => Sigma = Math.Round(value, 2).ToString(CultureInfo.CurrentCulture);
    }

    public double ThresholdValue
    {
        get => _draft.ThresholdValue;
        set => Threshold = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }

    public double EdgeLowValue
    {
        get => _draft.EdgeLowValue;
        set => EdgeLow = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }

    public double EdgeHighValue
    {
        get => _draft.EdgeHighValue;
        set => EdgeHigh = Math.Round(value).ToString(CultureInfo.CurrentCulture);
    }

    public double ThresholdMaximum => _draft.ThresholdMaximum;

    public bool HasMixedFilters => _document.SelectionFacts.HasMixedFilters;

    public bool IsPreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            if (Set(ref _previewEnabled, value))
            {
                Schedule(force: true, invalidate: true);
                RefreshCommands();
            }
        }
    }

    public bool IsActive
    {
        get => _active;
        private set => Set(ref _active, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public FilterPreviewResult? Result => _result;

    public event Action? Changed;

    public RelayCommand Preview { get; }
    public RelayCommand Apply { get; }
    public RelayCommand Stop { get; }
    public RelayCommand ClearRegistration { get; }
    public RelayCommand BeginParameterEdit { get; }
    public RelayCommand EndParameterEdit { get; }
    public RelayCommand CancelParameterEdit { get; }
    public RelayCommand AddStepToPipeline { get; }
    public RelayCommand RemoveSelectedStep { get; }
    public RelayCommand MoveSelectedStepUp { get; }
    public RelayCommand MoveSelectedStepDown { get; }
    public RelayCommand ToggleHelpManual { get; }

    public Task WhenIdle => _pump.WhenIdle;

    public FilterPreviewViewModel(IRoiFilterPreview processor, EditorSession editor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(editor);

        _processor = processor;
        _editor = editor;
        Preview = new(() =>
        {
            IsPreviewEnabled = true;
            Register();
        }, () => SelectedFilter != RoiFilterKind.None && _document.Selection.Count > 0 && !editor.IsPreviewing);
        Apply = new(Register, () => _document.Selection.Count > 0 && !editor.IsPreviewing);
        BeginParameterEdit = new(StartParameterEdit, () => !_disposed && !_draft.IsEditing &&
            !editor.IsPreviewing && _document.Selection.Count > 0 && SelectedFilter != RoiFilterKind.None);
        EndParameterEdit = new(CompleteParameterEdit, () => _draft.IsEditing);
        CancelParameterEdit = new(CancelParameterChanges, () => _draft.IsEditing);
        Stop = new(StopPreview, () => IsActive);
        ClearRegistration = new(ClearAllRegistrations,
            () => !editor.IsPreviewing && (!_document.SelectionFacts.RegisteredFilters.IsEmpty || PipelineSteps.Count > 0 || SelectedFilter != RoiFilterKind.None));

        AddStepToPipeline = new(AddCurrentFilterStep, () => SelectedFilter != RoiFilterKind.None && !editor.IsPreviewing);
        RemoveSelectedStep = new(RemoveCurrentFilterStep, () => SelectedPipelineStep is not null && !editor.IsPreviewing);
        MoveSelectedStepUp = new(() => MoveCurrentFilterStep(-1), () => SelectedPipelineStep is not null && PipelineSteps.IndexOf(SelectedPipelineStep) > 0);
        MoveSelectedStepDown = new(() => MoveCurrentFilterStep(1), () => SelectedPipelineStep is not null && PipelineSteps.IndexOf(SelectedPipelineStep) >= 0 && PipelineSteps.IndexOf(SelectedPipelineStep) < PipelineSteps.Count - 1);
        ToggleHelpManual = new(() => IsHelpManualOpen = !IsHelpManualOpen);
    }

    public void RefreshLiveFrame()
    {
        if (IsActive && _live && !IsBusy)
        {
            Schedule(force: true, keepVisible: true);
        }
    }

    public ImmutableArray<FilterSettings> BuildEffectivePipeline()
    {
        if (!PreFilterBeforeInspection)
        {
            return [];
        }

        if (PipelineSteps.Count > 0)
        {
            return PipelineSteps
                .Where(static s => s.IsEnabled && s.Settings.Kind != RoiFilterKind.None)
                .Select(static s => s.Settings)
                .ToImmutableArray();
        }

        if (TryReadSettings(out var current) && current.Kind != RoiFilterKind.None && current.IsEnabled)
        {
            return [current];
        }

        return [];
    }

    public void LoadRecipePipeline(IEnumerable<FilterSettings> steps, bool preFilterBeforeInspection = true)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _suppressPipelineSync = true;
        try
        {
            PreFilterBeforeInspection = preFilterBeforeInspection;
            PipelineSteps.Clear();
            var idx = 1;
            foreach (var step in steps)
            {
                if (step.Kind == RoiFilterKind.None)
                {
                    continue;
                }

                PipelineSteps.Add(new FilterPipelineStepItem(idx++, step, OnPipelineStepItemToggled));
            }

            SelectedPipelineStep = PipelineSteps.LastOrDefault();
            if (SelectedPipelineStep is { } last)
            {
                LoadSettings(last.Settings);
            }
        }
        finally
        {
            _suppressPipelineSync = false;
        }

        CommitPipelineToSelection();
    }

    public void Update(DocumentState state, bool isLive, RoiTransformPreview? preview = null, PixelKind? pixelKind = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_draft.UpdateInputMaximum(pixelKind))
        {
            Notify(nameof(ThresholdMaximum));
        }

        var editingChanged = _editing != _editor.IsPreviewing;
        _editing = _editor.IsPreviewing;
        if (IsViewOnlyNavigation(state, isLive, preview))
        {
            _document = state;
            if (editingChanged)
            {
                RefreshCommands();
            }
            return;
        }

        var selectionChanged = HasSelectionChanged(state);
        var settingsChanged = !selectionChanged && HasSelectedFilterSettingsChanged(state);
        var sourceChanged = _live != isLive;
        _document = state;
        _live = isLive;
        _transformPreview = preview;
        Notify(nameof(HasMixedFilters));
        if (selectionChanged || settingsChanged)
        {
            _draft.ResetEdit();
            var firstRoi = state.SelectionFacts.First;
            LoadSettings(firstRoi?.Filter ?? new(RoiFilterKind.None));
            _suppressPipelineSync = true;
            try
            {
                PipelineSteps.Clear();
                if (firstRoi is not null && !firstRoi.FilterPipeline.IsDefaultOrEmpty)
                {
                    var order = 1;
                    foreach (var step in firstRoi.FilterPipeline)
                    {
                        PipelineSteps.Add(new FilterPipelineStepItem(order++, step, OnPipelineStepItemToggled));
                    }
                }

                SelectedPipelineStep = PipelineSteps.LastOrDefault();
            }
            finally
            {
                _suppressPipelineSync = false;
            }

            UpdatePipelineSummary();
            RaiseActivePipelineChanged();
        }
        var invalidate = selectionChanged || settingsChanged || sourceChanged;
        var keepExistingVisible = !invalidate || (!selectionChanged && !sourceChanged && _result is not null && SelectedFilter != RoiFilterKind.None);
        Schedule(invalidate: invalidate, keepVisible: keepExistingVisible);
        RefreshCommands();
    }

    public void StopPreview() => IsPreviewEnabled = false;

    public void Dispose()
    {
        _disposed = true;
        _draft.ResetEdit();
        _pump.Dispose();
        _result = null;
    }

    private void ClearAllRegistrations()
    {
        _suppressPipelineSync = true;
        try
        {
            PipelineSteps.Clear();
            SelectedPipelineStep = null;
            SelectedFilter = RoiFilterKind.None;
        }
        finally
        {
            _suppressPipelineSync = false;
        }

        _editor.SetFilterPipeline([]);
        _editor.SetFilters(new(RoiFilterKind.None));
        Update(_editor.State, _live);
        UpdatePipelineSummary();
        RaiseActivePipelineChanged();
        RefreshCommands();
    }

    private void NotifyFilterFields()
    {
        Notify(nameof(IsBinarize));
        Notify(nameof(IsEdge));
        Notify(nameof(IsIndustrial));
        Notify(nameof(UsesRadius));
        Notify(nameof(UsesSigma));
        Notify(nameof(UsesThreshold));
        Notify(nameof(ThresholdCaption));
        _draft.RefreshSliderValues(Notify);
        Notify(nameof(ThresholdMaximum));
    }

    private bool TryReadSettings(out FilterSettings settings)
    {
        var parsed = _draft.TryParseSettings(out var error);
        if (_draft.SetValidationMessage(error))
        {
            Notify(nameof(ValidationMessage));
        }

        if (parsed is not null)
        {
            settings = parsed;
            return true;
        }

        settings = new(RoiFilterKind.None);
        Status = error;
        return false;
    }

    private void LoadSettings(FilterSettings settings)
    {
        _draft.LoadSettings(settings, Notify);
        NotifyFilterFields();
    }

    private void StartParameterEdit()
    {
        if (!TryReadSettings(out var settings))
        {
            return;
        }

        _draft.BeginEdit(settings, _document.Selection.Select(id => _document.Rois[id]).ToArray());
        RefreshCommands();
    }

    private void CompleteParameterEdit()
    {
        var valid = TryReadSettings(out _);
        _draft.ResetEdit();
        if (valid)
        {
            Register();
        }
        else
        {
            Schedule(force: true, invalidate: true);
        }
        RefreshCommands();
    }

    private void CancelParameterChanges()
    {
        var original = _draft.ResetEdit();
        if (original is not null)
        {
            LoadSettings(original);
        }
        Schedule(force: true, invalidate: true);
        RefreshCommands();
    }

    private void AddCurrentFilterStep()
    {
        if (!TryReadSettings(out var settings) || settings.Kind == RoiFilterKind.None)
        {
            return;
        }

        var item = new FilterPipelineStepItem(PipelineSteps.Count + 1, settings with { IsEnabled = true }, OnPipelineStepItemToggled);
        PipelineSteps.Add(item);
        SelectedPipelineStep = item;
        CommitPipelineToSelection();
    }

    private void RemoveCurrentFilterStep()
    {
        if (SelectedPipelineStep is not { } target || !PipelineSteps.Remove(target))
        {
            return;
        }

        RenumberPipelineSteps();
        SelectedPipelineStep = PipelineSteps.LastOrDefault();
        CommitPipelineToSelection();
    }

    private void MoveCurrentFilterStep(int delta)
    {
        if (SelectedPipelineStep is not { } target)
        {
            return;
        }

        var idx = PipelineSteps.IndexOf(target);
        var nextIdx = idx + delta;
        if (idx < 0 || nextIdx < 0 || nextIdx >= PipelineSteps.Count)
        {
            return;
        }

        PipelineSteps.Move(idx, nextIdx);
        RenumberPipelineSteps();
        SelectedPipelineStep = target;
        CommitPipelineToSelection();
    }

    private void RenumberPipelineSteps()
    {
        for (var i = 0; i < PipelineSteps.Count; i++)
        {
            PipelineSteps[i].RefreshStepOrder(i + 1);
        }
    }

    private void OnPipelineStepItemToggled(FilterPipelineStepItem step)
    {
        if (_suppressPipelineSync)
        {
            return;
        }

        CommitPipelineToSelection();
    }

    private void CommitPipelineToSelection()
    {
        UpdatePipelineSummary();
        RaiseActivePipelineChanged();
        if (_document.Selection.Count > 0 && !_editor.IsPreviewing)
        {
            var all = PipelineSteps.Select(static s => s.Settings).ToImmutableArray();
            _editor.SetFilterPipeline(all);
            Update(_editor.State, _live);
            Schedule(force: true, invalidate: true);
        }
        else
        {
            RefreshCommands();
        }
    }

    private void UpdatePipelineSummary()
    {
        var enabled = PipelineSteps.Where(static s => s.IsEnabled && s.Settings.Kind != RoiFilterKind.None).ToList();
        if (enabled.Count > 0)
        {
            var prefix = PreFilterBeforeInspection ? "[선(先)필터 → 후(後)검사 ON]" : "[선필터 우회]";
            var chain = string.Join(" → ", enabled.Select(static s => s.Settings.Kind.ToString()));
            PipelineSummaryText = $"{prefix} 활성 {enabled.Count}/{PipelineSteps.Count}단계: {chain}";
            return;
        }

        if (SelectedFilter != RoiFilterKind.None)
        {
            var prefix = PreFilterBeforeInspection ? "[선(先)필터 → 후(後)검사 ON]" : "[선필터 우회]";
            PipelineSummaryText = $"{prefix} 단일 필터: {SelectedFilter}";
            return;
        }

        PipelineSummaryText = "전처리 파이프라인: 0단계 (필터 없음 — 원본 직접 검사)";
    }

    private void RaiseActivePipelineChanged() => ActivePipelineChanged?.Invoke(BuildEffectivePipeline(), PreFilterBeforeInspection);

    private bool IsViewOnlyNavigation(DocumentState state, bool isLive, RoiTransformPreview? preview)
        => ReferenceEquals(_document.Rois, state.Rois) && ReferenceEquals(_document.Selection, state.Selection) &&
           _document.Image == state.Image && _live == isLive && _transformPreview == preview;

    private bool HasSelectionChanged(DocumentState state)
        => _document.Image != state.Image || !_document.Selection.SetEquals(state.Selection);

    private bool HasSelectedFilterSettingsChanged(DocumentState state)
        => !ReferenceEquals(_document.Rois, state.Rois) &&
           state.Selection.Any(id => !_document.Rois.TryGetValue(id, out var old) || old.Filter != state.Rois[id].Filter || !old.FilterPipeline.SequenceEqual(state.Rois[id].FilterPipeline));

    private void DraftChanged()
    {
        _draft.RefreshSliderValues(Notify);
        if (_draft.IsEditing)
        {
            if (TryReadSettings(out var settings))
            {
                _draft.UpdateDraftSettings(settings);
                Schedule(force: true, keepVisible: true);
            }
        }
        else if (IsActive || SelectedFilter == RoiFilterKind.None || !_document.SelectionFacts.RegisteredFilters.IsEmpty)
        {
            Register();
        }

        UpdatePipelineSummary();
        RaiseActivePipelineChanged();
        RefreshCommands();
    }

    private void Register()
    {
        if (_disposed || _editor.IsPreviewing)
        {
            return;
        }

        if (TryReadSettings(out var settings))
        {
            if (SelectedPipelineStep is not null && settings.Kind != RoiFilterKind.None && !_suppressPipelineSync)
            {
                SelectedPipelineStep.UpdateSettings(settings);
            }

            UpdatePipelineSummary();
            RaiseActivePipelineChanged();

            if (_document.Selection.Count == 0)
            {
                return;
            }

            if (PipelineSteps.Count > 0)
            {
                _editor.SetFilterPipeline(PipelineSteps.Select(static s => s.Settings).ToImmutableArray());
            }
            else
            {
                _editor.SetFilters(settings);
            }

            Update(_editor.State, _live);
            Schedule(force: true, invalidate: true);
        }
    }

    private void RefreshCommands()
    {
        Preview?.Refresh();
        Apply?.Refresh();
        Stop?.Refresh();
        ClearRegistration?.Refresh();
        BeginParameterEdit?.Refresh();
        EndParameterEdit?.Refresh();
        CancelParameterEdit?.Refresh();
        AddStepToPipeline?.Refresh();
        RemoveSelectedStep?.Refresh();
        MoveSelectedStepUp?.Refresh();
        MoveSelectedStepDown?.Refresh();
    }

    private void ClearResult()
    {
        if (_result is null)
        {
            return;
        }
        _result = null;
        Changed?.Invoke();
    }

    private void Schedule(bool force = false, bool keepVisible = false, bool invalidate = false)
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<Roi> rois = _draft.EditingRois ?? (IReadOnlyList<Roi>)_document.SelectionFacts.RegisteredFilters;
        IsActive = IsPreviewEnabled && _document.Image is not null && rois.Count > 0;
        if (!IsActive)
        {
            var idleStatus = !IsPreviewEnabled
                ? "미리보기 꺼짐 · 등록 설정 유지"
                : "등록된 필터 없음 · 필터 선택 후 설정을 적용하거나 리스트에 추가하세요.";
            ResetPendingSchedule(idleStatus);
            return;
        }

        try
        {
            var activePipeline = BuildEffectivePipeline();
            var next = new FilterRequest(_document.Image!, _live, rois, _transformPreview)
            {
                OverrideSettings = _draft.DraftSettings,
                OverridePipeline = activePipeline.IsEmpty ? null : activePipeline
            };
            if (!force && IsSameFilterRequest(_pump.LastRequested, next))
            {
                return;
            }

            QueueFilterRequest(next, keepVisible, invalidate);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            ResetPendingSchedule("임계값을 확인하세요. 0–255 숫자를 입력하세요.");
        }
    }

    private static bool IsSameFilterRequest(FilterRequest? previous, FilterRequest next) =>
        previous is not null &&
        previous.Image == next.Image &&
        previous.IsLive == next.IsLive &&
        previous.Transform == next.Transform &&
        previous.OverrideSettings == next.OverrideSettings &&
        ((previous.OverridePipeline is null && next.OverridePipeline is null) ||
         (previous.OverridePipeline is { } p1 && next.OverridePipeline is { } p2 && p1.SequenceEqual(p2))) &&
        previous.Rois.SequenceEqual(next.Rois);

    private void QueueFilterRequest(FilterRequest next, bool keepVisible, bool invalidate)
    {
        if (!keepVisible)
        {
            ClearResult();
            Status = "선택 영역을 처리하고 있습니다…";
        }

        _pump.Enqueue(
            next,
            invalidateInFlight: invalidate,
            executeAsync: async (request, capturedRevision, token) =>
            {
                var output = await _processor.ProcessAsync(request, token);
                if (_disposed || !_pump.IsCurrentRevision(capturedRevision) || !IsActive)
                {
                    return;
                }

                _result = output;
                UpdateHistogramFromResult(output);
                var state = !_pump.HasPending ? "미리보기 켜짐" : "갱신 중 · 마지막 완료 결과 표시";
                Status = $"{state} · {output.Regions.Count}개 ROI · 전체 {output.Milliseconds:F0} ms\n{DescribeExecution(output)}";
                Changed?.Invoke();
            },
            onError: ex =>
            {
                ClearResult();
                Status = $"미리보기 실패: {ex.Message}";
            },
            onBusyChanged: busy => IsBusy = busy);
    }

    private void ResetPendingSchedule(string status)
    {
        _pump.CancelPending();
        ClearResult();
        Status = status;
    }

    private void UpdateHistogramFromResult(FilterPreviewResult output)
    {
        if (output.Regions.Count == 0)
        {
            return;
        }

        var first = output.Regions[0];
        if (first.Width < MinimumSummaryRegionDimension || first.Height < MinimumSummaryRegionDimension || first.Gray8.Length < first.Width * first.Height)
        {
            return;
        }

        var summary = FrequencyAndTextureFilterEngine.ComputeRoiHistogramAndProfile(first.Gray8, first.Width, first.Height, first.Width);
        RoiHistogramSummaryText = string.Create(
            CultureInfo.InvariantCulture,
            $"히스토그램(256-Bin) | Otsu T={summary.OtsuThreshold} | 평균={summary.MeanIntensity:F1}, σ={summary.StdDevIntensity:F1}, Min={summary.MinIntensity}, Max={summary.MaxIntensity}");
        RoiLineProfileSummaryText = string.Create(
            CultureInfo.InvariantCulture,
            $"1D 중심선 프로파일 | 수평 평균={summary.HorizontalCenterMean:F1} (최대 ΔI={summary.HorizontalPeakGradient:F0}) | 수직 평균={summary.VerticalCenterMean:F1} (최대 ΔI={summary.VerticalPeakGradient:F0})");
    }

    private static string DescribeExecution(FilterPreviewResult output)
    {
        var reports = output.Regions.Where(region => region.Execution is not null).Select(region => region.Execution!).ToArray();
        if (reports.Length == 0)
        {
            return "OpenCV / OpenSrc · CPU";
        }
        var stages = reports.SelectMany(report => report.Stages).ToArray();
        var devices = string.Join(", ", stages.Select(stage => stage.Device == "CPU" ? "CPU" : "CUDA · " + stage.Device).Distinct());
        var fallback = string.Join("\n", stages.Where(stage => stage.Device == "CPU" && stage.Reason != RequestedCpuOnlyReason)
            .Select(stage => $"{stage.Filter}: CPU 전환 · {stage.Reason}").Distinct());
        var legacy = reports.Length < output.Regions.Count ? " + OpenCV CPU" : "";
        var memory = reports.Max(report => report.HostWorkingBytes + report.GpuWorkingBytes) / BytesPerMebibyte;
        var transfer = reports.Sum(report => report.UploadBytes + report.DownloadBytes) / BytesPerMebibyte;
        return $"IndustrialVision · {devices}{legacy}\n엔진 최대 작업 메모리 {memory:F1} MiB · 전송 {transfer:F1} MiB" +
            (fallback.Length == 0 ? "" : "\n" + fallback);
    }
}
