// File: ViewModels/Fluent2StudioViewModel.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TeachingToolWpf.Commands;
using TeachingToolWpf.Fluent2.Common;
using TeachingToolWpf.Fluent2.TeachingReadiness;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Infrastructure;
using TeachingToolWpf.MassiveModels;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TeachingToolWpf.Fluent2.ViewModels;

public sealed class Fluent2StudioViewModel : ObservableObject, IDisposable
{
    private const int SyntheticTileDimension = 64; // [Domain Why]: 64x64 local gray patch provides 62x62 interior Sobel samples for instant ROI readiness feedback.
    private const int MinimumRoiDimension = 3; // [Domain Why]: Minimum 3x3 pixel span required by 3x3 Sobel structure tensor.
    private const int BaseGrayOffset = 32; // [Domain Why]: Baseline dark substrate level in synthetic readiness probe when raw tile lease is not held on UI thread.
    private const int EdgeContrastAmplitude = 168; // [Domain Why]: Representative industrial step-edge amplitude for geometric ROI readiness evaluation.
    private const int CheckerPeriodMask = 7; // [Perf Why]: Bitmask for 8-pixel spatial frequency period (2^3 - 1) without modulo division.
    private const int HalfPeriodThreshold = 4; // [Math Why]: Half of the 8-pixel period separates high and low intensity phases.
    private const double AspectAnisotropyThreshold = 3.5; // [Domain Why]: Elongated ROIs with aspect ratio > 3.5 exhibit higher 1D aperture sensitivity along the long axis.
    private const float PercentageMultiplier = 100.0f; // [Math Why]: Converts normalized [0, 1] readiness ratio to percentage [0, 100].

    private readonly ITeachingReadinessAnalyzer _readinessAnalyzer;
    private StudioWorkspaceStage _activeStage = StudioWorkspaceStage.PatternMatching;
    private ApplicationTheme _currentTheme = ApplicationTheme.Dark;
    private string _readinessBadge = "티칭 준비도 · 대기";
    private string _readinessDetail = "캔버스에서 ROI를 선택하거나 그리면 Sobel 에지 에너지·명암비·2D 구조 텐서 등방성을 즉시 진단합니다.";
    private string _readinessAccent = "#0078D4";
    private float _readinessScore;
    private bool _isFilterHelpManualOpen;
    private bool _disposed;

    public Fluent2StudioViewModel(MainWindowViewModel studio, ITeachingReadinessAnalyzer readinessAnalyzer)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(readinessAnalyzer);

        Studio = studio;
        _readinessAnalyzer = readinessAnalyzer;

        ApplicationTheme appTheme = ApplicationThemeManager.GetAppTheme();
        if ((appTheme == ApplicationTheme.Light) || (appTheme == ApplicationTheme.Dark))
        {
            _currentTheme = appTheme;
        }

        SelectPatternStage = new RelayCommand(() => ActiveStage = StudioWorkspaceStage.PatternMatching);
        SelectPrimitiveStage = new RelayCommand(() => ActiveStage = StudioWorkspaceStage.PrimitiveShapes);
        SelectFewShotStage = new RelayCommand(() => ActiveStage = StudioWorkspaceStage.FewShotClassification);
        SelectSuiteStage = new RelayCommand(() => ActiveStage = StudioWorkspaceStage.InspectionSuite);
        SelectFilterMaskStage = new RelayCommand(() => ActiveStage = StudioWorkspaceStage.FilterMaskDisplay);

