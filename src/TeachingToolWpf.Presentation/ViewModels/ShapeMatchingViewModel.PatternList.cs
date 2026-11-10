using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Core.Editing;
using VisionCanvas.Rendering;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    private const double BytesPerKilobyte = 1024.0;

    private PatternLibraryFileStore _patternFileStore = new();
    private RegisteredPatternItem? _selectedRegisteredPattern;
    private PatternRegistrationRole _pendingRegistrationRole = PatternRegistrationRole.Good;
    private PatternRegistrationRole _trainRole = PatternRegistrationRole.Good;
    private bool _appendNewPatternOnTrain;
    private bool _suppressPatternSelectionSync;
    private AsyncCommand? _registerGoodPatternCommand;
    private AsyncCommand? _registerDefectPatternCommand;
    private RelayCommand? _toggleSelectedPatternRoleCommand;
    private RelayCommand? _removeSelectedPatternCommand;
    private AsyncCommand? _reloadPatternImagesCommand;

    /// <summary>등록 패턴 이미지를 개별 바이너리 파일(.pimg)과 매니페스트(manifest.json)로 병렬 관리하는 파일 저장소.</summary>
    public PatternLibraryFileStore PatternFileStore => _patternFileStore;

    /// <summary>세션에 등록된 모든 패턴 템플릿 목록.</summary>
    public ObservableCollection<RegisteredPatternItem> RegisteredPatterns { get; } = [];

    /// <summary>현재 선택된 매칭 방식(<see cref="Method"/>)에 등록된 패턴 템플릿 목록.</summary>
    public ObservableCollection<RegisteredPatternItem> ActiveMethodPatterns { get; } = [];

    public AsyncCommand RegisterGoodPattern => _registerGoodPatternCommand ??= new AsyncCommand(
        RegisterGoodPatternAsync,
        Failed,
        CanTrainCurrentRoi);

    public AsyncCommand RegisterDefectPattern => _registerDefectPatternCommand ??= new AsyncCommand(
        RegisterDefectPatternAsync,
        Failed,
        CanTrainCurrentRoi);

    public RelayCommand ToggleSelectedPatternRole => _toggleSelectedPatternRoleCommand ??= new RelayCommand(
        () => TogglePatternRole(_selectedRegisteredPattern),
        () => IsIdle && _selectedRegisteredPattern is not null);

    public RelayCommand RemoveSelectedPattern => _removeSelectedPatternCommand ??= new RelayCommand(
        () => RemovePattern(_selectedRegisteredPattern),
        () => IsIdle && _selectedRegisteredPattern is not null);

    public AsyncCommand ReloadPatternImages => _reloadPatternImagesCommand ??= new AsyncCommand(
        ReloadPatternImagesInParallelAsync,
        Failed,
        () => IsIdle && RegisteredPatterns.Count > 0);

    public void SetPatternStoreDirectory(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        _patternFileStore = new PatternLibraryFileStore(storeDirectory);
        Notify(nameof(PatternFileStore));
        Notify(nameof(PatternStorageSummaryText));
    }

    public PatternRegistrationRole PendingRegistrationRole
    {
        get => _pendingRegistrationRole;
        set
        {
            if (Set(ref _pendingRegistrationRole, value))
            {
                Notify(nameof(IsPendingRoleGood));
                Notify(nameof(IsPendingRoleDefect));
            }
        }
    }

    public bool IsPendingRoleGood
    {
        get => _pendingRegistrationRole == PatternRegistrationRole.Good;
        set
        {
            if (value)
            {
                PendingRegistrationRole = PatternRegistrationRole.Good;
            }
        }
    }

    public bool IsPendingRoleDefect
    {
        get => _pendingRegistrationRole == PatternRegistrationRole.Defect;
        set
        {
            if (value)
            {
                PendingRegistrationRole = PatternRegistrationRole.Defect;
            }
        }
    }

    public RegisteredPatternItem? SelectedRegisteredPattern
    {
        get => _selectedRegisteredPattern;
        set
        {
            if (ReferenceEquals(_selectedRegisteredPattern, value))
            {
                return;
            }
            _selectedRegisteredPattern = value;
            Notify();
            Notify(nameof(HasSelectedRegisteredPattern));
            _toggleSelectedPatternRoleCommand?.Refresh();
            _removeSelectedPatternCommand?.Refresh();
            if (!_suppressPatternSelectionSync && value is not null && IsIdle)
            {
                ActivateRegisteredPattern(value);
            }
        }
    }

    public bool HasSelectedRegisteredPattern => _selectedRegisteredPattern is not null;
    public int RegisteredPatternCount => ActiveMethodPatterns.Count;
    public int RegisteredGoodPatternCount => ActiveMethodPatterns.Count(p => p.IsGood);
    public int RegisteredDefectPatternCount => ActiveMethodPatterns.Count(p => p.IsDefect);
    public int EnabledPatternCount => ActiveMethodPatterns.Count(p => p.IsEnabled);
    public bool HasRegisteredPatterns => ActiveMethodPatterns.Count > 0;
    public bool HasEnabledRegisteredPatterns => ActiveMethodPatterns.Count == 0
        ? HasModel
        : ActiveMethodPatterns.Any(p => p.IsEnabled);

    public string RegisteredPatternListSummary => ActiveMethodPatterns.Count == 0
        ? "등록된 패턴 없음 · ROI를 선택한 뒤 [+ 정상(OK) 패턴 추가] 또는 [+ 불량(NG) 패턴 추가]를 누르세요."
        : $"등록 패턴 {RegisteredPatternCount}개 (정상 {RegisteredGoodPatternCount} · 불량 {RegisteredDefectPatternCount} · 활성 {EnabledPatternCount}개)";

    public string PatternStorageSummaryText
    {
        get
        {
            if (ActiveMethodPatterns.Count == 0)
            {
                return "외부 파일 분리 보관 대기 (.pimg + manifest.json)";
            }

            int fileCount = ActiveMethodPatterns.Count(p => p.IsBackedByFile);
            long totalBytes = ActiveMethodPatterns.Sum(p => p.DiskSizeBytes);
            return $"외부 병렬 파일 캐시: {fileCount}/{ActiveMethodPatterns.Count}개 (.pimg · 총 {totalBytes / BytesPerKilobyte:F1} KB)";
        }
    }

    private bool CanTrainCurrentRoi()
        => IsIdle && _canExecute && _image is not null && _selection is not null && OptionsError.Length == 0 &&
           (IsPatMaxMethod ? _patMaxService is not null : IsCpuMethod ? _cpuService is not null : IsShapeMethod || _templateService is not null);

    private async Task RegisterGoodPatternAsync()
    {
        _appendNewPatternOnTrain = true;
        _trainRole = PatternRegistrationRole.Good;
        PendingRegistrationRole = PatternRegistrationRole.Good;
        await ExecuteTrainCoreAsync();
    }

    private async Task RegisterDefectPatternAsync()
    {
        _appendNewPatternOnTrain = true;
        _trainRole = PatternRegistrationRole.Defect;
        PendingRegistrationRole = PatternRegistrationRole.Defect;
        await ExecuteTrainCoreAsync();
    }

    private async Task TrainSelectedOrDefaultAsync()
    {
        _appendNewPatternOnTrain = false;
        _trainRole = _selectedRegisteredPattern is { Method: var m } && m == Method
            ? _selectedRegisteredPattern.Role
            : _pendingRegistrationRole;
        await ExecuteTrainCoreAsync();
    }

    public void TogglePatternRole(RegisteredPatternItem? item)
    {
        var target = item ?? _selectedRegisteredPattern;
        if (target is null || !IsIdle)
        {
            return;
        }
        var nextRole = target.IsDefect ? PatternRegistrationRole.Good : PatternRegistrationRole.Defect;
        if (target.Name.StartsWith("정상-", StringComparison.Ordinal) && nextRole == PatternRegistrationRole.Defect)
        {
            target.Name = string.Concat("불량-", target.Name.AsSpan("정상-".Length));
        }
        else if (target.Name.StartsWith("불량-", StringComparison.Ordinal) && nextRole == PatternRegistrationRole.Good)
        {
            target.Name = string.Concat("정상-", target.Name.AsSpan("불량-".Length));
        }
        target.Role = nextRole;
        if (target.TeachingRoiId is { } roiId)
        {
            RecordTrainedRoiAssignment(roiId, target.TeachingRoiName);
        }
    }

    public void RemovePattern(RegisteredPatternItem? item)
    {
        var target = item ?? _selectedRegisteredPattern;
        if (target is null || !IsIdle)
        {
            return;
        }

        target.StateChanged -= OnRegisteredPatternStateChanged;
        RegisteredPatterns.Remove(target);
        ActiveMethodPatterns.Remove(target);
        _patternFileStore.DeletePatternImageFile(target.Id);
        ReindexMethodPatterns(target.Method);
        PersistPatternLibrarySnapshot();

        if (target.Method == Method)
        {
            if (ActiveMethodPatterns.Count > 0)
            {
                var next = ActiveMethodPatterns[^1];
                _suppressPatternSelectionSync = true;
                try
                {
                    SelectedRegisteredPattern = next;
                }
                finally
                {
                    _suppressPatternSelectionSync = false;
                }
                ApplyPatternToActiveMethodState(next, showPreview: true);
            }
            else
            {
                _suppressPatternSelectionSync = true;
                try
                {
                    SelectedRegisteredPattern = null;
                }
                finally
                {
                    _suppressPatternSelectionSync = false;
                }
                ClearActiveMethodStateCore(disposePatMax: false);
            }
        }

        if (target.PatMaxTemplate is { } pm && !RegisteredPatterns.Any(p => ReferenceEquals(p.PatMaxTemplate, pm)))
        {
            if (ReferenceEquals(_patMaxState.Template, pm))
            {
                _patMaxState.Template = null;
            }
            target.Dispose();
        }

        Invalidate();
        NotifyPatternListProperties();
        RefreshCommands();
    }

    private void RegisterOrUpdateTrainedPattern(
        PatternMatchingMethod method,
        Guid? roiId,
        string? roiName,
        RoiGeometry teachingRegion,
        ImmutableArray<MaskRegion> teachingMasks,
        string summary,
        ShapeModel? shapeModel = null,
        ShapeTrainingSettings? shapeTrainedSettings = null,
        PatMaxTrainedTemplate? patMaxTemplate = null,
        PatMaxUiSettings? patMaxSettings = null,
        CpuTemplate? cpuTemplate = null,
        CorrelationTemplate? templateModel = null,
        byte[]? previewGray8 = null,
        int previewWidth = 0,
        int previewHeight = 0)
    {
        bool isDifferentRoiOrLocation = _selectedRegisteredPattern is not null &&
            (_selectedRegisteredPattern.TeachingRoiId != roiId ||
             (_selectedRegisteredPattern.TeachingRegion != teachingRegion && !IsTrainingDirty));
        bool append = _appendNewPatternOnTrain ||
                      _selectedRegisteredPattern is null ||
                      _selectedRegisteredPattern.Method != method ||
                      isDifferentRoiOrLocation;
        PatternRegistrationRole role = _trainRole;

        int existingIndexInAll = !append && _selectedRegisteredPattern is not null
            ? RegisteredPatterns.IndexOf(_selectedRegisteredPattern)
            : -1;
        int existingIndexInActive = !append && _selectedRegisteredPattern is not null
            ? ActiveMethodPatterns.IndexOf(_selectedRegisteredPattern)
            : -1;

        int methodNumber = append
            ? RegisteredPatterns.Count(p => p.Method == method) + 1
            : (_selectedRegisteredPattern?.Index ?? 1);

        int roleSeq = append
            ? RegisteredPatterns.Count(p => p.Method == method && p.Role == role) + 1
            : methodNumber;

        string rolePrefix = role == PatternRegistrationRole.Defect ? "불량" : "정상";
        string roiSuffix = !string.IsNullOrWhiteSpace(roiName) ? $" ({roiName.Trim()})" : "";
        string patternName = !append && _selectedRegisteredPattern is { } prev && string.IsNullOrWhiteSpace(roiName)
            ? prev.Name
            : $"{rolePrefix}-{roleSeq}{roiSuffix}";

        var resolvedPatch = PatternThumbnailRenderer.ResolveOrSynthesizePatch(
            rawGray8: previewGray8,
            rawWidth: previewWidth,
            rawHeight: previewHeight,
            teachingRegion: teachingRegion,
            shapeModel: shapeModel,
            patMaxTemplate: patMaxTemplate,
            cpuTemplate: cpuTemplate,
            templateModel: templateModel);

        var newItem = new RegisteredPatternItem(
            id: !append && _selectedRegisteredPattern is { } old ? old.Id : Guid.NewGuid(),
            index: methodNumber,
            name: patternName,
            method: method,
            role: role,
            teachingRoiId: roiId,
            teachingRoiName: roiName,
            teachingRegion: teachingRegion,
            teachingMasks: teachingMasks,
            summary: summary,
            previewPatch: resolvedPatch,
            onToggleRole: TogglePatternRole,
            onRemove: RemovePattern,
            shapeModel: shapeModel,
            shapeTrainedSettings: shapeTrainedSettings,
            patMaxTemplate: patMaxTemplate,
            patMaxSettings: patMaxSettings,
            cpuTemplate: cpuTemplate,
            templateModel: templateModel);

        newItem.StateChanged += OnRegisteredPatternStateChanged;

        if (!append && existingIndexInAll >= 0 && _selectedRegisteredPattern is { } replaced)
        {
            replaced.StateChanged -= OnRegisteredPatternStateChanged;
            newItem.IsEnabled = replaced.IsEnabled;
            RegisteredPatterns[existingIndexInAll] = newItem;
            if (existingIndexInActive >= 0)
            {
                ActiveMethodPatterns[existingIndexInActive] = newItem;
            }
            if (replaced.PatMaxTemplate is { } oldPm && !ReferenceEquals(oldPm, patMaxTemplate) &&
                !RegisteredPatterns.Any(p => ReferenceEquals(p.PatMaxTemplate, oldPm)))
            {
                replaced.Dispose();
            }
        }
        else
        {
            RegisteredPatterns.Add(newItem);
            if (method == Method)
            {
                ActiveMethodPatterns.Add(newItem);
            }
        }

        ReindexMethodPatterns(method);
        PersistPatternLibrarySnapshot();

        _suppressPatternSelectionSync = true;
        try
        {
            SelectedRegisteredPattern = newItem;
        }
        finally
        {
            _suppressPatternSelectionSync = false;
        }

        NotifyPatternListProperties();
    }

    private void PersistPatternLibrarySnapshot()
    {
        try
        {
            var snapshot = RegisteredPatterns.ToArray();
            _patternFileStore.SaveLibraryInParallelAsync(snapshot).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Ignore non-fatal temp file store errors so in-memory pattern matching still works
        }
    }

    public async Task ReloadPatternImagesInParallelAsync()
    {
        if (RegisteredPatterns.Count == 0)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        var snapshot = RegisteredPatterns.ToArray();
        int loaded = await _patternFileStore.ReloadPatternImagesInParallelAsync(snapshot);
        sw.Stop();
        Status = $"외부 패턴 이미지 {loaded}개 병렬 파일 로드 완료 ({sw.Elapsed.TotalMilliseconds:F1} ms)";
        NotifyPatternListProperties();
    }

    private void RebuildActiveMethodPatterns()
    {
        ActiveMethodPatterns.Clear();
        int idx = 1;
        foreach (var item in RegisteredPatterns)
        {
            if (item.Method == Method)
            {
                item.Index = idx++;
                ActiveMethodPatterns.Add(item);
            }
        }

        _suppressPatternSelectionSync = true;
        try
        {
            SelectedRegisteredPattern = ActiveMethodPatterns.LastOrDefault();
        }
        finally
        {
            _suppressPatternSelectionSync = false;
        }

        NotifyPatternListProperties();
    }

    private void ReindexMethodPatterns(PatternMatchingMethod method)
    {
        int idx = 1;
        foreach (var item in RegisteredPatterns)
        {
            if (item.Method == method)
            {
                item.Index = idx++;
            }
        }
    }

    private void ActivateRegisteredPattern(RegisteredPatternItem item)
    {
        if (item.Method != Method)
        {
            Method = item.Method;
        }
        ApplyPatternToActiveMethodState(item, showPreview: true);
    }

    private void ApplyPatternToActiveMethodState(RegisteredPatternItem item, bool showPreview)
    {
        if (item.Method == PatternMatchingMethod.PatMax && item.PatMaxTemplate is { } pm)
        {
            _patMaxState.Template = pm;
            _patMaxState.TeachingRoiId = item.TeachingRoiId;
            _patMaxState.TeachingMasks = item.TeachingMasks;
            _patMaxState.IsMaskDirty = item.IsMaskDirty;
            if (showPreview)
            {
                PresentPatMaxTrainingPreview(pm);
            }
        }
        else if (_cpuStates.TryGetValue(item.Method, out var cpuState) && item.CpuTemplate is { } cpu)
        {
            cpuState.Template = cpu;
            cpuState.TeachingRoiId = item.TeachingRoiId;
            cpuState.IsMaskDirty = item.IsMaskDirty;
            if (showPreview)
            {
                PresentCpuTrainingPreview(cpu);
            }
        }
        else if (item.Method == PatternMatchingMethod.Shape && item.ShapeModel is { } shape)
        {
            ActiveShapeState.Model = shape;
            ActiveShapeState.TeachingRegion = item.TeachingRegion;
            ActiveShapeState.TrainedSettings = item.ShapeTrainedSettings ?? ActiveShapeState.Training;
            ActiveShapeState.TeachingRoiId = item.TeachingRoiId;
            ActiveShapeState.TeachingMasks = item.TeachingMasks;
            ActiveShapeState.IsMaskDirty = item.IsMaskDirty;
            if (showPreview)
            {
                PresentTrainingPreview(shape, item.TeachingRegion);
            }
        }
        else if (item.Method == PatternMatchingMethod.OpenCvTemplate && item.TemplateModel is { } tpl)
        {
            _templateState.Model = tpl;
            _templateState.TeachingRegion = item.TeachingRegion;
            _templateState.TeachingRoiId = item.TeachingRoiId;
            _templateState.TeachingMasks = item.TeachingMasks;
            _templateState.IsMaskDirty = item.IsMaskDirty;
            if (showPreview)
            {
                PresentTemplateTrainingPreview(tpl, item.TeachingRegion);
            }
        }

        Notify(nameof(ModelSummary));
        Notify(nameof(Model));
        Notify(nameof(TemplateModel));
        Notify(nameof(HasModel));
        Notify(nameof(IsTrainingDirty));
        RefreshCommands();
    }

    private void RemoveAllPatternsForMethod(PatternMatchingMethod method)
    {
        var toRemove = RegisteredPatterns.Where(p => p.Method == method).ToList();
        foreach (var item in toRemove)
        {
            item.StateChanged -= OnRegisteredPatternStateChanged;
            RegisteredPatterns.Remove(item);
            _patternFileStore.DeletePatternImageFile(item.Id);
            item.Dispose();
        }
        PersistPatternLibrarySnapshot();
        if (method == Method)
        {
            ActiveMethodPatterns.Clear();
            _suppressPatternSelectionSync = true;
            try
            {
                SelectedRegisteredPattern = null;
            }
            finally
            {
                _suppressPatternSelectionSync = false;
            }
        }
        NotifyPatternListProperties();
    }

    private void UpdateRegisteredPatternsMaskDirty(DocumentState state)
    {
        foreach (var item in RegisteredPatterns)
        {
            if (item.TeachingRoiId is { } id && state.Rois.TryGetValue(id, out var roi))
            {
                item.IsMaskDirty = item.Method is PatternMatchingMethod.CpuLine2D or PatternMatchingMethod.CpuSubNcc
                    ? !roi.Masks.IsEmpty
                    : !item.TeachingMasks.SequenceEqual(roi.Masks);
            }
        }
    }

    private void OnRegisteredPatternStateChanged()
    {
        Invalidate();
        NotifyPatternListProperties();
        RefreshCommands();
    }

    private void NotifyPatternListProperties()
    {
        Notify(nameof(HasSelectedRegisteredPattern));
        Notify(nameof(RegisteredPatternCount));
        Notify(nameof(RegisteredGoodPatternCount));
        Notify(nameof(RegisteredDefectPatternCount));
        Notify(nameof(EnabledPatternCount));
        Notify(nameof(HasRegisteredPatterns));
        Notify(nameof(HasEnabledRegisteredPatterns));
        Notify(nameof(RegisteredPatternListSummary));
        Notify(nameof(PatternStorageSummaryText));
        _toggleSelectedPatternRoleCommand?.Refresh();
        _removeSelectedPatternCommand?.Refresh();
        _reloadPatternImagesCommand?.Refresh();
    }

    private bool ShouldUseMultiPatternSearch()
    {
        if (ActiveMethodPatterns.Count == 0)
        {
            return false;
        }
        int enabledCount = 0;
        bool anyDefect = false;
        foreach (var p in ActiveMethodPatterns)
        {
            if (p.IsEnabled)
            {
                enabledCount++;
                if (p.IsDefect)
                {
                    anyDefect = true;
                }
            }
        }
        return enabledCount > 1 || anyDefect;
    }

    private sealed record MultiPatternMatchCandidate(
        RegisteredPatternItem Pattern,
        InspectionVerdict Verdict,
        double Score,
        PointD Center,
        double AngleRadians,
        double AngleDegrees,
        double Scale,
        double Width,
        double Height,
        ImmutableArray<PointD> Outline,
        ImmutableArray<PointD> Features,
        ImmutableArray<PointD> KeyFeatures,
        ImmutableArray<PointD> UnmatchedFeatures,
        ImmutableArray<DiagnosticPointGroup> DiagnosticGroups,
        string DetailSuffix);

    private sealed record RegionMultiPatternEvaluation(
        InspectionVerdict Verdict,
        string Reason,
        ImmutableArray<MatchMarker> Markers,
        ImmutableArray<string> DetailRows,
        int GoodCount,
        int DefectCount,
        double TopScore,
        string BestPoseSummary,
        double TotalMilliseconds,
        bool AnyIncomplete);

    private async Task SearchRegisteredPatternsAsync(long startedTimestamp)
    {
        var image = _image!;
        var method = Method;
        var live = _live;
        var enabledPatterns = ActiveMethodPatterns.Where(p => p.IsEnabled).ToImmutableArray();
        if (enabledPatterns.IsEmpty)
        {
            return;
        }

        if (UseSelection && _selection is null && _targetRoiCount > 0)
        {
            await SearchMultipleRoisWithRegisteredPatternsAsync(image, live, method, enabledPatterns);
            return;
        }

        var region = UseSelection ? _searchSelection : null;
        var exclusion = UseSelection ? _selectionExclusion : null;

        await RunAsync(async (token, epoch) =>
        {
            Status = $"등록 패턴 {enabledPatterns.Length}개 (정상 {enabledPatterns.Count(p => p.IsGood)} · 불량 {enabledPatterns.Count(p => p.IsDefect)}) 검사 중…";
            var eval = await EvaluatePatternsOnRegionAsync(image, live, method, enabledPatterns, region, exclusion, roiPrefix: null, token);
            if (!Current(epoch, token, method))
            {
                return;
            }

            Verdict = eval.Verdict;
            _inspectionNotice = eval.Reason;
            var boundary = SearchBoundary(image, region);
            var summaryColor = InspectionResultPresentation.Color(eval.Verdict);
            _result = new ResultOverlay(
                $"{InspectionResultPresentation.Name(eval.Verdict)} · 정상 {eval.GoodCount}개 / 불량 {eval.DefectCount}개",
                summaryColor,
                boundary,
                eval.Markers,
                ShowFeatures)
            {
                BatchLabels = true
            };

            SetResultDetails(
                eval.Reason + $"\n등록 패턴 {enabledPatterns.Length}개 비교 검사 · 좌표: 원본 픽셀",
                eval.DetailRows);

            Status = $"{InspectionResultPresentation.Name(eval.Verdict)} · 정상 {eval.GoodCount}개 / 불량 {eval.DefectCount}개 · 총 {eval.TotalMilliseconds:F1} ms";
            PublishResult();
            RecordSingleRoiPatternResult(
                _selectedRoiId,
                _selectedRoiName,
                region,
                eval.Verdict,
                eval.Markers.Length,
                eval.TopScore,
                eval.BestPoseSummary,
                eval.TotalMilliseconds);
        });
    }

    private async Task SearchMultipleRoisWithRegisteredPatternsAsync(
        ImageReference image,
        bool live,
        PatternMatchingMethod method,
        ImmutableArray<RegisteredPatternItem> enabledPatterns)
    {
        var targets = _state is not null
            ? InspectionSelection.ResolveTargetLeafRois(_state, _lastTransformPreview)
            : [];
        await RunAsync(async (token, epoch) =>
        {
            Status = $"대상 리프 ROI {targets.Length}개 × 등록 패턴 {enabledPatterns.Length}개 검사 중…";
            var allMarkers = ImmutableArray.CreateBuilder<MatchMarker>();
            var rowStrings = new List<string>(targets.Length);
            int passCount = 0;
            int failCount = 0;
            int warnCount = 0;
            double totalMs = 0.0;

            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                var roiId = target.RoiId;
                var roiName = string.IsNullOrWhiteSpace(target.RoiName)
                    ? (roiId is { } id ? $"ROI-{id.ToString()[..4]}" : $"ROI #{i + 1}")
                    : target.RoiName!;
                var searchRegion = target.SearchSelection ?? target.Selection;
                var exclusion = target.Exclusion;

                var eval = await EvaluatePatternsOnRegionAsync(image, live, method, enabledPatterns, searchRegion, exclusion, roiName, token);
                if (!Current(epoch, token, method))
                {
                    return;
                }

                totalMs += eval.TotalMilliseconds;
                AccumulateVerdict(eval.Verdict, ref passCount, ref failCount, ref warnCount);
                allMarkers.AddRange(eval.Markers);

                if (eval.Markers.IsEmpty && searchRegion is not null)
                {
                    var fallbackColor = InspectionResultPresentation.Color(eval.Verdict);
                    var fallbackLabel = eval.Verdict == InspectionVerdict.Pass
                        ? $"{roiName}: OK (불량 미검출)"
                        : $"{roiName}: NG (정상 미검출)";
                    allMarkers.Add(new MatchMarker(
                        searchRegion.Outline.ToImmutableArray(),
                        searchRegion.Center,
                        searchRegion.Angle * Math.PI / 180.0,
                        fallbackLabel,
                        fallbackColor,
                        []));
                }

                string verdictTag = eval.Verdict == InspectionVerdict.Pass ? "OK·정상" : eval.Verdict == InspectionVerdict.Warning ? "WARN" : "NG·불량";
                rowStrings.Add($"[{roiName}] {verdictTag} (정상 {eval.GoodCount} / 불량 {eval.DefectCount}) · {eval.Reason} · 최고점수 {eval.TopScore:F3} · {eval.BestPoseSummary} ({eval.TotalMilliseconds:F1} ms)");
                RecordSingleRoiPatternResult(roiId, roiName, searchRegion, eval.Verdict, eval.Markers.Length, eval.TopScore, eval.BestPoseSummary, eval.TotalMilliseconds);
            }

            Verdict = failCount > 0
                ? InspectionVerdict.Fail
                : warnCount > 0
                    ? InspectionVerdict.Warning
                    : InspectionVerdict.Pass;
            Vector4 summaryColor = InspectionResultPresentation.Color(Verdict.Value);
            string summaryHeader = $"다중 패턴 ROI 검사 ({targets.Length}개) · OK {passCount} / NG {failCount} / WARN {warnCount} · 총 {totalMs:F1} ms";
            _inspectionNotice = summaryHeader;
            _result = new ResultOverlay(
                summaryHeader,
                summaryColor,
                SearchBoundary(image, null),
                allMarkers.ToImmutable(),
                ShowFeatures)
            {
                BatchLabels = true
            };
            SetResultDetails(summaryHeader, rowStrings);
            Status = summaryHeader;
            PublishResult();
        });
    }

    private async Task<RegionMultiPatternEvaluation> EvaluatePatternsOnRegionAsync(
        ImageReference image,
        bool live,
        PatternMatchingMethod method,
        ImmutableArray<RegisteredPatternItem> enabledPatterns,
        RoiGeometry? searchRegion,
        RoiExclusion? exclusion,
        string? roiPrefix,
        CancellationToken token)
    {
        var rawCandidates = new List<MultiPatternMatchCandidate>();
        double totalMs = 0.0;
        bool anyIncomplete = false;
        var rotation = method == PatternMatchingMethod.Shape ? ActiveRotationAcceptance : null;

        foreach (var pattern in enabledPatterns)
        {
            if (method == PatternMatchingMethod.Shape && pattern.ShapeModel is { } shapeModel)
            {
                var options = CreateShapeSearchOptions();
                if (options.ComputeMode == InspectionComputeMode.GpuPreprocessing && _service is IShapeComputePreparation prep)
                {
                    await prep.PrepareGpuAsync(token);
                }
                var output = await _service.SearchAsync(image, live, shapeModel, searchRegion, options, token, exclusion);
                totalMs += output.TotalMilliseconds;
                anyIncomplete |= output.Truncated || output.MatchLimitExceeded;
                var teachingRegion = pattern.TeachingRegion;

                foreach (var match in output.Matches)
                {
                    var pose = match.Pose;
                    var deg = RoiGeometry.NormalizeAngle(pose.Angle * 180.0 / Math.PI);
                    var cos = Math.Cos(pose.Angle) * pose.Scale;
                    var sin = Math.Sin(pose.Angle) * pose.Scale;
                    var features = shapeModel.Levels[0].Features.Select(f => new PointD(
                        pose.X + (cos * f.X) - (sin * f.Y),
                        pose.Y + (sin * f.X) + (cos * f.Y))).ToImmutableArray();
                    var keyFeatures = match.KeyMatchedFeatures.IsDefaultOrEmpty
                        ? []
                        : match.KeyMatchedFeatures.Select(p => new PointD(p.X, p.Y)).ToImmutableArray();
                    var unmatchedFeatures = match.UnmatchedFeatures.IsDefaultOrEmpty
                        ? []
                        : match.UnmatchedFeatures.Select(p => new PointD(p.X, p.Y)).ToImmutableArray();
                    var detectedRegion = teachingRegion.WithFrame(
                        new(pose.X, pose.Y),
                        teachingRegion.Width * pose.Scale,
                        teachingRegion.Height * pose.Scale,
                        deg);

                    InspectionVerdict matchVerdict;
                    string detailSuffix = "";
                    if (pattern.IsDefect)
                    {
                        matchVerdict = InspectionVerdict.Fail;
                        detailSuffix = "불량 패턴 일치";
                    }
                    else
                    {
                        var rotDecision = InspectionDecision.Evaluate(match, rotation, shapeModel.TrainingReferenceAngle);
                        matchVerdict = rotDecision.Verdict == InspectionVerdict.Fail
                            ? InspectionVerdict.Fail
                            : InspectionVerdict.Pass;
                        if (rotation is not null)
                        {
                            detailSuffix = $"Δ{RotationAcceptance.DeviationDegrees(pose.Angle, shapeModel.TrainingReferenceAngle):F1}°";
                        }
                    }

                    rawCandidates.Add(new MultiPatternMatchCandidate(
                        Pattern: pattern,
                        Verdict: matchVerdict,
                        Score: match.Score,
                        Center: new(pose.X, pose.Y),
                        AngleRadians: pose.Angle,
                        AngleDegrees: deg,
                        Scale: pose.Scale,
                        Width: teachingRegion.Width * pose.Scale,
                        Height: teachingRegion.Height * pose.Scale,
                        Outline: detectedRegion.Outline.ToImmutableArray(),
                        Features: features,
                        KeyFeatures: keyFeatures,
                        UnmatchedFeatures: unmatchedFeatures,
                        DiagnosticGroups: [],
                        DetailSuffix: detailSuffix));
                }
            }
            else if (method == PatternMatchingMethod.PatMax && pattern.PatMaxTemplate is { } patMaxTemplate)
            {
                var searchParams = _patMaxState.Settings.ToSearchParams(MinimumScore, MaximumMatches);
                var output = await _patMaxService!.SearchAsync(image, live, patMaxTemplate, searchRegion, searchParams, token, exclusion);
                totalMs += output.TotalMilliseconds;
                anyIncomplete |= output.MatchLimitExceeded;
                var rawPoints = patMaxTemplate.PreviewPoints;

                foreach (var match in output.Matches)
                {
                    var rad = match.AngleDegrees * Math.PI / 180.0;
                    var cos = Math.Cos(rad) * match.Scale;
                    var sin = Math.Sin(rad) * match.Scale;
                    var features = rawPoints.IsDefaultOrEmpty
                        ? []
                        : rawPoints.Select(p => new PointD(match.X + (cos * p.X) - (sin * p.Y), match.Y + (sin * p.X) + (cos * p.Y))).ToImmutableArray();
                    var w = patMaxTemplate.TeachingRegion.Width * match.Scale;
                    var h = patMaxTemplate.TeachingRegion.Height * match.Scale * match.AspectRatio;
                    var outline = new RoiGeometry(new(match.X, match.Y), w, h, match.AngleDegrees).Outline.ToImmutableArray();

                    rawCandidates.Add(new MultiPatternMatchCandidate(
                        Pattern: pattern,
                        Verdict: pattern.IsDefect ? InspectionVerdict.Fail : InspectionVerdict.Pass,
                        Score: match.Score,
                        Center: new(match.X, match.Y),
                        AngleRadians: rad,
                        AngleDegrees: match.AngleDegrees,
                        Scale: match.Scale,
                        Width: w,
                        Height: h,
                        Outline: outline,
                        Features: features,
                        KeyFeatures: [],
                        UnmatchedFeatures: [],
                        DiagnosticGroups: [],
                        DetailSuffix: $"Cov {match.Coverage:P0}"));
                }
            }
            else if (IsCpuMethod && pattern.CpuTemplate is { } cpuTemplate)
            {
                var options = CreateCpuOptions();
                var useCuda = ComputeMode == InspectionComputeMode.GpuPreprocessing;
                var output = await _cpuService!.SearchAsync(image, live, cpuTemplate, searchRegion, options, token, exclusion, useCuda);
                totalMs += output.TotalMilliseconds;
                var res = output.Result;
                anyIncomplete |= res.CandidatesTruncated || res.MatchLimitExceeded;
                var rawPoints = cpuTemplate.Matcher.PreviewPoints;

                foreach (var match in res.Matches)
                {
                    var rad = match.AngleDegrees * Math.PI / 180.0;
                    var cos = Math.Cos(rad) * match.Scale;
                    var sin = Math.Sin(rad) * match.Scale;
                    var features = rawPoints.Count == 0
                        ? []
                        : rawPoints.Select(p => new PointD(match.X + (cos * p.X) - (sin * p.Y), match.Y + (sin * p.X) + (cos * p.Y))).ToImmutableArray();
                    var w = cpuTemplate.TeachingRegion.Width * match.Scale;
                    var h = cpuTemplate.TeachingRegion.Height * match.Scale;
                    var outline = new RoiGeometry(new(match.X, match.Y), w, h, match.AngleDegrees).Outline.ToImmutableArray();

                    rawCandidates.Add(new MultiPatternMatchCandidate(
                        Pattern: pattern,
                        Verdict: pattern.IsDefect ? InspectionVerdict.Fail : InspectionVerdict.Pass,
                        Score: match.Score,
                        Center: new(match.X, match.Y),
                        AngleRadians: rad,
                        AngleDegrees: match.AngleDegrees,
                        Scale: match.Scale,
                        Width: w,
                        Height: h,
                        Outline: outline,
                        Features: features,
                        KeyFeatures: [],
                        UnmatchedFeatures: [],
                        DiagnosticGroups: [],
                        DetailSuffix: ""));
                }
            }
            else if (method == PatternMatchingMethod.OpenCvTemplate && pattern.TemplateModel is { } tplModel)
            {
                var options = CreateTemplateSearchOptions();
                var output = await _templateService!.SearchAsync(image, live, tplModel, searchRegion, options, token, exclusion);
                totalMs += output.TotalMilliseconds;
                anyIncomplete |= output.Truncated || output.MatchLimitExceeded;
                var teachingRegion = pattern.TeachingRegion;

                foreach (var match in output.Matches)
                {
                    var refPt = new PointD(match.ReferencePoint.X, match.ReferencePoint.Y);
                    var detectedRegion = teachingRegion.WithFrame(refPt, teachingRegion.Width, teachingRegion.Height, teachingRegion.Angle);

                    rawCandidates.Add(new MultiPatternMatchCandidate(
                        Pattern: pattern,
                        Verdict: pattern.IsDefect ? InspectionVerdict.Fail : InspectionVerdict.Pass,
                        Score: match.Score,
                        Center: refPt,
                        AngleRadians: teachingRegion.Angle * Math.PI / 180.0,
                        AngleDegrees: teachingRegion.Angle,
                        Scale: 1.0,
                        Width: teachingRegion.Width,
                        Height: teachingRegion.Height,
                        Outline: detectedRegion.Outline.ToImmutableArray(),
                        Features: [],
                        KeyFeatures: [],
                        UnmatchedFeatures: [],
                        DiagnosticGroups: [],
                        DetailSuffix: "NCC"));
                }
            }
        }

        // Cross-pattern NMS: 동일 위치에서 정상 패턴과 불량 패턴(또는 서로 다른 등록 패턴)이 동시에 후보를 낸 경우 최고 점수 패턴이 승리한다.
        var surviving = ResolveCrossPatternCandidates(rawCandidates, MaximumMatches);

        int defectHits = surviving.Count(c => c.Pattern.IsDefect);
        int goodHits = surviving.Count(c => c.Pattern.IsGood && c.Verdict == InspectionVerdict.Pass);
        int rotationFailHits = surviving.Count(c => c.Pattern.IsGood && c.Verdict == InspectionVerdict.Fail);
        bool hasGoodRegistered = enabledPatterns.Any(p => p.IsGood);
        bool hasDefectRegistered = enabledPatterns.Any(p => p.IsDefect);

        InspectionVerdict overallVerdict;
        string reason;
        if (defectHits > 0)
        {
            overallVerdict = InspectionVerdict.Fail;
            var bestDefect = surviving.First(c => c.Pattern.IsDefect);
            reason = $"NG · 불량 패턴 검출 {defectHits}개 (최고: {bestDefect.Pattern.Name} {bestDefect.Score:F3})";
        }
        else if (rotationFailHits > 0)
        {
            overallVerdict = InspectionVerdict.Fail;
            reason = $"NG · 회전 허용각 초과 {rotationFailHits}개 검출";
        }
        else if (anyIncomplete)
        {
            overallVerdict = InspectionVerdict.Warning;
            reason = "일부 패턴 검색이 후보 예산 또는 최대 결과 한도에 도달했습니다.";
        }
        else if (hasGoodRegistered)
        {
            if (goodHits > 0)
            {
                overallVerdict = InspectionVerdict.Pass;
                var bestGood = surviving.First(c => c.Pattern.IsGood);
                reason = $"OK · 정상 패턴 {goodHits}개 검출 (최고: {bestGood.Pattern.Name} {bestGood.Score:F3})" +
                         (hasDefectRegistered ? " · 불량 패턴 미검출" : "");
            }
            else
            {
                overallVerdict = InspectionVerdict.Fail;
                reason = "NG · 등록된 정상(OK) 패턴 미검출";
            }
        }
        else
        {
            overallVerdict = InspectionVerdict.Pass;
            reason = "OK · 등록된 불량(NG) 패턴 미검출 (정상)";
        }

        var markers = ImmutableArray.CreateBuilder<MatchMarker>(surviving.Count);
        var rows = ImmutableArray.CreateBuilder<string>(surviving.Count);

        for (int i = 0; i < surviving.Count; i++)
        {
            var c = surviving[i];
            string roleTag = c.Pattern.IsDefect
                ? "불량·NG"
                : c.Verdict == InspectionVerdict.Fail
                    ? "회전·NG"
                    : "정상·OK";
            var markerColor = InspectionResultPresentation.Color(c.Verdict);
            string prefix = string.IsNullOrWhiteSpace(roiPrefix) ? $"#{i + 1}" : $"{roiPrefix} #{i + 1}";
            string label = $"{prefix} [{roleTag}] {c.Pattern.Name} ({c.Score:F3})";

            markers.Add(new MatchMarker(
                c.Outline,
                c.Center,
                c.AngleRadians,
                label,
                markerColor,
                c.Features)
            {
                KeyFeatures = c.KeyFeatures,
                UnmatchedFeatures = c.UnmatchedFeatures,
                DiagnosticGroups = c.DiagnosticGroups
            });

            string suffix = string.IsNullOrEmpty(c.DetailSuffix) ? "" : $" · {c.DetailSuffix}";
            rows.Add($"{i + 1} · [{roleTag}] {c.Pattern.Name} · 점수 {c.Score:F3} · X {c.Center.X:F2} / Y {c.Center.Y:F2} · 각도 {c.AngleDegrees:F2}° · 배율 {c.Scale:F3}{suffix}");
        }

        double topScore = surviving.Count > 0 ? surviving[0].Score : 0.0;
        string bestPose = surviving.Count > 0
            ? $"[{surviving[0].Pattern.RoleShortBadge}:{surviving[0].Pattern.Name}] X={surviving[0].Center.X:F2}, Y={surviving[0].Center.Y:F2}, θ={surviving[0].AngleDegrees:F2}°"
            : (overallVerdict == InspectionVerdict.Pass ? "불량 미검출(OK)" : "검출 없음(NG)");

        return new RegionMultiPatternEvaluation(
            Verdict: overallVerdict,
            Reason: reason,
            Markers: markers.MoveToImmutable(),
            DetailRows: rows.MoveToImmutable(),
            GoodCount: goodHits,
            DefectCount: defectHits,
            TopScore: topScore,
            BestPoseSummary: bestPose,
            TotalMilliseconds: totalMs,
            AnyIncomplete: anyIncomplete);
    }

    private static List<MultiPatternMatchCandidate> ResolveCrossPatternCandidates(
        List<MultiPatternMatchCandidate> candidates,
        int maxMatches)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var sorted = candidates.OrderByDescending(c => c.Score).ToList();
        var surviving = new List<MultiPatternMatchCandidate>(Math.Min(sorted.Count, Math.Max(1, maxMatches)));

        foreach (var cand in sorted)
        {
            bool suppressed = false;
            foreach (var accepted in surviving)
            {
                if (accepted.Pattern.Id == cand.Pattern.Id)
                {
                    continue;
                }
                double minDim = Math.Min(Math.Min(cand.Width, cand.Height), Math.Min(accepted.Width, accepted.Height));
                double radius = Math.Max(4.0, 0.45 * minDim);
                double dx = cand.Center.X - accepted.Center.X;
                double dy = cand.Center.Y - accepted.Center.Y;
                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    suppressed = true;
                    break;
                }
            }

            if (!suppressed)
            {
                surviving.Add(cand);
                if (surviving.Count >= maxMatches)
                {
                    break;
                }
            }
        }

        return surviving;
    }
}