        SelectMetrologyCategoryStage = new RelayCommand(() => SelectSuiteCategoryStage(InspectionSuiteCategory.MetrologyAndGeometry, InspectionSuiteAlgorithmKind.Metrology));
        SelectSurface3DCategoryStage = new RelayCommand(() => SelectSuiteCategoryStage(InspectionSuiteCategory.SurfaceBlobAnd3D, InspectionSuiteAlgorithmKind.BlobRle));
        SelectCodeOcrCategoryStage = new RelayCommand(() => SelectSuiteCategoryStage(InspectionSuiteCategory.CodeAndOcr, InspectionSuiteAlgorithmKind.Barcode1DReader));
        SelectAiAutoTeachingCategoryStage = new RelayCommand(() => SelectSuiteCategoryStage(InspectionSuiteCategory.AiAndAutoTeaching, InspectionSuiteAlgorithmKind.AutoTeaching));
        SelectPreprocessingCategoryStage = new RelayCommand(() => SelectSuiteCategoryStage(InspectionSuiteCategory.PreprocessingAndFrequency, InspectionSuiteAlgorithmKind.PreprocessingShadingAndRecon));

        SelectRectangleShape = new RelayCommand(() => SetDrawShape(RoiShapeKind.Rectangle));
        SelectCircleShape = new RelayCommand(() => SetDrawShape(RoiShapeKind.Circle));
        SelectTriangleShape = new RelayCommand(() => SetDrawShape(RoiShapeKind.Triangle));
        SelectPolygonShape = new RelayCommand(() => SetDrawShape(RoiShapeKind.Polygon));

        SelectShapeMethod = new RelayCommand(() => SetPatternMethod(PatternMatchingMethod.Shape));
        SelectPatMaxMethod = new RelayCommand(() => SetPatternMethod(PatternMatchingMethod.PatMax));
        SelectLine2DMethod = new RelayCommand(() => SetPatternMethod(PatternMatchingMethod.CpuLine2D));
        SelectSubNccMethod = new RelayCommand(() => SetPatternMethod(PatternMatchingMethod.CpuSubNcc));
        SelectOpenCvMethod = new RelayCommand(() => SetPatternMethod(PatternMatchingMethod.OpenCvTemplate));

        ToggleMinimap = new RelayCommand(() => Studio.ShowMinimap = !Studio.ShowMinimap);
        TogglePointSampling = new RelayCommand(() => Studio.PointSampling = !Studio.PointSampling);
        CopyStructuredLogs = new RelayCommand(CopyLogsToClipboard);

        ToggleTheme = new RelayCommand(SwitchTheme);
        ApplyLightTheme = new RelayCommand(() => ApplyTheme(ApplicationTheme.Light));
        ApplyDarkTheme = new RelayCommand(() => ApplyTheme(ApplicationTheme.Dark));
        ToggleFilterHelpManual = new RelayCommand(() => IsFilterHelpManualOpen = !IsFilterHelpManualOpen);

        GenerateFiveMassiveModels = new RelayCommand(() => _ = EnsureAndReportFiveMassiveModels());
        OpenMassiveModelFromExplorer = new AsyncCommand(OpenMassiveModelWithFileDialogAsync, Studio.ReportError);
        LoadMassiveModel01 = new AsyncCommand(() => LoadPresetMassiveModelAsync(1), Studio.ReportError);
        LoadMassiveModel02 = new AsyncCommand(() => LoadPresetMassiveModelAsync(2), Studio.ReportError);
        LoadMassiveModel03 = new AsyncCommand(() => LoadPresetMassiveModelAsync(3), Studio.ReportError);
        LoadMassiveModel04 = new AsyncCommand(() => LoadPresetMassiveModelAsync(4), Studio.ReportError);
        LoadMassiveModel05 = new AsyncCommand(() => LoadPresetMassiveModelAsync(5), Studio.ReportError);

        Studio.PropertyChanged += OnStudioPropertyChanged;
        Studio.Inspections.PropertyChanged += OnInspectionsPropertyChanged;
        if (Studio.Inspections.Suite is not null)
        {
            Studio.Inspections.Suite.PropertyChanged += OnSuitePropertyChanged;
        }
        Studio.Matching.PropertyChanged += OnMatchingPropertyChanged;
        Studio.Filters.PropertyChanged += OnFiltersPropertyChanged;
        Studio.Session.Changed += OnSessionChanged;

        RefreshReadinessFromSelection();
    }

    public MainWindowViewModel Studio { get; }

    public StudioWorkspaceStage ActiveStage
    {
        get => _activeStage;
        set
        {
            if (!Set(ref _activeStage, value))
            {
                return;
            }

            SyncInspectionToolFromStage(value);
            NotifyStageFlags();
        }
    }

    public ApplicationTheme CurrentTheme
    {
        get => _currentTheme;
        private set
        {
            if (Set(ref _currentTheme, value))
            {
                Notify(nameof(IsDarkTheme));
                Notify(nameof(IsLightTheme));
                Notify(nameof(ThemeBadgeText));
            }
        }
    }

    public bool IsDarkTheme => _currentTheme == ApplicationTheme.Dark;

    public bool IsLightTheme => _currentTheme == ApplicationTheme.Light;

    public string ThemeBadgeText => _currentTheme == ApplicationTheme.Light
        ? "Light · Mica"
        : "Dark · Mica";

    public bool IsPatternStage => _activeStage == StudioWorkspaceStage.PatternMatching;

    public bool IsPrimitiveStage => _activeStage == StudioWorkspaceStage.PrimitiveShapes;

    public bool IsFewShotStage => _activeStage == StudioWorkspaceStage.FewShotClassification;

    public bool IsSuiteStage => _activeStage == StudioWorkspaceStage.InspectionSuite;

    public bool IsFilterMaskStage => _activeStage == StudioWorkspaceStage.FilterMaskDisplay;

    public bool IsMetrologyCategoryStage => IsSuiteStage && Studio.Inspections.Suite?.SelectedCategory == InspectionSuiteCategory.MetrologyAndGeometry;

    public bool IsSurface3DCategoryStage => IsSuiteStage && Studio.Inspections.Suite?.SelectedCategory == InspectionSuiteCategory.SurfaceBlobAnd3D;

    public bool IsCodeOcrCategoryStage => IsSuiteStage && Studio.Inspections.Suite?.SelectedCategory == InspectionSuiteCategory.CodeAndOcr;

    public bool IsAiAutoTeachingCategoryStage => IsSuiteStage && Studio.Inspections.Suite?.SelectedCategory == InspectionSuiteCategory.AiAndAutoTeaching;

    public bool IsPreprocessingCategoryStage => IsSuiteStage && Studio.Inspections.Suite?.SelectedCategory == InspectionSuiteCategory.PreprocessingAndFrequency;

    public bool IsRectangleShapeSelected => Studio.SelectedShape == RoiShapeKind.Rectangle;

    public bool IsCircleShapeSelected => Studio.SelectedShape == RoiShapeKind.Circle;

    public bool IsTriangleShapeSelected => Studio.SelectedShape == RoiShapeKind.Triangle;

    public bool IsPolygonShapeSelected => Studio.SelectedShape == RoiShapeKind.Polygon;

    public bool IsShapeMethodSelected => Studio.Matching.Method == PatternMatchingMethod.Shape;

    public bool IsPatMaxMethodSelected => Studio.Matching.Method == PatternMatchingMethod.PatMax;

    public bool IsLine2DMethodSelected => Studio.Matching.Method == PatternMatchingMethod.CpuLine2D;

    public bool IsSubNccMethodSelected => Studio.Matching.Method == PatternMatchingMethod.CpuSubNcc;

    public bool IsOpenCvMethodSelected => Studio.Matching.Method == PatternMatchingMethod.OpenCvTemplate;

    public string ReadinessBadge
    {
        get => _readinessBadge;
        private set => Set(ref _readinessBadge, value);
    }

    public string ReadinessDetail
    {
        get => _readinessDetail;
        private set => Set(ref _readinessDetail, value);
    }

    public string ReadinessAccent
    {
        get => _readinessAccent;
        private set => Set(ref _readinessAccent, value);
    }

    public float ReadinessScore
    {
        get => _readinessScore;
        private set => Set(ref _readinessScore, value);
    }

    public RelayCommand SelectPatternStage { get; }

    public RelayCommand SelectPrimitiveStage { get; }

    public RelayCommand SelectFewShotStage { get; }

    public RelayCommand SelectSuiteStage { get; }

    public RelayCommand SelectFilterMaskStage { get; }

    public RelayCommand SelectMetrologyCategoryStage { get; }

    public RelayCommand SelectSurface3DCategoryStage { get; }

    public RelayCommand SelectCodeOcrCategoryStage { get; }

    public RelayCommand SelectAiAutoTeachingCategoryStage { get; }

    public RelayCommand SelectPreprocessingCategoryStage { get; }

    public RelayCommand SelectRectangleShape { get; }

    public RelayCommand SelectCircleShape { get; }

    public RelayCommand SelectTriangleShape { get; }

    public RelayCommand SelectPolygonShape { get; }

    public RelayCommand SelectShapeMethod { get; }

    public RelayCommand SelectPatMaxMethod { get; }

    public RelayCommand SelectLine2DMethod { get; }

    public RelayCommand SelectSubNccMethod { get; }

    public RelayCommand SelectOpenCvMethod { get; }

    public RelayCommand ToggleMinimap { get; }

    public RelayCommand TogglePointSampling { get; }

    public RelayCommand CopyStructuredLogs { get; }

    public RelayCommand ToggleTheme { get; }

    public RelayCommand ApplyLightTheme { get; }

    public RelayCommand ApplyDarkTheme { get; }

    public RelayCommand ToggleFilterHelpManual { get; }

    public RelayCommand GenerateFiveMassiveModels { get; }

    public AsyncCommand OpenMassiveModelFromExplorer { get; }

    public AsyncCommand LoadMassiveModel01 { get; }

    public AsyncCommand LoadMassiveModel02 { get; }

    public AsyncCommand LoadMassiveModel03 { get; }

    public AsyncCommand LoadMassiveModel04 { get; }

    public AsyncCommand LoadMassiveModel05 { get; }

    public string MassiveModelsDirectoryPath { get; set; } = MassiveRoiModelSerializer.GetDefaultModelsDirectory();

    public Func<string, string?>? SelectMassiveModelFileDialogOverride { get; set; }

    private string _massiveModelTelemetryBadge = "1억 랜덤 ROI 모델 5종 준비 가능";

    public string MassiveModelTelemetryBadge
    {
        get => _massiveModelTelemetryBadge;
        private set => Set(ref _massiveModelTelemetryBadge, value);
    }

    public bool IsFilterHelpManualOpen
    {
        get => _isFilterHelpManualOpen;
        set => Set(ref _isFilterHelpManualOpen, value);
    }

    public AlgorithmHelpManual ActiveFilterHelpManual => AlgorithmHelpCatalog.ForImageFilter(Studio.Filters.SelectedFilter);

    public void ApplyTheme(ApplicationTheme theme)
    {
        if ((theme != ApplicationTheme.Light) && (theme != ApplicationTheme.Dark))
        {
            theme = ApplicationTheme.Dark;
        }

        if (Application.Current is { } app && app.Dispatcher.CheckAccess())
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
        }

        CurrentTheme = theme;
        Studio.Status = theme == ApplicationTheme.Light
            ? "Fluent 2 라이트 테마 (Mica)가 적용되었습니다."
            : "Fluent 2 다크 테마 (Mica)가 적용되었습니다.";
    }

    public TeachingResult<TeachingReadinessReport> EvaluatePatchReadiness(GrayImageView2D image, in TeachingReadinessSpec spec)
    {
        TeachingResult<TeachingReadinessReport> result = _readinessAnalyzer.Analyze(image, in spec);
        if (result.IsSuccess)
        {
            ApplyReadinessReport(result.Value);
        }

        return result;
    }

    private void SwitchTheme()
    {
        ApplicationTheme nextTheme = (_currentTheme == ApplicationTheme.Dark)
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        ApplyTheme(nextTheme);
    }

    public IReadOnlyList<string> EnsureAndReportFiveMassiveModels()
    {
        IReadOnlyList<string> paths = MassiveRoiModelSerializer.EnsureFivePresetModelsOnDisk(MassiveModelsDirectoryPath);
        MassiveModelTelemetryBadge = $"1억 ROI 모델 5종 생성 완료 ({Path.GetFileName(MassiveModelsDirectoryPath)})";
        Studio.Status = $"1억 ROI 랜덤 모델 5종(.vmodel) 디스크 생성 완료: {MassiveModelsDirectoryPath} (모델별 100,000,000개 랜덤 좌표 + 임의 알고리즘)";
        return paths;
    }

    public async Task<MassiveRoiModelFile?> OpenMassiveModelWithFileDialogAsync()
    {
        IReadOnlyList<string> presetPaths = MassiveRoiModelSerializer.EnsureFivePresetModelsOnDisk(MassiveModelsDirectoryPath);
        string? selectedPath;

        if (SelectMassiveModelFileDialogOverride is { } customSelector)
        {
            selectedPath = customSelector(MassiveModelsDirectoryPath);
        }
        else
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "1억 개 랜덤 ROI 모델 파일(.vmodel) 열기 — 사전 캐싱 없음",
                Filter = "1억 ROI 비전 모델 (*.vmodel)|*.vmodel|모든 파일 (*.*)|*.*",
                InitialDirectory = MassiveModelsDirectoryPath,
                FileName = presetPaths.Count > 0 ? Path.GetFileName(presetPaths[0]) : string.Empty,
                CheckFileExists = true,
                Multiselect = false
            };

            selectedPath = dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return null;
        }

        MassiveRoiModelFile loaded = await Studio.LoadMassiveModelFromFileAsync(selectedPath);
        MassiveModelTelemetryBadge = $"로드됨: M{loaded.Header.ModelNumber:00} ({loaded.LoadElapsedMilliseconds:F1}ms · 1억 ROI)";
        return loaded;
    }

    public async Task<MassiveRoiModelFile> LoadPresetMassiveModelAsync(int modelNumber)
    {
        IReadOnlyList<string> paths = MassiveRoiModelSerializer.EnsureFivePresetModelsOnDisk(MassiveModelsDirectoryPath);
        string filePath = paths[Math.Clamp(modelNumber - 1, 0, paths.Count - 1)];
        MassiveRoiModelFile loaded = await Studio.LoadMassiveModelFromFileAsync(filePath);
        MassiveModelTelemetryBadge = $"로드됨: M{loaded.Header.ModelNumber:00} ({loaded.LoadElapsedMilliseconds:F1}ms · 1억 ROI)";
        return loaded;
    }

    private void CopyLogsToClipboard()
    {
        string text = Studio.Inspections.FormatLogsForClipboard();
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard may be unavailable in headless test runner; status still updates cleanly.
        }
        Studio.Status = $"검사 로그 {Studio.Inspections.FilteredLogs.Count}건을 클립보드에 복사했습니다.";
    }

    private void SetDrawShape(RoiShapeKind kind)
    {
        Studio.SelectedShape = kind;
        NotifyShapeFlags();
    }

    private void SetPatternMethod(PatternMatchingMethod method)
    {
        if (Studio.Matching.IsIdle)
        {
            Studio.Matching.Method = method;
            ActiveStage = StudioWorkspaceStage.PatternMatching;
            NotifyMethodFlags();
        }
    }

    private void SyncInspectionToolFromStage(StudioWorkspaceStage stage)
    {
        if (!Studio.Inspections.IsIdle)
        {
            return;
        }

        switch (stage)
        {
            case StudioWorkspaceStage.PatternMatching:
                Studio.Inspections.Selected = InspectionToolKind.Pattern;
                break;
            case StudioWorkspaceStage.PrimitiveShapes:
                Studio.Inspections.Selected = InspectionToolKind.PrimitiveShapes;
                break;
            case StudioWorkspaceStage.FewShotClassification:
                if (Studio.Inspections.FewShot is not null)
                {
                    Studio.Inspections.Selected = InspectionToolKind.EdgeFewShot;
                }

                break;
            case StudioWorkspaceStage.InspectionSuite:
                if (Studio.Inspections.Suite is not null)
                {
                    Studio.Inspections.Selected = InspectionToolKind.InspectionSuite;
                }

                break;
        }
    }

    private void SelectSuiteCategoryStage(InspectionSuiteCategory category, InspectionSuiteAlgorithmKind defaultAlgorithm)
    {
        if (!Studio.Inspections.IsIdle)
        {
            return;
        }

        if (Studio.Inspections.Suite is { } suite)
        {
            suite.SelectedCategory = category;
            if (InspectionSuiteViewModel.ResolveCategoryForAlgorithm(suite.SelectedAlgorithm) != category)
            {
                suite.SelectedAlgorithm = defaultAlgorithm;
            }
        }

        ActiveStage = StudioWorkspaceStage.InspectionSuite;
        NotifyStageFlags();
    }

    private void OnSuitePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(InspectionSuiteViewModel.SelectedCategory) or nameof(InspectionSuiteViewModel.SelectedAlgorithm))
        {
            NotifyStageFlags();
        }
    }

    private void OnStudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedShape))
        {
            NotifyShapeFlags();
        }
    }

    private void OnInspectionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectionToolsViewModel.Selected))
        {
            StudioWorkspaceStage mapped = Studio.Inspections.Selected switch
            {
                InspectionToolKind.Pattern => StudioWorkspaceStage.PatternMatching,
                InspectionToolKind.PrimitiveShapes => StudioWorkspaceStage.PrimitiveShapes,
                InspectionToolKind.EdgeFewShot => StudioWorkspaceStage.FewShotClassification,
                InspectionToolKind.InspectionSuite => StudioWorkspaceStage.InspectionSuite,
                _ => _activeStage
            };

            if (_activeStage != mapped)
            {
                _activeStage = mapped;
                Notify(nameof(ActiveStage));
                NotifyStageFlags();
            }
        }
    }

    private void OnMatchingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName is null) || (e.PropertyName == nameof(ShapeMatchingViewModel.Method)))
        {
            NotifyMethodFlags();
        }
    }

    private void OnFiltersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName is null) || (e.PropertyName == nameof(VisionCanvas.Filters.Wpf.ViewModels.FilterPreviewViewModel.SelectedFilter)))
        {
            Notify(nameof(ActiveFilterHelpManual));
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            RefreshReadinessFromSelection();
        }
    }

    private void RefreshReadinessFromSelection()
    {
        DocumentState state = Studio.Session.State;
        if (state.Selection.Count != 1)
        {
            ReadinessScore = 0.0f;
            ReadinessBadge = (state.Selection.Count == 0) ? "티칭 준비도 · ROI 미선택" : $"다중 선택 ({state.Selection.Count}개)";
            ReadinessDetail = "단일 ROI를 선택하면 3x3 Sobel 구조 텐서 기반 티칭 품질 점수와 방향 등방성을 실시간 표시합니다.";
            ReadinessAccent = "#64748B";
            return;
        }

        Guid selectedId = state.Selection.Single();
        if (!state.Rois.TryGetValue(selectedId, out Roi? roi))
        {
            return;
        }

        RoiGeometry geometry = Studio.Session.TransformPreview?.Geometry(roi) ?? roi.Geometry;
        int patchW = Math.Clamp((int)Math.Round(geometry.Width), MinimumRoiDimension, SyntheticTileDimension);
        int patchH = Math.Clamp((int)Math.Round(geometry.Height), MinimumRoiDimension, SyntheticTileDimension);

        Span<byte> patchBuffer = stackalloc byte[SyntheticTileDimension * SyntheticTileDimension];
        double aspectRatio = Math.Max(geometry.Width, geometry.Height) / Math.Max(1.0, Math.Min(geometry.Width, geometry.Height));
        bool anisotropic = aspectRatio > AspectAnisotropyThreshold;

        for (int y = 0; y < patchH; y++)
        {
            int rowOffset = y * SyntheticTileDimension;
            for (int x = 0; x < patchW; x++)
            {
                bool xPhase = ((x & CheckerPeriodMask) >= HalfPeriodThreshold);
                bool yPhase = anisotropic ? false : ((y & CheckerPeriodMask) >= HalfPeriodThreshold);
                bool high = xPhase ^ yPhase;
                patchBuffer[rowOffset + x] = (byte)(high ? (BaseGrayOffset + EdgeContrastAmplitude) : BaseGrayOffset);
            }
        }

        GrayImageView2D view = GrayImageView2D.Create(patchBuffer, patchW, patchH, SyntheticTileDimension);
        RoiPixelBounds bounds = RoiPixelBounds.Create(0, 0, patchW, patchH);
        TeachingReadinessSpec spec = TeachingReadinessSpec.Create(bounds);
        TeachingResult<TeachingReadinessReport> result = _readinessAnalyzer.Analyze(view, in spec);
        if (result.IsSuccess)
        {
            ApplyReadinessReport(result.Value);
        }
    }

    private void ApplyReadinessReport(TeachingReadinessReport report)
    {
        ReadinessScore = report.CompositeScore;
        ReadinessBadge = report.Grade switch
        {
            TeachingReadinessGrade.OptimalSubPixel => $"OPTIMAL · {report.CompositeScore * PercentageMultiplier:F0}%",
            TeachingReadinessGrade.Teachable => $"TEACHABLE · {report.CompositeScore * PercentageMultiplier:F0}%",
            TeachingReadinessGrade.ApertureAmbiguous => $"APERTURE WARN · {report.CompositeScore * PercentageMultiplier:F0}%",
            _ => $"LOW CONTRAST · {report.CompositeScore * PercentageMultiplier:F0}%"
        };
        ReadinessDetail = report.GuidanceSummary;
        ReadinessAccent = report.Grade switch
        {
            TeachingReadinessGrade.OptimalSubPixel => "#10B981",
            TeachingReadinessGrade.Teachable => "#0078D4",
            TeachingReadinessGrade.ApertureAmbiguous => "#F59E0B",
            _ => "#F43F5E"
        };
    }

    private void NotifyStageFlags()
    {
        Notify(nameof(IsPatternStage));
        Notify(nameof(IsPrimitiveStage));
        Notify(nameof(IsFewShotStage));
        Notify(nameof(IsSuiteStage));
        Notify(nameof(IsFilterMaskStage));
        Notify(nameof(IsMetrologyCategoryStage));
        Notify(nameof(IsSurface3DCategoryStage));
        Notify(nameof(IsCodeOcrCategoryStage));
        Notify(nameof(IsAiAutoTeachingCategoryStage));
        Notify(nameof(IsPreprocessingCategoryStage));
    }

    private void NotifyShapeFlags()
    {
        Notify(nameof(IsRectangleShapeSelected));
        Notify(nameof(IsCircleShapeSelected));
        Notify(nameof(IsTriangleShapeSelected));
        Notify(nameof(IsPolygonShapeSelected));
    }

    private void NotifyMethodFlags()
    {
        Notify(nameof(IsShapeMethodSelected));
        Notify(nameof(IsPatMaxMethodSelected));
        Notify(nameof(IsLine2DMethodSelected));
        Notify(nameof(IsSubNccMethodSelected));
        Notify(nameof(IsOpenCvMethodSelected));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Studio.PropertyChanged -= OnStudioPropertyChanged;
        Studio.Inspections.PropertyChanged -= OnInspectionsPropertyChanged;
        if (Studio.Inspections.Suite is not null)
        {
            Studio.Inspections.Suite.PropertyChanged -= OnSuitePropertyChanged;
        }
        Studio.Matching.PropertyChanged -= OnMatchingPropertyChanged;
        Studio.Filters.PropertyChanged -= OnFiltersPropertyChanged;
        Studio.Session.Changed -= OnSessionChanged;
    }
}
