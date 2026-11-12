using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TeachingToolWpf.Fluent2.Common;
using TeachingToolWpf.Fluent2.TeachingReadiness;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.Fluent2.Views;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.MassiveModels;
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionAlgorithm.OpenSrc.Synthetic8kTestGen;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Xunit;

namespace TeachingToolWpf.Fluent2.Tests;

public sealed class Fluent2StudioViewModelTests(SharedPpOcrFixture ppOcrFixture) : IClassFixture<SharedPpOcrFixture>
{
    private const int TestImageDimension = 1000;
    private const int TestViewportWidth = 1000;
    private const int TestViewportHeight = 700;
    private readonly SharedPpOcrFixture _ppOcrFixture = ppOcrFixture;

    [Fact]
    public void StageSelectionCommands_SynchronizeActiveStageAndInspectionTool()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        Assert.True(viewModel.IsPatternStage);
        Assert.Equal(StudioWorkspaceStage.PatternMatching, viewModel.ActiveStage);

        viewModel.SelectPrimitiveStage.Execute(null);
        Assert.True(viewModel.IsPrimitiveStage);
        Assert.Equal(InspectionToolKind.PrimitiveShapes, studio.Inspections.Selected);

        viewModel.SelectFewShotStage.Execute(null);
        Assert.True(viewModel.IsFewShotStage);
        Assert.Equal(InspectionToolKind.EdgeFewShot, studio.Inspections.Selected);

        viewModel.SelectSuiteStage.Execute(null);
        Assert.True(viewModel.IsSuiteStage);
        Assert.Equal(InspectionToolKind.InspectionSuite, studio.Inspections.Selected);

        viewModel.SelectFilterMaskStage.Execute(null);
        Assert.True(viewModel.IsFilterMaskStage);
        Assert.Equal(StudioWorkspaceStage.FilterMaskDisplay, viewModel.ActiveStage);

        viewModel.SelectPatternStage.Execute(null);
        Assert.True(viewModel.IsPatternStage);
        Assert.Equal(InspectionToolKind.Pattern, studio.Inspections.Selected);
    }

    [Fact]
    public void ThemeCommands_SwitchBetweenDarkAndLightMicaThemes()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        viewModel.ApplyDarkTheme.Execute(null);
        Assert.Equal(ApplicationTheme.Dark, viewModel.CurrentTheme);
        Assert.True(viewModel.IsDarkTheme);
        Assert.False(viewModel.IsLightTheme);
        Assert.Equal("Dark · Mica", viewModel.ThemeBadgeText);

        viewModel.ApplyLightTheme.Execute(null);
        Assert.Equal(ApplicationTheme.Light, viewModel.CurrentTheme);
        Assert.True(viewModel.IsLightTheme);
        Assert.False(viewModel.IsDarkTheme);
        Assert.Equal("Light · Mica", viewModel.ThemeBadgeText);

        viewModel.ToggleTheme.Execute(null);
        Assert.Equal(ApplicationTheme.Dark, viewModel.CurrentTheme);
        Assert.True(viewModel.IsDarkTheme);
    }

    [Fact]
    public void OneClickShapeAndPatternMethodPills_UpdateStudioStateWithoutDropdowns()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        studio.Session.InitializeImage(
            new ImageReference("test", "test", TestImageDimension, TestImageDimension),
            new ViewportTransform(1, default));

        viewModel.SelectCircleShape.Execute(null);
        Assert.True(viewModel.IsCircleShapeSelected);
        Assert.Equal(RoiShapeKind.Circle, studio.SelectedShape);

        viewModel.SelectTriangleShape.Execute(null);
        Assert.True(viewModel.IsTriangleShapeSelected);
        Assert.Equal(RoiShapeKind.Triangle, studio.SelectedShape);

        viewModel.SelectPolygonShape.Execute(null);
        Assert.True(viewModel.IsPolygonShapeSelected);
        Assert.Equal(RoiShapeKind.Polygon, studio.SelectedShape);

        viewModel.SelectRectangleShape.Execute(null);
        Assert.True(viewModel.IsRectangleShapeSelected);
        Assert.Equal(RoiShapeKind.Rectangle, studio.SelectedShape);

        viewModel.SelectLine2DMethod.Execute(null);
        Assert.True(viewModel.IsLine2DMethodSelected);
        Assert.Equal(PatternMatchingMethod.CpuLine2D, studio.Matching.Method);

        viewModel.SelectSubNccMethod.Execute(null);
        Assert.True(viewModel.IsSubNccMethodSelected);
        Assert.Equal(PatternMatchingMethod.CpuSubNcc, studio.Matching.Method);

        viewModel.SelectPatMaxMethod.Execute(null);
        Assert.True(viewModel.IsPatMaxMethodSelected);
        Assert.Equal(PatternMatchingMethod.PatMax, studio.Matching.Method);

        viewModel.SelectOpenCvMethod.Execute(null);
        Assert.True(viewModel.IsOpenCvMethodSelected);
        Assert.Equal(PatternMatchingMethod.OpenCvTemplate, studio.Matching.Method);

        viewModel.SelectShapeMethod.Execute(null);
        Assert.True(viewModel.IsShapeMethodSelected);
        Assert.Equal(PatternMatchingMethod.Shape, studio.Matching.Method);
    }

    [Fact]
    public void ClickingRoi_RevealsAssignedAlgorithmAndSynchronizesInspectionToolAndRowBadges()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        studio.Session.InitializeImage(
            new ImageReference("test", "test", TestImageDimension, TestImageDimension),
            new ViewportTransform(1, default));

        Guid roiA = studio.Session.Add(new RoiGeometry(new PointD(200, 200), 80, 60, 0));
        Guid roiB = studio.Session.Add(new RoiGeometry(new PointD(500, 300), 90, 90, 0));

        // Select ROI A and assign PatMax pattern matching
        studio.Session.Select([roiA]);
        viewModel.SelectPatMaxMethod.Execute(null);
        Assert.True(studio.Inspections.AssignCurrentAlgorithmToSelectedRois.CanExecute(null));
        studio.Inspections.AssignCurrentAlgorithmToSelectedRois.Execute(null);

        RoiRow? rowA = studio.FindRow(roiA);
        Assert.NotNull(rowA);
        Assert.True(rowA.HasAlgorithm);
        Assert.Equal("패턴 · PatMax", rowA.AlgorithmBadge);

        // Select ROI B and assign InspectionSuite (BlobRle)
        studio.Session.Select([roiB]);
        viewModel.SelectSuiteStage.Execute(null);
        studio.Inspections.Suite!.SelectedAlgorithm = InspectionSuiteAlgorithmKind.BlobRle;
        studio.Inspections.AssignCurrentAlgorithmToSelectedRois.Execute(null);

        RoiRow? rowB = studio.FindRow(roiB);
        Assert.NotNull(rowB);
        Assert.True(rowB.HasAlgorithm);
        Assert.Equal("스위트 · 블롭RLE", rowB.AlgorithmBadge);

        // Click ROI A -> Studio automatically switches back to Pattern (PatMax) and shows ROI A's badge
        studio.Session.Select([roiA]);
        Assert.Equal(InspectionToolKind.Pattern, studio.Inspections.Selected);
        Assert.Equal(PatternMatchingMethod.PatMax, studio.Matching.Method);
        Assert.True(viewModel.IsPatternStage);
        Assert.Equal("패턴 · PatMax", studio.Inspections.SelectedRoiAlgorithmBadge);

        // Click ROI B -> Studio automatically switches to InspectionSuite (BlobRle) and shows ROI B's badge
        studio.Session.Select([roiB]);
        Assert.Equal(InspectionToolKind.InspectionSuite, studio.Inspections.Selected);
        Assert.Equal(InspectionSuiteAlgorithmKind.BlobRle, studio.Inspections.Suite.SelectedAlgorithm);
        Assert.True(viewModel.IsSuiteStage);
        Assert.Equal("스위트 · 블롭RLE", studio.Inspections.SelectedRoiAlgorithmBadge);
    }

    [Fact]
    public async Task PatternTraining_RegistersThumbnailInPatternListAndLeftRoiPanel_AndSupportsMultiPatternManagement()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        studio.Session.InitializeImage(
            new ImageReference("sample:1024:1024", "패턴 샘플", 1024, 1024),
            new ViewportTransform(1, default));

        Guid roi1 = studio.Session.Add(new RoiGeometry(new PointD(220, 220), 96, 72, 0));
        Guid roi2 = studio.Session.Add(new RoiGeometry(new PointD(480, 220), 96, 72, 0));

        studio.Session.Select([roi1]);
        viewModel.SelectPatMaxMethod.Execute(null);
        Assert.True(studio.Matching.Train.CanExecute(null));
        await studio.Matching.Train.ExecuteAsync();

        Assert.Single(studio.Matching.ActiveMethodPatterns);
        RegisteredPatternItem firstPattern = studio.Matching.ActiveMethodPatterns[0];
        Assert.True(firstPattern.HasThumbnail);
        Assert.NotNull(firstPattern.ThumbnailImage);
        Assert.True(firstPattern.IsGood);

        RoiRow? row1 = studio.FindRow(roi1);
        Assert.NotNull(row1);
        Assert.True(row1.HasAlgorithm);
        Assert.True(row1.HasPatternThumbnail);
        Assert.NotNull(row1.PatternThumbnail);
        Assert.True(studio.Inspections.SelectedRoiHasPatternThumbnail);
        Assert.NotNull(studio.Inspections.SelectedRoiPatternThumbnail);

        studio.Session.Select([roi2]);
        await studio.Matching.Train.ExecuteAsync();

        Assert.Equal(2, studio.Matching.ActiveMethodPatterns.Count);
        RegisteredPatternItem secondPattern = studio.Matching.ActiveMethodPatterns[1];
        Assert.True(secondPattern.HasThumbnail);
        Assert.NotNull(secondPattern.ThumbnailImage);

        RoiRow? row2 = studio.FindRow(roi2);
        Assert.NotNull(row2);
        Assert.True(row2.HasPatternThumbnail);

        secondPattern.ToggleRoleCommand.Execute(null);
        Assert.True(secondPattern.IsDefect);
        Assert.Contains("불량", secondPattern.DisplayTitle, StringComparison.Ordinal);

        firstPattern.RemoveCommand.Execute(null);
        Assert.Single(studio.Matching.ActiveMethodPatterns);
        Assert.Same(secondPattern, studio.Matching.ActiveMethodPatterns[0]);
    }

    [Fact]
    public async Task ExecuteInspection_WithMultipleOrZeroSelectedRois_InspectsEachLeafRoiAndUpdatesStructuredLogs()
    {
        RecordingPrimitiveService primitiveService = new RecordingPrimitiveService();
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display, primitiveService);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        studio.Session.InitializeImage(
            new ImageReference("test", "test", TestImageDimension, TestImageDimension),
            new ViewportTransform(1, default));

        Guid roi1 = studio.Session.Add(new RoiGeometry(new PointD(160, 160), 80, 80, 0));
        Guid roi2 = studio.Session.Add(new RoiGeometry(new PointD(420, 260), 90, 90, 0));
        Guid roi3 = studio.Session.Add(new RoiGeometry(new PointD(680, 360), 70, 70, 0));

        viewModel.SelectPrimitiveStage.Execute(null);
        studio.Inspections.Shapes.UseSelection = true;

        // Case 1: Multiple ROIs selected (roi1 and roi2) -> inspects both individually
        studio.Session.Select([roi1, roi2]);
        Assert.True(studio.Inspections.Shapes.Detect.CanExecute(null));
        await studio.Inspections.Shapes.Detect.ExecuteAsync();

        Assert.Equal(2, primitiveService.InspectedRegions.Count);
        Assert.Equal(2, studio.Inspections.TotalLogCount);
        Assert.Equal("PASS", studio.FindRow(roi1)?.VerdictBadge);
        Assert.Equal("PASS", studio.FindRow(roi2)?.VerdictBadge);

        // Case 2: Zero ROIs selected (0 selected) -> inspects all 3 leaf ROIs in document individually
        primitiveService.InspectedRegions.Clear();
        studio.Session.Select([]);
        Assert.Equal(0, studio.Session.State.SelectionCount);
        Assert.True(studio.Inspections.ExecuteActiveToolOnRois.CanExecute(null));

        await studio.Inspections.ExecuteActiveToolOnRois.ExecuteAsync();

        Assert.Equal(3, primitiveService.InspectedRegions.Count);
        Assert.Equal(5, studio.Inspections.TotalLogCount);
        Assert.Equal("PASS", studio.FindRow(roi3)?.VerdictBadge);

        // Verify structured log filter commands
        studio.Inspections.FilterPassLogs.Execute(null);
        Assert.True(studio.Inspections.IsLogFilterPass);
        Assert.Equal(5, studio.Inspections.FilteredLogs.Count);

        studio.Inspections.FilterFailWarnLogs.Execute(null);
        Assert.True(studio.Inspections.IsLogFilterFailWarn);
        Assert.Empty(studio.Inspections.FilteredLogs);

        studio.Session.Select([roi3]);
        studio.Inspections.FilterSelectedRoiLogs.Execute(null);
        Assert.True(studio.Inspections.IsLogFilterSelectedRoi);
        Assert.Single(studio.Inspections.FilteredLogs);

        studio.Inspections.FilterAllLogs.Execute(null);
        Assert.Equal(5, studio.Inspections.FilteredLogs.Count);
        Assert.Contains("도형·마크 검출", studio.Inspections.FormatLogsForClipboard(), StringComparison.Ordinal);

        studio.Inspections.ClearStructuredLogs.Execute(null);
        Assert.Equal(0, studio.Inspections.TotalLogCount);
        Assert.Empty(studio.Inspections.FilteredLogs);
    }

    [Fact]
    public void ReadinessHud_UpdatesAutomaticallyOnRoiSelectionAndDirectBufferAnalysis()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        studio.Session.InitializeImage(
            new ImageReference("test", "test", TestImageDimension, TestImageDimension),
            new ViewportTransform(1, default));

        Assert.Equal(0.0f, viewModel.ReadinessScore, 3);

        studio.Session.Add(new RoiGeometry(new PointD(240, 180), 96, 80, 0));

        Assert.True(viewModel.ReadinessScore >= 0.72f);
        Assert.Contains("OPTIMAL", viewModel.ReadinessBadge, StringComparison.Ordinal);

        byte[] flatBuffer = new byte[64 * 64];
        GrayImageView2D flatView = GrayImageView2D.Create(flatBuffer, 64, 64, 64);
        RoiPixelBounds bounds = RoiPixelBounds.Create(4, 4, 48, 48);
        TeachingReadinessSpec spec = TeachingReadinessSpec.Create(bounds);

        TeachingResult<TeachingReadinessReport> report = viewModel.EvaluatePatchReadiness(flatView, in spec);
        Assert.True(report.IsSuccess);
        Assert.Equal(TeachingReadinessGrade.InsufficientContrast, report.Value.Grade);
        Assert.Equal(0.0f, viewModel.ReadinessScore, 3);
        Assert.Contains("LOW CONTRAST", viewModel.ReadinessBadge, StringComparison.Ordinal);
    }

    [Fact]
    public void Fluent2XamlViews_ContainZeroExpanderControls()
    {
        string projectDir = FindFluent2ProjectDirectory();
        string[] xamlFiles = Directory.GetFiles(projectDir, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(xamlFiles);

        foreach (string xamlFile in xamlFiles)
        {
            string content = File.ReadAllText(xamlFile);
            Assert.DoesNotContain("<Expander", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("</Expander>", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Fluent2AlgorithmCards_EquipAllNumericParametersWithSliders()
    {
        string viewsDir = Path.Combine(FindFluent2ProjectDirectory(), "Views");

        Assert.True(CountSubstrings(File.ReadAllText(Path.Combine(viewsDir, "FluentPatternCard.xaml")), "<Slider ") >= 40);
        Assert.Equal(7, CountSubstrings(File.ReadAllText(Path.Combine(viewsDir, "FluentPrimitiveCard.xaml")), "<Slider "));
        Assert.Equal(5, CountSubstrings(File.ReadAllText(Path.Combine(viewsDir, "FluentFewShotCard.xaml")), "<Slider "));
        Assert.True(CountSubstrings(File.ReadAllText(Path.Combine(viewsDir, "FluentSuiteCard.xaml")), "<Slider ") >= 10);

    }

    [Fact]
    public async Task ElevenSuiteAlgorithms_LoadSamplesAndExecuteCornerOcrAndAutoTeachingInFluent2Studio()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        InspectionSuiteViewModel suite = Assert.IsType<InspectionSuiteViewModel>(studio.Inspections.Suite);
        Assert.True(suite.Algorithms.Count >= 12);

        // 1. Default sample loading (UI button behavior) must NOT auto-create ROIs or auto-execute
        await studio.LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind.CornerAlignment);
        Assert.True(viewModel.IsSuiteStage);
        Assert.True(suite.IsCornerAlignmentSelected);
        Assert.Equal(0, studio.Session.State.RoiCount);

        // 2. Preset ROI verification (placePresetRois: true) verifies the algorithm execution
        await studio.LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind.CornerAlignment, placePresetRois: true);
        Assert.True(viewModel.IsSuiteStage);
        Assert.True(suite.IsCornerAlignmentSelected);
        Assert.Equal(4, studio.Session.State.RoiCount);
        Assert.NotNull(suite.LastOutput);
        Assert.All(suite.LastOutput!.Items, item =>
        {
            Assert.True(item.IsPass, item.DetailLine);
            Assert.Contains("Line1=", item.DetailLine, StringComparison.Ordinal);
            Assert.Contains("Line2=", item.DetailLine, StringComparison.Ordinal);
            Assert.Contains("내각=", item.DetailLine, StringComparison.Ordinal);
            Assert.DoesNotContain("내각=90.000° (직각편차=0.000°)", item.DetailLine, StringComparison.Ordinal);
            Assert.True(item.DefectOrOutlierPoints.Length > 0, $"Expected chamfer/defect outlier points to be rejected and recorded for {item.Name}, got 0.");
        });

        await studio.LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind.PpOcrOnnx, placePresetRois: true);
        Assert.True(suite.IsPpOcrOnnxSelected);
        Assert.True(suite.IsPpOcrWired);
        Assert.Equal(2, studio.Session.State.RoiCount);
        Assert.NotNull(suite.LastOutput);
        Assert.True(suite.HasOcrLivePreviewImages);
        Assert.NotNull(suite.OcrEnhancedPreviewImage);
        Assert.NotNull(suite.OcrBinarizedPreviewImage);
        Assert.Equal(2, suite.OcrLiveRoiItems.Count);
        Assert.False(string.IsNullOrWhiteSpace(suite.OcrLiveRecognizedText));
        AssertAllOcrItemsHaveValidDiagnostics(suite.LastOutput!.Items);
        Assert.Equal(2, suite.LastOutput!.Items.Length);
        Assert.Contains("LOT-2026-A09", suite.LastOutput!.Items[0].OcrDiagnostic!.RecognizedText, StringComparison.Ordinal);
        Assert.Contains("EXP.2027.11.24", suite.LastOutput!.Items[1].OcrDiagnostic!.RecognizedText, StringComparison.Ordinal);
        Assert.All(suite.LastOutput!.Items, item => Assert.Equal(
            !string.IsNullOrWhiteSpace(item.OcrDiagnostic!.RecognizedText)
            && item.OcrDiagnostic!.MeanConfidence >= suite.PpOcrMinConfidence
            && item.OcrDiagnostic!.MinConfidence >= InspectionSuiteParameters.DefaultPpOcrMinCharacterConfidence,
            item.IsPass));

        // Legacy classical OCR kinds normalize seamlessly to the unified PpOcrOnnx workflow
#pragma warning disable CS0618
        await studio.LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind.IndustrialOcrOcv, placePresetRois: true);
#pragma warning restore CS0618
        Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, suite.SelectedAlgorithm);
        Assert.True(suite.IsPpOcrOnnxSelected);
        Assert.Equal(2, studio.Session.State.RoiCount);

        await studio.LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind.AutoTeaching, placePresetRois: true);
        Assert.True(suite.IsAutoTeachingSelected);
        Assert.True(suite.UsesReferenceRois);
        Assert.True(suite.HasRegisteredReferences);
        Assert.Equal(3, suite.RegisteredReferenceItems.Count);
        Assert.Equal(3, studio.Session.State.RoiCount);
        Assert.NotNull(suite.LastOutput);
        Assert.Equal(3, suite.LastOutput!.Items.Length);
        Assert.All(suite.LastOutput!.Items, item => Assert.True(item.IsPass, item.DetailLine));

        // Verify individual part removal and re-teaching in AutoTeaching model pool
        suite.RegisteredReferenceItems[0].RemoveCommand.Execute(null);
        Assert.Equal(2, suite.RegisteredReferenceItems.Count);
        Assert.True(suite.RegisterNormalReference.CanExecute(null));
        suite.RegisterNormalReference.Execute(null);
        Assert.Equal(3, suite.RegisteredReferenceItems.Count);

        // Verify every ComboBox algorithm selection exposes its dedicated teaching parameter summary & header
        Assert.Equal(19, suite.Algorithms.Count);
        foreach (InspectionSuiteAlgorithmOption option in suite.Algorithms)
        {
            suite.SelectedAlgorithm = option.Kind;
            Assert.Contains(option.Name, suite.SelectedAlgorithmHeaderTitle, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(suite.ParameterPreviewSummary));
        }

        // Verify geometric guides for OCR/OCV preview
        var sampleRoi = new InspectionSuiteRoiInput(
            Guid.NewGuid(),
            "OCR-Guide-Test",
            new PointD(200, 120),
            240,
            60,
            0.0,
            [new PointD(80, 90), new PointD(320, 90), new PointD(320, 150), new PointD(80, 150)]);
        var ocrGuides = suite.Registry.BuildGeometricGuides(
            sampleRoi,
            InspectionSuiteAlgorithmKind.PpOcrOnnx,
            new InspectionSuiteParameters());
        Assert.Equal(2, ocrGuides.Length);
    }

    private void AssertFilterSliders(string viewsDir)
    {
        Assert.True(CountSubstrings(File.ReadAllText(Path.Combine(viewsDir, "FluentFilterCard.xaml")), "<Slider ") >= 5);
    }

    private static int CountSubstrings(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    [Fact]
    public void Fluent2Window_InstantiatesWithMicaNavigationViewAndThemeTokensOnStaThread()
    {
        Exception? capturedException = null;
        Thread staThread = new Thread(() =>
        {
            try
            {
                EnsureApplicationThemeLoaded();
                using StubDisplay display = new StubDisplay();
                using MainWindowViewModel studio = CreateStudio(display);
                using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
                    studio,
                    new SubPixelTeachingReadinessEngine());

                Fluent2StudioWindow window = new Fluent2StudioWindow();
                window.Bind(viewModel);
                FrameworkElement rootContent = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                rootContent.Measure(new Size(1920, 1080));
                rootContent.Arrange(new Rect(0, 0, 1920, 1080));
                rootContent.UpdateLayout();

                Assert.IsAssignableFrom<FluentWindow>(window);
                Assert.Equal(WindowBackdropType.Mica, window.WindowBackdropType);
                Assert.True(window.ExtendsContentIntoTitleBar);
                Assert.Equal(5, window.WorkspaceNavigation.MenuItems.Count);
                Assert.Same(viewModel, window.DataContext);
                Assert.Equal(1880, window.Width, 1);
                Assert.Equal(1020, window.Height, 1);
                Assert.Equal(1920, rootContent.ActualWidth, 1);
                Assert.Equal(1080, rootContent.ActualHeight, 1);

                // Verify Fluent 2 CornerRadius tokens in merged ResourceDictionary
                Assert.Equal(new CornerRadius(4), (CornerRadius)Application.Current.FindResource("Fluent2ControlCornerRadius"));
                Assert.Equal(new CornerRadius(8), (CornerRadius)Application.Current.FindResource("Fluent2OverlayCornerRadius"));

                TeachingToolWpf.Views.PatternMaskWindow maskWindow = new TeachingToolWpf.Views.PatternMaskWindow
                {
                    DataContext = studio.PatternMaskEditor
                };
                FrameworkElement maskRoot = Assert.IsAssignableFrom<FrameworkElement>(maskWindow.Content);
                maskRoot.Measure(new Size(1060, 740));
                maskRoot.Arrange(new Rect(0, 0, 1060, 740));
                maskRoot.UpdateLayout();

                // Verify ApplicationThemeManager theme switching on STA thread
                viewModel.ApplyLightTheme.Execute(null);
                Assert.Equal(ApplicationTheme.Light, ApplicationThemeManager.GetAppTheme());

                viewModel.ApplyDarkTheme.Execute(null);
                Assert.Equal(ApplicationTheme.Dark, ApplicationThemeManager.GetAppTheme());

                // Verify '?' Help Manual visual Border Visibility toggles between Collapsed and Visible in XAML
                FluentPatternCard patternCard = new FluentPatternCard { DataContext = viewModel };
                patternCard.Measure(new Size(800, 900));
                patternCard.Arrange(new Rect(0, 0, 800, 900));
                patternCard.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, patternCard.HelpManualBorder.Visibility);
                studio.Matching.ToggleHelpManual.Execute(null);
                patternCard.UpdateLayout();
                Assert.Equal(Visibility.Visible, patternCard.HelpManualBorder.Visibility);

                studio.Matching.ToggleHelpManual.Execute(null);
                patternCard.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, patternCard.HelpManualBorder.Visibility);

                FluentSuiteCard suiteCard = new FluentSuiteCard { DataContext = viewModel };
                suiteCard.Measure(new Size(800, 900));
                suiteCard.Arrange(new Rect(0, 0, 800, 900));
                suiteCard.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, suiteCard.HelpManualBorder.Visibility);
                studio.Inspections.Suite!.ToggleHelpManual.Execute(null);
                suiteCard.UpdateLayout();
                Assert.Equal(Visibility.Visible, suiteCard.HelpManualBorder.Visibility);

                studio.Inspections.Suite.ToggleHelpManual.Execute(null);
                suiteCard.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, suiteCard.HelpManualBorder.Visibility);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        Assert.Null(capturedException);
    }

    [Fact]
    public async Task RealOcrImages_Ocr1Ocr2Ocr3_RecognizeSingleAndMultiLineRoisInBothOcrEngines()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(FindFluent2ProjectDirectory(), "..", ".."));
        string ocr1Path = Path.Combine(repoRoot, "ocr1.png");
        string ocr2Path = Path.Combine(repoRoot, "ocr2.png");
        string ocr3Path = Path.Combine(repoRoot, "ocr3.png");

        Assert.True(File.Exists(ocr1Path), $"Missing {ocr1Path}");
        Assert.True(File.Exists(ocr2Path), $"Missing {ocr2Path}");
        Assert.True(File.Exists(ocr3Path), $"Missing {ocr3Path}");

        string tempCache = Path.Combine(Path.GetTempPath(), "TeachingToolWpf_OcrTestCache_" + Guid.NewGuid().ToString("N"));
        try
        {
            IImageLoader loader = ImageLoaderHost.Create(tempCache);
            InspectionSuiteService suiteService = new InspectionSuiteService(loader, () => null, _ppOcrFixture.Service);
            InspectionSuiteParameters defaultParams = new InspectionSuiteParameters();

            // 1. ocr1.png (286x184): Full-image multi-line ROI + individual line ROIs
            ImageReference ocr1Ref = new ImageReference(ocr1Path, "ocr1.png", 286, 184);
            InspectionSuiteRoiInput ocr1FullRoi = CreateRectRoiInput("OCR1-Full", 143, 92, 276, 176);
            InspectionSuiteRoiInput ocr1Line1 = CreateRectRoiInput("OCR1-L1", 143, 27, 260, 42);
            InspectionSuiteRoiInput ocr1Line4 = CreateRectRoiInput("OCR1-L4", 143, 162, 260, 36);

            var ocr1Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr1Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                [ocr1FullRoi, ocr1Line1, ocr1Line4],
                [],
                defaultParams,
                CancellationToken.None);

            Assert.Equal(3, ocr1Out.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(ocr1Out.Items);

            // 2. ocr2.png (3680x1352): Standard Font (left) and OCR Font (right) ROIs (multi-ROI batched execution)
            ImageReference ocr2Ref = new ImageReference(ocr2Path, "ocr2.png", 3680, 1352);
            InspectionSuiteRoiInput ocr2RightTitle = CreateRectRoiInput("OCR2-RightTitle", 2290, 275, 500, 56);
            InspectionSuiteRoiInput ocr2RightTotal = CreateRectRoiInput("OCR2-RightTotal", 3068, 948, 440, 56);
            InspectionSuiteRoiInput ocr2LeftTitle = CreateRectRoiInput("OCR2-LeftTitle", 790, 188, 740, 92);
            InspectionSuiteRoiInput ocr2LeftName = CreateRectRoiInput("OCR2-LeftName", 450, 476, 560, 48);
            InspectionSuiteRoiInput ocr2LeftTrans = CreateRectRoiInput("OCR2-LeftTrans", 450, 668, 580, 64);
            InspectionSuiteRoiInput ocr2LeftTotal = CreateRectRoiInput("OCR2-LeftTotal", 1264, 1072, 320, 56);
            InspectionSuiteRoiInput ocr2LeftFullHalf = CreateRectRoiInput("OCR2-LeftFullHalf", 785, 610, 1300, 1060);

            var ocr2Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr2Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                [ocr2RightTitle, ocr2RightTotal, ocr2LeftTitle, ocr2LeftName, ocr2LeftTrans, ocr2LeftTotal, ocr2LeftFullHalf],
                [],
                defaultParams,
                CancellationToken.None);

            Assert.Equal(7, ocr2Out.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(ocr2Out.Items);

            // 3. ocr3.png (705x727): Light-on-dark laser-etched metallic cap (2-line ROI + individual line ROIs)
            ImageReference ocr3Ref = new ImageReference(ocr3Path, "ocr3.png", 705, 727);
            InspectionSuiteRoiInput ocr3TwoLine = CreateRectRoiInput("OCR3-Both", 353, 375, 390, 130);
            InspectionSuiteRoiInput ocr3Line1 = CreateRectRoiInput("OCR3-L1", 353, 342, 380, 52);
            InspectionSuiteRoiInput ocr3Line2 = CreateRectRoiInput("OCR3-L2", 353, 405, 280, 52);

            var ocr3Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr3Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                [ocr3TwoLine, ocr3Line1, ocr3Line2],
                [],
                defaultParams,
                CancellationToken.None);

            Assert.Equal(3, ocr3Out.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(ocr3Out.Items);

            // 4. ocr4.png (1085x1292): Thermal receipt ROIs
            string ocr4Path = Path.Combine(repoRoot, "ocr4.png");
            ImageReference ocr4Ref = new ImageReference(ocr4Path, "ocr4.png", 1085, 1292);
            InspectionSuiteRoiInput[] ocr4Rois =
            [
                CreateRectRoiInput("OCR4_Store", 180, 52, 340, 56),
                CreateRectRoiInput("OCR4_CityStateZip", 285, 156, 550, 56)
            ];

            var ocr4Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr4Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                ocr4Rois,
                [],
                defaultParams,
                CancellationToken.None);

            Assert.Equal(2, ocr4Out.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(ocr4Out.Items);

            // 5. ocr5.png (496x403): Korean license plate ROIs — wiring & diagnostic preview verification
            string ocr5Path = Path.Combine(repoRoot, "ocr5.png");
            ImageReference ocr5Ref = new ImageReference(ocr5Path, "ocr5.png", 496, 403);
            InspectionSuiteRoiInput[] ocr5Rois =
            [
                CreateRectRoiInput("OCR5_PlateLine", 255, 191, 410, 84),
                CreateRectRoiInput("OCR5_FullImage", 248, 201, 480, 380)
            ];

            var ocr5Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr5Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                ocr5Rois,
                [],
                defaultParams,
                CancellationToken.None);

            Assert.Equal(2, ocr5Out.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(ocr5Out.Items);
        }
        finally
        {
            if (Directory.Exists(tempCache))
            {
                try { Directory.Delete(tempCache, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RealOcrImages_Ocr6AndOcr7_RecognizeReceiptAndTestTarget()
    {
        string fluent2Dir = FindFluent2ProjectDirectory();
        string repoRoot = Directory.GetParent(fluent2Dir)!.Parent!.FullName;
        string tempCache = Path.Combine(Path.GetTempPath(), $"TeachingToolWpf_Ocr67Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCache);

        try
        {
            IImageLoader loader = ImageLoaderHost.Create(tempCache);
            var suiteService = new InspectionSuiteService(loader, () => null, _ppOcrFixture.Service);
            var defaultParams = new InspectionSuiteParameters();

            string ocr6Path = Path.Combine(repoRoot, "ocr6.jpg");
            ImageReference ocr6Ref = new ImageReference(ocr6Path, "ocr6.jpg", 346, 578);
            InspectionSuiteRoiInput[] ocr6Rois =
            [
                CreateRectRoiInput("OCR6_FullReceipt", 162, 250, 290, 490),
                CreateRectRoiInput("OCR6_Header", 95, 39, 160, 70),
                CreateRectRoiInput("OCR6_SaleBlock", 104, 132, 174, 86),
                CreateRectRoiInput("OCR6_Items", 152, 208, 266, 36),
                CreateRectRoiInput("OCR6_Totals", 90, 319, 148, 124),
                CreateRectRoiInput("OCR6_Footer", 116, 455, 116, 56)
            ];

            var ocr6Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr6Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                ocr6Rois,
                [],
                defaultParams,
                CancellationToken.None);

            string ocr7Path = Path.Combine(repoRoot, "OCR7.jpg");
            ImageReference ocr7Ref = new ImageReference(ocr7Path, "OCR7.jpg", 1024, 572);
            InspectionSuiteRoiInput[] ocr7Rois =
            [
                CreateRectRoiInput("OCR7_Title", 318, 51, 536, 26),
                CreateRectRoiInput("OCR7_QuickFox", 291, 133, 416, 30),
                CreateRectRoiInput("OCR7_Digits24pt", 186, 169, 204, 30),
                CreateRectRoiInput("OCR7_UpperAlpha", 294, 205, 402, 24),
                CreateRectRoiInput("OCR7_LowerAlpha", 252, 233, 322, 22),
                CreateRectRoiInput("OCR7_DotMatrixMfg", 689, 151, 328, 22),
                CreateRectRoiInput("OCR7_DotMatrixExp", 619, 179, 186, 22),
                CreateRectRoiInput("OCR7_OcrAB", 644, 260, 238, 52),
                CreateRectRoiInput("OCR7_LowContrast", 616, 360, 180, 22),
                CreateRectRoiInput("OCR7_Blurred", 645, 416, 238, 38),
                CreateRectRoiInput("OCR7_Damaged", 643, 468, 234, 20),
                CreateRectRoiInput("OCR7_WhiteOnBlack", 645, 520, 236, 30),
                CreateRectRoiInput("OCR7_LogisticsRev", 870, 370, 164, 42),
                CreateRectRoiInput("OCR7_LogisticsNormal", 878, 516, 192, 46),
                CreateRectRoiInput("OCR7_7Segment", 918, 172, 96, 68),
                CreateRectRoiInput("OCR7_Handwriting", 888, 260, 184, 54),
                CreateRectRoiInput("OCR7_Tilt15", 306, 405, 76, 86),
                CreateRectRoiInput("OCR7_Tilt30", 354, 412, 78, 86),
                CreateRectRoiInput("OCR7_Tilt45", 408, 420, 92, 90),
                CreateRectRoiInput("OCR7_Tilt45_Vertical90", 485, 432, 34, 186),
                CreateRectRoiInput("OCR7_BlurCaptionKor", 630, 385, 210, 22),
                CreateRectRoiInput("OCR7_NoiseCaptionKor", 645, 445, 244, 22),
                CreateRectRoiInput("OCR7_SectionD_Block", 645, 442, 248, 210),
                CreateRectRoiInput("OCR7_SectionE_Block", 880, 442, 206, 210),
                CreateRectRoiInput("OCR7_FullImage", 512, 286, 1016, 564)
            ];

            var ocr7Out = await suiteService.ExecuteRoiInspectionAsync(
                ocr7Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                ocr7Rois,
                [],
                defaultParams,
                CancellationToken.None);

            // Verify wiring, report formatting, and diagnostic preview buffers across ocr6.jpg & OCR7.jpg
            // (Per-ROI CER and Line Accuracy assertions are enforced in OcrEvaluationRatchetTests against labels.json)
            Assert.Equal(ocr6Rois.Length, ocr6Out.Items.Length);
            Assert.False(string.IsNullOrWhiteSpace(ocr6Out.DetailedReport));
            AssertAllOcrItemsHaveValidDiagnostics(ocr6Out.Items);

            Assert.Equal(ocr7Rois.Length, ocr7Out.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(ocr7Out.Items);
            Assert.Equal("OCR VISION INSPECTION TEST TARGET", ocr7Out.Items[0].OcrDiagnostic!.RecognizedText);
            Assert.Contains("노이즈", ocr7Out.Items[21].OcrDiagnostic!.RecognizedText, StringComparison.Ordinal);

            // Verify PP-OCRv4 preset & threshold sensitivity
            var highSpeedParams = new InspectionSuiteParameters(
                PpOcrPreset: PpOcrSpeedPreset.HighSpeedProduction,
                PpOcrLanguageMode: PpOcrLanguage.English);
            var highSpeedOut = await suiteService.ExecuteRoiInspectionAsync(
                ocr7Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                [ocr7Rois[1]],
                [],
                highSpeedParams,
                CancellationToken.None);
            Assert.Contains("고속양산(배치)", highSpeedOut.Items[0].OcrDiagnostic!.PreprocessingBadge, StringComparison.Ordinal);

            var strictConfidenceParams = new InspectionSuiteParameters(PpOcrMinConfidence: 0.9999f);
            var strictOut = await suiteService.ExecuteRoiInspectionAsync(
                ocr7Ref,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                [ocr7Rois[8]],
                [],
                strictConfidenceParams,
                CancellationToken.None);
            Assert.Contains("합격=False", strictOut.DetailedReport, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempCache))
            {
                try { Directory.Delete(tempCache, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ParameterAdjustment_PreservesLiveOverlayWithoutFlickering()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);

        await studio.LoadInspectionSuiteSampleAsync(InspectionSuiteAlgorithmKind.CornerAlignment);
        Assert.Equal(0, studio.Session.State.RoiCount);

        Guid roiId = studio.Session.Add(new RoiGeometry(new PointD(260, 220), 160, 120, 0));
        studio.Session.Select([roiId]);
        Assert.Equal(1, studio.Session.State.RoiCount);

        InspectionSuiteViewModel suiteVm = studio.Inspections.Suite!;
        Assert.NotNull(suiteVm.Overlay);

        // Wait briefly for initial async live measurement to populate _livePreview
        await Task.Delay(120);
        Assert.NotNull(suiteVm.Overlay);

        int overlayNullCount = 0;
        suiteVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InspectionSuiteViewModel.Overlay) && suiteVm.Overlay is null)
            {
                overlayNullCount++;
            }
        };

        // Adjust multiple parameters rapidly (simulating slider drag)
        suiteVm.GaussianSigma = 1.6f;
        Assert.NotNull(suiteVm.Overlay);
        suiteVm.MinimumContrast = 24.0f;
        Assert.NotNull(suiteVm.Overlay);
        suiteVm.CaliperProjectionWidth = 10;
        Assert.NotNull(suiteVm.Overlay);

        Assert.Equal(0, overlayNullCount);
    }

    [Fact]
    public async Task HelpManualToggleAndMassRepeatedRoiTest_OperateSmoothlyAcrossAllAlgorithms()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        await studio.InitializeAsync();

        // 1. Verify '?' Help Manual toggle & rich content for Pattern Matching methods
        Assert.False(studio.Matching.IsHelpManualOpen);
        studio.Matching.ToggleHelpManual.Execute(null);
        Assert.True(studio.Matching.IsHelpManualOpen);
        foreach (PatternMatchingMethod method in Enum.GetValues<PatternMatchingMethod>())
        {
            studio.Matching.Method = method;
            Assert.False(string.IsNullOrWhiteSpace(studio.Matching.ActiveHelpManual.Title));
            Assert.False(string.IsNullOrWhiteSpace(studio.Matching.ActiveHelpManual.PrincipleAndFormula));
            Assert.False(string.IsNullOrWhiteSpace(studio.Matching.ActiveHelpManual.StepByStepGuide));
            Assert.False(string.IsNullOrWhiteSpace(studio.Matching.ActiveHelpManual.ParameterTuningGuide));
            Assert.False(string.IsNullOrWhiteSpace(studio.Matching.ActiveHelpManual.TroubleshootingTips));
        }
        studio.Matching.ToggleHelpManual.Execute(null);
        Assert.False(studio.Matching.IsHelpManualOpen);

        // 2. Verify '?' Help Manual toggle & content for Primitive Shapes and Edge Few-Shot
        Assert.False(studio.Inspections.Shapes.IsHelpManualOpen);
        studio.Inspections.Shapes.ToggleHelpManual.Execute(null);
        Assert.True(studio.Inspections.Shapes.IsHelpManualOpen);
        Assert.Contains("11종", studio.Inspections.Shapes.ActiveHelpManual.Title, StringComparison.Ordinal);

        EdgeFewShotViewModel fewShot = Assert.IsType<EdgeFewShotViewModel>(studio.Inspections.FewShot);
        Assert.False(fewShot.IsHelpManualOpen);
        fewShot.ToggleHelpManual.Execute(null);
        Assert.True(fewShot.IsHelpManualOpen);
        Assert.Contains("Few-Shot", fewShot.ActiveHelpManual.Title, StringComparison.Ordinal);

        // 3. Verify '?' Help Manual toggle & content across all 16 Inspection Suite algorithms
        InspectionSuiteViewModel suite = Assert.IsType<InspectionSuiteViewModel>(studio.Inspections.Suite);
        Assert.False(suite.IsHelpManualOpen);
        suite.ToggleHelpManual.Execute(null);
        Assert.True(suite.IsHelpManualOpen);
        foreach (InspectionSuiteAlgorithmKind kind in Enum.GetValues<InspectionSuiteAlgorithmKind>())
        {
            suite.SelectedAlgorithm = kind;
            Assert.False(string.IsNullOrWhiteSpace(suite.ActiveHelpManual.Title));
            Assert.False(string.IsNullOrWhiteSpace(suite.ActiveHelpManual.PrincipleAndFormula));
            Assert.False(string.IsNullOrWhiteSpace(suite.ActiveHelpManual.StepByStepGuide));
        }

        // 4. Verify '?' Help Manual toggle & content for ROI Filters
        Assert.False(viewModel.IsFilterHelpManualOpen);
        viewModel.ToggleFilterHelpManual.Execute(null);
        Assert.True(viewModel.IsFilterHelpManualOpen);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ActiveFilterHelpManual.Title));

        // 5. Verify AddRepeatedTest (10,201 ROIs) executes rapidly without O(N log N) UI stalls
        var sw = System.Diagnostics.Stopwatch.StartNew();
        studio.AddRepeatedTest.Execute(null);
        sw.Stop();
        Assert.Equal(10201, studio.Session.State.RoiCount);
        Assert.Equal(512, studio.VisibleRows.Count);
        Assert.Contains("10201개", studio.Inspections.SearchScopeSummaryText, StringComparison.Ordinal);
        Assert.True(sw.ElapsedMilliseconds < 2500, $"AddRepeatedTest took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task PreFilterPipelineRule_UnifiedTeachingCategories_AndNewSuiteAlgorithms_ExecuteDeterministically()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        await studio.InitializeAsync();

        // 1. Multi-step filter pipeline list & pre-inspection execution rule
        studio.Filters.SelectedFilter = VisionCanvas.Core.Editing.RoiFilterKind.Clahe;
        studio.Filters.AddStepToPipeline.Execute(null);
        studio.Filters.SelectedFilter = VisionCanvas.Core.Editing.RoiFilterKind.FftNotch;
        studio.Filters.AddStepToPipeline.Execute(null);
        studio.Filters.SelectedFilter = VisionCanvas.Core.Editing.RoiFilterKind.GaborTexture;
        studio.Filters.AddStepToPipeline.Execute(null);

        Assert.Equal(3, studio.Filters.PipelineSteps.Count);
        Assert.Equal(3, studio.PreFilterPipeline.ActiveSteps.Length);
        Assert.True(studio.Inspections.HasActivePreFilterPipeline);

        // Disable the second step -> active steps immediately reflect 2 steps
        studio.Filters.PipelineSteps[1].IsEnabled = false;
        Assert.Equal(2, studio.PreFilterPipeline.ActiveSteps.Length);

        // Verify PreInspectionFilterPipeline executes enabled steps in order on a grayscale buffer
        byte[] testPixels = new byte[64 * 64];
        for (int i = 0; i < testPixels.Length; i++)
        {
            testPixels[i] = (byte)((i * 17) & 0xFF);
        }

        int executedSteps = studio.PreFilterPipeline.ApplyInPlaceIfConfigured(testPixels, 64, 64);
        Assert.Equal(2, executedSteps);

        // Clear registration -> 0 active steps
        studio.Filters.ClearRegistration.Execute(null);
        Assert.Empty(studio.Filters.PipelineSteps);
        Assert.False(studio.Inspections.HasActivePreFilterPipeline);

        // 2. Unified Teaching Category selector switches both tool and suite algorithm cleanly
        studio.Inspections.SelectCategorySuiteMetrology.Execute(null);
        Assert.Equal(InspectionToolKind.InspectionSuite, studio.Inspections.Selected);
        Assert.Equal(InspectionSuiteAlgorithmKind.Metrology, studio.Inspections.Suite!.SelectedAlgorithm);

        studio.Inspections.SelectCategorySuiteSurface.Execute(null);
        Assert.Equal(InspectionSuiteAlgorithmKind.PhotometricStereo, studio.Inspections.Suite.SelectedAlgorithm);

        studio.Inspections.SelectCategorySuiteOcr.Execute(null);
        Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, studio.Inspections.Suite.SelectedAlgorithm);

        studio.Inspections.SelectCategorySuiteBarcode.Execute(null);
        Assert.Equal(InspectionSuiteAlgorithmKind.Barcode1DReader, studio.Inspections.Suite.SelectedAlgorithm);

        studio.Inspections.SelectCategorySuitePreprocess.Execute(null);
        Assert.Equal(InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor, studio.Inspections.Suite.SelectedAlgorithm);

        studio.Inspections.SelectCategoryPattern.Execute(null);
        Assert.Equal(InspectionToolKind.Pattern, studio.Inspections.Selected);

        // 3. Verify all 4 newly added Suite algorithms (16..19) load procedural scenes and pass preset ROI inspection
        InspectionSuiteAlgorithmKind[] newAlgorithms =
        [
            InspectionSuiteAlgorithmKind.PreprocessingFftAndGabor,
            InspectionSuiteAlgorithmKind.Barcode1DReader,
            InspectionSuiteAlgorithmKind.PhotometricStereo,
            InspectionSuiteAlgorithmKind.GeometricTolerancing
        ];

        foreach (InspectionSuiteAlgorithmKind kind in newAlgorithms)
        {
            await studio.LoadInspectionSuiteSampleAsync(kind, placePresetRois: true);
            Assert.True(studio.Session.State.RoiCount > 0, $"Preset ROIs were not created for {kind}");
            await studio.Inspections.Suite.ExecuteAllRois.ExecuteAsync();
            Assert.NotNull(studio.Inspections.Suite.LastOutput);
            Assert.True(studio.Inspections.Suite.LastOutput!.Items.Count(i => i.IsPass) > 0, $"Expected PASS results for {kind}, status: {studio.Inspections.Suite.Status}");
        }

        // 4. Verify PatternMaskEditorViewModel '?' Help Manual toggle
        Assert.False(studio.PatternMaskEditor.IsHelpManualOpen);
        studio.PatternMaskEditor.ToggleHelpManualCommand.Execute(null);
        Assert.True(studio.PatternMaskEditor.IsHelpManualOpen);
        Assert.False(string.IsNullOrWhiteSpace(studio.PatternMaskEditor.ActiveHelpManual.Title));
    }

    [Fact]
    public void EveryAlgorithm_HasDedicated1To1ParameterPanelAndIndependentProperties()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        InspectionSuiteViewModel suite = Assert.IsType<InspectionSuiteViewModel>(studio.Inspections.Suite);

        Func<InspectionSuiteViewModel, bool>[] primaryVisibilityFlags =
        [
            vm => vm.IsMetrologySelected,
            vm => vm.IsGoldenVariationSelected,
            vm => vm.IsBlobRleSelected,
            vm => vm.IsCurvilinearSelected,
            vm => vm.IsFewShotSelected,
            vm => vm.IsPatchAnomalySelected,
            vm => vm.IsCalibrationSelected,
            vm => vm.IsCornerAlignmentSelected,
            vm => vm.IsAutoTeachingSelected,
            vm => vm.IsPreprocessingShadingSelected,
            vm => vm.IsAdvancedMetrologyEllipseSelected,
            vm => vm.IsAdvancedBlobWatershedSelected,
            vm => vm.IsDataMatrix2DSelected,
            vm => vm.IsPreprocessingFftGaborSelected,
            vm => vm.IsBarcode1DSelected,
            vm => vm.IsPhotometricStereoSelected,
            vm => vm.IsGeometricTolerancingSelected,
            vm => vm.IsPpOcrOnnxSelected,
            vm => vm.IsQrCode2DSelected
        ];

        HashSet<string> summaries = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(19, suite.Algorithms.Count);
        foreach (InspectionSuiteAlgorithmOption option in suite.Algorithms)
        {
            suite.SelectedAlgorithm = option.Kind;
            int activePrimaryFlags = primaryVisibilityFlags.Count(flag => flag(suite));
            Assert.Equal(1, activePrimaryFlags);
            Assert.True(summaries.Add(suite.ParameterPreviewSummary), $"Duplicate ParameterPreviewSummary for {option.Kind}: {suite.ParameterPreviewSummary}");
        }

        // Verify legacy OCR enum values normalize to the unified PpOcrOnnx panel
#pragma warning disable CS0618
        suite.SelectedAlgorithm = InspectionSuiteAlgorithmKind.IndustrialOcrOcv;
        Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, suite.SelectedAlgorithm);
        suite.SelectedAlgorithm = InspectionSuiteAlgorithmKind.LightweightOcr;
        Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, suite.SelectedAlgorithm);
        suite.SelectedAlgorithm = InspectionSuiteAlgorithmKind.CpuDeepOcr;
        Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, suite.SelectedAlgorithm);
#pragma warning restore CS0618

        // Verify PP-OCRv4 preset and OCV contrast properties update cleanly and independently
        suite.PpOcrPreset = PpOcrSpeedPreset.HighSpeedProduction;
        suite.OcvForegroundContrast = 24.0f;
        Assert.Contains("고속 양산", suite.ParameterPreviewSummary, StringComparison.Ordinal);
        Assert.Contains("24.0GV", suite.ParameterPreviewSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void CategoryBasedAlgorithmSelection_FiltersCategoryAlgorithmsAndSwitchesStageDirectly()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        InspectionSuiteViewModel suite = Assert.IsType<InspectionSuiteViewModel>(studio.Inspections.Suite);

        viewModel.SelectMetrologyCategoryStage.Execute(null);
        Assert.True(viewModel.IsSuiteStage);
        Assert.True(viewModel.IsMetrologyCategoryStage);
        Assert.True(suite.IsMetrologyCategorySelected);
        Assert.Equal(5, suite.CategoryAlgorithms.Count);
        Assert.All(suite.CategoryAlgorithms, a => Assert.Equal(InspectionSuiteCategory.MetrologyAndGeometry, a.Category));

        viewModel.SelectSurface3DCategoryStage.Execute(null);
        Assert.True(viewModel.IsSurface3DCategoryStage);
        Assert.True(suite.IsSurface3DCategorySelected);
        Assert.Equal(6, suite.CategoryAlgorithms.Count);
        Assert.All(suite.CategoryAlgorithms, a => Assert.Equal(InspectionSuiteCategory.SurfaceBlobAnd3D, a.Category));

        viewModel.SelectOcrCategoryStage.Execute(null);
        Assert.True(viewModel.IsOcrCategoryStage);
        Assert.True(suite.IsOcrCategorySelected);
        Assert.True(suite.IsAnyOcrSelected);
        Assert.Equal(InspectionSuiteAlgorithmKind.PpOcrOnnx, suite.SelectedAlgorithm);
        Assert.Single(suite.CategoryAlgorithms);
        Assert.All(suite.CategoryAlgorithms, a => Assert.Equal(InspectionSuiteCategory.Ocr, a.Category));

        viewModel.SelectBarcodeCategoryStage.Execute(null);
        Assert.True(viewModel.IsBarcodeCategoryStage);
        Assert.True(suite.IsBarcodeCategorySelected);
        Assert.False(suite.IsAnyOcrSelected);
        Assert.Equal(InspectionSuiteAlgorithmKind.Barcode1DReader, suite.SelectedAlgorithm);
        Assert.Equal(3, suite.CategoryAlgorithms.Count);
        Assert.All(suite.CategoryAlgorithms, a => Assert.Equal(InspectionSuiteCategory.Barcode1D2D, a.Category));

        viewModel.SelectAiAutoTeachingCategoryStage.Execute(null);
        Assert.True(viewModel.IsAiAutoTeachingCategoryStage);
        Assert.True(suite.IsAiAutoTeachingCategorySelected);
        Assert.Equal(2, suite.CategoryAlgorithms.Count);
        Assert.All(suite.CategoryAlgorithms, a => Assert.Equal(InspectionSuiteCategory.AiAndAutoTeaching, a.Category));

        viewModel.SelectPreprocessingCategoryStage.Execute(null);
        Assert.True(viewModel.IsPreprocessingCategoryStage);
        Assert.True(suite.IsPreprocessingCategorySelected);
        Assert.Equal(2, suite.CategoryAlgorithms.Count);
        Assert.All(suite.CategoryAlgorithms, a => Assert.Equal(InspectionSuiteCategory.PreprocessingAndFrequency, a.Category));

        suite.SelectAllCategories.Execute(null);
        Assert.True(suite.IsAllCategoriesSelected);
        Assert.Equal(19, suite.CategoryAlgorithms.Count);
    }

    [Fact]
    public async Task SelectRowsAndRepeatedTest_FocusesSelectedRoiLocationAndPopulatesBatchWithoutLag()
    {
        using StubDisplay display = new StubDisplay();
        using MainWindowViewModel studio = CreateStudio(display);
        using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
            studio,
            new SubPixelTeachingReadinessEngine());

        await studio.InitializeAsync();

        ViewportTransform initialViewport = studio.Session.State.Viewport;
        Guid targetRoi = studio.Session.Add(new RoiGeometry(new PointD(7200, 6800), 60, 40, 0));
        Guid secondRoi = studio.Session.Add(new RoiGeometry(new PointD(400, 300), 50, 50, 0));

        // Selecting a row from the left ROI list (SelectRows) must focus the canvas viewport onto that ROI's location
        studio.SelectRows([targetRoi]);
        ViewportTransform focusedViewport = studio.Session.State.Viewport;
        Assert.NotEqual(initialViewport, focusedViewport);

        PointD screenCenter = new PointD(TestViewportWidth * 0.5, TestViewportHeight * 0.5);
        PointD projectedTarget = focusedViewport.ImageToScreen(new PointD(7200, 6800));
        Assert.InRange(Math.Abs(projectedTarget.X - screenCenter.X), 0.0, 1.0);
        Assert.InRange(Math.Abs(projectedTarget.Y - screenCenter.Y), 0.0, 1.0);

        studio.SelectRows([secondRoi]);
        PointD projectedSecond = studio.Session.State.Viewport.ImageToScreen(new PointD(400, 300));
        Assert.InRange(Math.Abs(projectedSecond.X - screenCenter.X), 0.0, 1.0);
        Assert.InRange(Math.Abs(projectedSecond.Y - screenCenter.Y), 0.0, 1.0);

        // Executing AddRepeatedTest (1억 ROI button) must complete rapidly without redundant O(N) UI-thread scans
        Assert.True(studio.AddRepeatedTest.CanExecute(null));
        studio.AddRepeatedTest.Execute(null);

        Assert.Equal(10203, studio.Session.State.RoiCount);
        Assert.Equal(10201, studio.Session.State.SelectionCount);
        Assert.Equal(512, studio.VisibleRows.Count);
        Assert.Contains("10201", studio.Inspections.QuickInspectLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiveMassiveRandomRoiModels_GenerateDistinctLocationsAndLoadFromExplorerUltraFast()
    {
        string tempModelsDir = Path.Combine(Path.GetTempPath(), $"TeachingTool_Models100M_{Guid.NewGuid():N}");
        try
        {
            using StubDisplay display = new StubDisplay();
            using MainWindowViewModel studio = CreateStudio(display);
            using Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(
                studio,
                new SubPixelTeachingReadinessEngine());

            await studio.InitializeAsync();
            viewModel.MassiveModelsDirectoryPath = tempModelsDir;

            IReadOnlyList<string> modelPaths = viewModel.EnsureAndReportFiveMassiveModels();
            Assert.Equal(5, modelPaths.Count);
            foreach (string path in modelPaths)
            {
                Assert.True(File.Exists(path));
                Assert.EndsWith(".vmodel", path, StringComparison.OrdinalIgnoreCase);
            }

            // Verify all 5 models have 100,000,000 ROIs, completely distinct positions across all 5 models, and assigned algorithms
            MassiveRoiModelFile[] loadedFiles = new MassiveRoiModelFile[5];
            for (int m = 0; m < 5; m++)
            {
                loadedFiles[m] = await MassiveRoiModelSerializer.LoadFromDiskAsync(modelPaths[m]);
                Assert.Equal(m + 1, loadedFiles[m].Header.ModelNumber);
                Assert.Equal(100_000_000L, loadedFiles[m].Header.TotalRoiCount);
                Assert.True(loadedFiles[m].LoadElapsedMilliseconds < 250.0);
            }

            long[] sampleIndices = [0L, 1L, 511L, 4_096L, 1_234_567L, 50_000_000L, 99_999_999L];
            foreach (long roiIndex in sampleIndices)
            {
                HashSet<(double X, double Y)> distinctCenters = [];
                for (int m = 0; m < 5; m++)
                {
                    MassiveRoiEntry entry = loadedFiles[m].GetEntry(roiIndex);
                    Assert.True(entry.IsInitialized);
                    Assert.Equal(roiIndex, entry.Index);
                    Assert.False(string.IsNullOrWhiteSpace(entry.AlgorithmBadge));
                    Assert.NotEqual("미지정", entry.AlgorithmBadge);
                    Assert.True(distinctCenters.Add((entry.Geometry.Center.X, entry.Geometry.Center.Y)));
                }

                Assert.Equal(5, distinctCenters.Count);
            }

            // Simulate opening Model #3 via File Explorer dialog override (zero pre-caching)
            viewModel.SelectMassiveModelFileDialogOverride = dir =>
            {
                Assert.Equal(tempModelsDir, dir);
                return modelPaths[2];
            };

            await viewModel.OpenMassiveModelFromExplorer.ExecuteAsync();

            Assert.True(studio.HasActiveMassiveModel);
            Assert.Equal(3, studio.ActiveMassiveModel!.Header.ModelNumber);
            Assert.Equal(100_000_000L, studio.RoiCount);
            Assert.Contains("100,000,000", studio.SelectionSummary, StringComparison.Ordinal);
            Assert.Contains("195,313", studio.RowPageLabel, StringComparison.Ordinal);
            Assert.Equal(512, studio.VisibleRows.Count);
            Assert.True(studio.VisibleRows[0].HasAlgorithm);

            // Navigate to a deep virtual page (e.g. Page 50,000 -> ROI #25,600,001) and select an unmaterialized ROI
            studio.JumpToRowPage(50_000);
            Assert.Equal(512, studio.VisibleRows.Count);
            RoiRow deepRow = studio.VisibleRows[10];
            MassiveRoiEntry expectedDeepEntry = studio.ActiveMassiveModel.GetEntry((50_000L * 512L) + 10L);
            Assert.Equal(expectedDeepEntry.Id, deepRow.Id);
            Assert.Equal(expectedDeepEntry.AlgorithmBadge, deepRow.AlgorithmBadge);

            // Selecting the deep row in the left list materializes it in O(1), syncs its algorithm, and focuses the canvas viewport on its random coordinates
            studio.SelectRows([deepRow.Id]);
            Assert.True(studio.Session.State.Rois.ContainsKey(deepRow.Id));
            Assert.Equal(expectedDeepEntry.AlgorithmBadge, studio.Inspections.SelectedRoiAlgorithmBadge);

            PointD screenCenter = new PointD(TestViewportWidth * 0.5, TestViewportHeight * 0.5);
            PointD projectedDeepCenter = studio.Session.State.Viewport.ImageToScreen(expectedDeepEntry.Geometry.Center);
            Assert.InRange(Math.Abs(projectedDeepCenter.X - screenCenter.X), 0.0, 1.0);
            Assert.InRange(Math.Abs(projectedDeepCenter.Y - screenCenter.Y), 0.0, 1.0);
        }
        finally
        {
            if (Directory.Exists(tempModelsDir))
            {
                Directory.Delete(tempModelsDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RealExpireCanAndDataMatrixBarcode_RecognizeSlashDateAndOutputBarcodeWorldCoordinates()
    {
        string fluent2Dir = FindFluent2ProjectDirectory();
        string repoRoot = Directory.GetParent(fluent2Dir)!.Parent!.FullName;
        string tempCache = Path.Combine(Path.GetTempPath(), $"TeachingToolWpf_ExpireBarcodeTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempCache);

        try
        {
            IImageLoader loader = ImageLoaderHost.Create(tempCache);
            var suiteService = new InspectionSuiteService(loader, () => null, _ppOcrFixture.Service);
            var defaultParams = new InspectionSuiteParameters();

            // 1. expire.jpg (526x701): Metallic can bottom 5x7 dot-matrix stamp ("2024/01/28" and "14:00 QZ")
            string expirePath = Path.Combine(repoRoot, "expire.jpg");
            ImageReference expireRef = new ImageReference(expirePath, "expire.jpg", 526, 701);
            InspectionSuiteRoiInput[] expireRois =
            [
                CreateRectRoiInput("Expire_TwoLineStamp", 258, 398, 240, 86),
                CreateRectRoiInput("Expire_Line1_Date", 258, 382, 234, 44),
                CreateRectRoiInput("Expire_Line2_TimeCode", 263, 416, 194, 42),
                CreateRectRoiInput("Expire_WideCanCenter", 263, 398, 360, 160),
                CreateRectRoiInput("Expire_FullCan", 263, 350, 500, 660)
            ];

            var expireOut = await suiteService.ExecuteRoiInspectionAsync(
                expireRef,
                live: false,
                InspectionSuiteAlgorithmKind.PpOcrOnnx,
                expireRois,
                [],
                defaultParams,
                CancellationToken.None);

            Assert.Equal(expireRois.Length, expireOut.Items.Length);
            AssertAllOcrItemsHaveValidDiagnostics(expireOut.Items);

            // 2. DataMatrix 2D & 1D Barcode reading + exact barcode world coordinates & rotation angle (theta) display
            ImageReference dmRef = new ImageReference(
                $"{InspectionSuiteSampleTileSource.KeyPrefix}datamatrix",
                "16. [본검사] 산업용 2D DataMatrix ECC 200 리더 샘플 (1200×800)",
                InspectionSuiteSampleTileSource.DefaultWidth,
                InspectionSuiteSampleTileSource.DefaultHeight);
            InspectionSuiteRoiInput[] dmRois =
            [
                CreateRectRoiInput("DM_Left_Tight", 324, 384, 188, 188),
                CreateRectRoiInput("DM_Right_Loose", 860, 395, 320, 280),
                CreateRectRoiInput("DM_Wide_Both", 584, 384, 820, 320),
                CreateRectRoiInput("DM_Rotated_22_5", 584, 656, 196, 196)
            ];

            var dmOut = await suiteService.ExecuteRoiInspectionAsync(
                dmRef,
                live: false,
                InspectionSuiteAlgorithmKind.DataMatrix2DReader,
                dmRois,
                [],
                defaultParams,
                CancellationToken.None);

            Assert.All(dmOut.Items, item => Assert.True(item.IsPass, item.DetailLine));
            Assert.Contains("LOT26A", dmOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("@ (324.0, 384.0)", dmOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("θ=0.00°", dmOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("바코드 중심좌표=(324.0, 384.0)", dmOut.Items[0].DetailLine, StringComparison.Ordinal);
            Assert.Contains("회전각=0.00°", dmOut.Items[0].DetailLine, StringComparison.Ordinal);
            Assert.Contains("바운딩박스=[좌:240.0, 상:300.0 ~ 우:408.0, 하:468.0]", dmOut.Items[0].DetailLine, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(dmOut.Items[0].ReferencePoint.X - 324.0), 0.0, 2.0);
            Assert.InRange(Math.Abs(dmOut.Items[0].ReferencePoint.Y - 384.0), 0.0, 2.0);

            // Even when ROI #2 is offset at (860, 395), ReferencePoint must be the actual barcode center (844, 384)
            Assert.Contains("OK9912", dmOut.Items[1].BadgeLabel, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(dmOut.Items[1].ReferencePoint.X - 844.0), 0.0, 2.0);
            Assert.InRange(Math.Abs(dmOut.Items[1].ReferencePoint.Y - 384.0), 0.0, 2.0);
            Assert.Contains("바코드 중심좌표=(844.0, 384.0)", dmOut.Items[1].DetailLine, StringComparison.Ordinal);

            // Wide ROI covering both symbols isolates and decodes BOTH DataMatrix codes with exact world coordinates in reading order
            Assert.Equal(5, dmOut.Items.Length);
            Assert.Equal("DM_Wide_Both #1", dmOut.Items[2].Name);
            Assert.Contains("LOT26A", dmOut.Items[2].BadgeLabel, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(dmOut.Items[2].ReferencePoint.X - 324.0), 0.0, 2.0);
            Assert.InRange(Math.Abs(dmOut.Items[2].ReferencePoint.Y - 384.0), 0.0, 2.0);

            Assert.Equal("DM_Wide_Both #2", dmOut.Items[3].Name);
            Assert.Contains("OK9912", dmOut.Items[3].BadgeLabel, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(dmOut.Items[3].ReferencePoint.X - 844.0), 0.0, 2.0);
            Assert.InRange(Math.Abs(dmOut.Items[3].ReferencePoint.Y - 384.0), 0.0, 2.0);

            // Rotated 2D DataMatrix (+22.5 deg) inside a 0-deg ROI outputs true world center (584, 656) and true rotation (+22.5 deg)
            Assert.Contains("ROT23", dmOut.Items[4].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("회전각=", dmOut.Items[4].DetailLine, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(dmOut.Items[4].ReferencePoint.X - 584.0), 0.0, 2.5);
            Assert.InRange(Math.Abs(dmOut.Items[4].ReferencePoint.Y - 656.0), 0.0, 2.5);
            double dmRotDeg = dmOut.Items[4].AngleRadians * (180.0 / Math.PI);
            Assert.InRange(dmRotDeg, 21.3, 23.7);

            // 3. 1D Linear Barcode sample scene: 0 deg & -18.0 deg rotated Code 128B symbols
            ImageReference bar1dRef = new ImageReference(
                $"{InspectionSuiteSampleTileSource.KeyPrefix}barcode1d",
                "18. [본검사] 1D 바코드(Code 128) 멀티스캔 디코더 & ISO 15416 품질 샘플 (1200×800)",
                InspectionSuiteSampleTileSource.DefaultWidth,
                InspectionSuiteSampleTileSource.DefaultHeight);
            InspectionSuiteRoiInput[] bar1dRois =
            [
                CreateRectRoiInput("Bar1D_Left_0Deg", 310, 385, 460, 220),
                CreateRectRoiInput("Bar1D_Right_0Deg", 855, 385, 390, 220),
                CreateRectRoiInput("Bar1D_Rotated_Neg18Deg", 600, 648, 360, 210)
            ];

            var bar1dOut = await suiteService.ExecuteRoiInspectionAsync(
                bar1dRef,
                live: false,
                InspectionSuiteAlgorithmKind.Barcode1DReader,
                bar1dRois,
                [],
                defaultParams,
                CancellationToken.None);

            Assert.All(bar1dOut.Items, item => Assert.True(item.IsPass, item.DetailLine));
            Assert.Contains("LOT26A", bar1dOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("θ=0.00°", bar1dOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("회전각=0.00°", bar1dOut.Items[0].DetailLine, StringComparison.Ordinal);

            Assert.Contains("OK99", bar1dOut.Items[1].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("ANG18", bar1dOut.Items[2].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("회전각=", bar1dOut.Items[2].DetailLine, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(bar1dOut.Items[2].ReferencePoint.X - 600.0), 0.0, 4.0);
            Assert.InRange(Math.Abs(bar1dOut.Items[2].ReferencePoint.Y - 648.0), 0.0, 4.0);
            double bar1dRotDeg = bar1dOut.Items[2].AngleRadians * (180.0 / Math.PI);
            Assert.InRange(bar1dRotDeg, -19.2, -16.8);

            // 4. 2D QR Code (ISO 18004) sample scene: 0 deg, -32.5 deg rotated, and +135.0 deg inverted symbols
            ImageReference qrRef = new ImageReference(
                $"{InspectionSuiteSampleTileSource.KeyPrefix}qrcode",
                "21. [본검사] 산업용 2D QR 코드(ISO 18004) 360° 위치·각도 리더 샘플 (1200×800)",
                InspectionSuiteSampleTileSource.DefaultWidth,
                InspectionSuiteSampleTileSource.DefaultHeight);
            InspectionSuiteRoiInput[] qrRois =
            [
                CreateRectRoiInput("QR_0Deg", 306, 326, 200, 200),
                CreateRectRoiInput("QR_Rot_Neg32_5", 872, 334, 240, 240),
                CreateRectRoiInput("QR_Inv_Pos135", 584, 620, 224, 224)
            ];

            var qrOut = await suiteService.ExecuteRoiInspectionAsync(
                qrRef,
                live: false,
                InspectionSuiteAlgorithmKind.QrCode2DReader,
                qrRois,
                [],
                defaultParams,
                CancellationToken.None);

            Assert.All(qrOut.Items, item => Assert.True(item.IsPass, item.DetailLine));

            Assert.Contains("QR26A", qrOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(qrOut.Items[0].ReferencePoint.X - 306.0), 0.0, 1.5);
            Assert.InRange(Math.Abs(qrOut.Items[0].ReferencePoint.Y - 326.0), 0.0, 1.5);
            Assert.InRange(Math.Abs(qrOut.Items[0].AngleRadians * (180.0 / Math.PI)), 0.0, 1.0);

            // Even when ROI #2 is offset at (872, 334), ReferencePoint must be the actual QR world center (860, 326) and angle -32.5 deg
            Assert.Contains("ROT-325", qrOut.Items[1].BadgeLabel, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(qrOut.Items[1].ReferencePoint.X - 860.0), 0.0, 1.5);
            Assert.InRange(Math.Abs(qrOut.Items[1].ReferencePoint.Y - 326.0), 0.0, 1.5);
            double qrRotDeg1 = qrOut.Items[1].AngleRadians * (180.0 / Math.PI);
            Assert.InRange(qrRotDeg1, -33.7, -31.3);

            // Inverted Light-on-Dark QR at +135.0 deg recovers exact world center (584, 620) and +135.0 deg angle
            Assert.Contains("INV135", qrOut.Items[2].BadgeLabel, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(qrOut.Items[2].ReferencePoint.X - 584.0), 0.0, 1.5);
            Assert.InRange(Math.Abs(qrOut.Items[2].ReferencePoint.Y - 620.0), 0.0, 1.5);
            double qrRotDeg2 = qrOut.Items[2].AngleRadians * (180.0 / Math.PI);
            Assert.InRange(qrRotDeg2, 133.8, 136.2);

            // 5. Full-frame SINGLE ROI multi-barcode decoding across ALL 3 readers (QR, DataMatrix, 1D Barcode):
            //    1 ROI covering the entire 1200x800 scene must return all 3 symbols with individual tight Bounds, world pose, and reading order.
            InspectionSuiteRoiInput[] fullFrameSingleRoi =
            [
                CreateRectRoiInput("FullFrame_AllCodes", 600, 400, 1140, 740)
            ];

            var qrFullOut = await suiteService.ExecuteRoiInspectionAsync(
                qrRef,
                live: false,
                InspectionSuiteAlgorithmKind.QrCode2DReader,
                fullFrameSingleRoi,
                [],
                defaultParams,
                CancellationToken.None);
            Assert.Equal(3, qrFullOut.Items.Length);
            Assert.Contains("ROI 1개", qrFullOut.SummaryTitle, StringComparison.Ordinal);
            Assert.Contains("QR26A", qrFullOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("ROT-325", qrFullOut.Items[1].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("INV135", qrFullOut.Items[2].BadgeLabel, StringComparison.Ordinal);
            Assert.All(qrFullOut.Items, item => Assert.Equal(fullFrameSingleRoi[0].RoiId, item.RoiId));
            Assert.NotEqual(qrFullOut.Items[0].Bounds[0], qrFullOut.Items[1].Bounds[0]);

            var dmFullOut = await suiteService.ExecuteRoiInspectionAsync(
                dmRef,
                live: false,
                InspectionSuiteAlgorithmKind.DataMatrix2DReader,
                fullFrameSingleRoi,
                [],
                defaultParams,
                CancellationToken.None);
            Assert.Equal(3, dmFullOut.Items.Length);
            Assert.Contains("LOT26A", dmFullOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("OK9912", dmFullOut.Items[1].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("ROT23", dmFullOut.Items[2].BadgeLabel, StringComparison.Ordinal);

            var bar1dFullOut = await suiteService.ExecuteRoiInspectionAsync(
                bar1dRef,
                live: false,
                InspectionSuiteAlgorithmKind.Barcode1DReader,
                fullFrameSingleRoi,
                [],
                defaultParams,
                CancellationToken.None);
            Assert.Equal(3, bar1dFullOut.Items.Length);
            Assert.Contains("LOT26A", bar1dFullOut.Items[0].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("OK99", bar1dFullOut.Items[1].BadgeLabel, StringComparison.Ordinal);
            Assert.Contains("ANG18", bar1dFullOut.Items[2].BadgeLabel, StringComparison.Ordinal);

            // 6. ViewModel shared pipeline verification:
            //    3 results from 1 ROI must emit 3 structured logs, 1 aggregated ROI assignment (never overwriting NG),
            //    and 0ms live-preview pose projection without collapsing markers or blinking on ROI move!
            using StubDisplay vmDisplay = new StubDisplay();
            using MainWindowViewModel studio = CreateStudio(vmDisplay);
            studio.Session.InitializeImage(qrRef, new ViewportTransform(1, default));
            Guid singleRoiId = studio.Session.Add(new RoiGeometry(new PointD(600, 400), 1140, 740, 0));
            studio.Session.Select([singleRoiId]);

            studio.Inspections.Selected = InspectionToolKind.InspectionSuite;
            var suiteVm = studio.Inspections.Suite!;
            suiteVm.AutoPreview = false;
            suiteVm.SelectedAlgorithm = InspectionSuiteAlgorithmKind.QrCode2DReader;

            await suiteVm.ExecuteSelected.ExecuteAsync();

            Assert.NotNull(suiteVm.Overlay);
            Assert.Equal(3, suiteVm.Overlay!.Matches.Length);
            Assert.All(suiteVm.Overlay.Matches, m => Assert.Equal(singleRoiId, m.SourceRoiId));
            Assert.Equal(3, studio.Inspections.TotalLogCount);
            Assert.Equal("PASS (3/3)", studio.FindRow(singleRoiId)?.VerdictBadge);

            // Drag/nudge ROI by (+12, -8): 0ms live preview projection must preserve all 3 markers and shift each barcode center by (+12, -8)
            PointD beforeRef0 = suiteVm.Overlay.Matches[0].ReferencePoint;
            PointD beforeRef2 = suiteVm.Overlay.Matches[2].ReferencePoint;
            studio.Session.SetGeometry(singleRoiId, new RoiGeometry(new PointD(612, 392), 1140, 740, 0));

            Assert.NotNull(suiteVm.Overlay);
            Assert.Equal(3, suiteVm.Overlay!.Matches.Length);
            Assert.DoesNotContain("[가이드 미리보기]", suiteVm.Overlay.Summary, StringComparison.Ordinal);
            Assert.InRange(Math.Abs(suiteVm.Overlay.Matches[0].ReferencePoint.X - (beforeRef0.X + 12.0)), 0.0, 1e-3);
            Assert.InRange(Math.Abs(suiteVm.Overlay.Matches[0].ReferencePoint.Y - (beforeRef0.Y - 8.0)), 0.0, 1e-3);
            Assert.InRange(Math.Abs(suiteVm.Overlay.Matches[2].ReferencePoint.X - (beforeRef2.X + 12.0)), 0.0, 1e-3);
            Assert.InRange(Math.Abs(suiteVm.Overlay.Matches[2].ReferencePoint.Y - (beforeRef2.Y - 8.0)), 0.0, 1e-3);

            // Also verify mixed PASS/NG aggregation: if MinIsoContrast is raised to 0.99f so symbols fail contrast,
            // the aggregated ROI badge must be NG (0/3), never masking failures!
            suiteVm.QrCodeMinIsoContrast = 0.99f;
            await suiteVm.ExecuteSelected.ExecuteAsync();
            Assert.Equal("NG (0/3)", studio.FindRow(singleRoiId)?.VerdictBadge);
        }
        finally
        {
            if (Directory.Exists(tempCache))
            {
                try { Directory.Delete(tempCache, recursive: true); } catch { }
            }
        }
    }

    private static void AssertAllOcrItemsHaveValidDiagnostics(IReadOnlyList<InspectionSuiteItemResult> items)
    {
        foreach (InspectionSuiteItemResult item in items)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.BadgeLabel));
            Assert.False(string.IsNullOrWhiteSpace(item.DetailLine));
            Assert.NotNull(item.OcrDiagnostic);
            OcrDiagnosticSnapshot diag = item.OcrDiagnostic!;
            Assert.True(diag.PreviewWidth > 0 && diag.PreviewHeight > 0);
            int pixelCount = diag.PreviewWidth * diag.PreviewHeight;
            Assert.Equal(pixelCount, diag.EnhancedGrayPixels.Length);
            Assert.Equal(pixelCount * 4, diag.BinarizedRgbaPixels.Length);
            Assert.InRange(diag.MeanConfidence, 0.0f, 1.0f);
        }
    }

    private static InspectionSuiteRoiInput CreateRectRoiInput(string name, double cx, double cy, double width, double height)
    {
        double hw = width * 0.5;
        double hh = height * 0.5;
        return new InspectionSuiteRoiInput(
            Guid.NewGuid(),
            name,
            new PointD(cx, cy),
            width,
            height,
            0.0,
            [
                new PointD(cx - hw, cy - hh),
                new PointD(cx + hw, cy - hh),
                new PointD(cx + hw, cy + hh),
                new PointD(cx - hw, cy + hh)
            ]);
    }

    private static void EnsureApplicationThemeLoaded()
    {
        Application app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        }

        Uri tokensUri = new Uri("pack://application:,,,/TeachingToolWpf.Fluent2;component/Themes/Fluent2Tokens.xaml", UriKind.Absolute);
        bool alreadyMerged = app.Resources.MergedDictionaries.Any(dict => dict.Source == tokensUri);
        if (!alreadyMerged)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = tokensUri });
        }
    }

    private static string FindFluent2ProjectDirectory()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "src", "TeachingToolWpf.Fluent2");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/TeachingToolWpf.Fluent2 directory.");
    }

    private MainWindowViewModel CreateStudio(
        StubDisplay display,
        IPrimitiveShapeDetectionService? primitiveOverride = null)
    {
        StubLoader rawLoader = new StubLoader();
        IImageLoader loader = new SourceImageLoader(rawLoader);
        PreInspectionFilterPipeline preFilterPipeline = new PreInspectionFilterPipeline();
        TaughtFontOcvService ocvService = new TaughtFontOcvService(loader, () => null, preFilterPipeline);
        InspectionToolsViewModel inspections = new InspectionToolsViewModel(
            new ShapeMatchingViewModel(
                new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper, preFilterPipeline: preFilterPipeline),
                new TemplateMatchingService(loader, () => null, preFilterPipeline),
                new CpuTemplateMatchingService(loader, () => null, preFilterPipeline: preFilterPipeline),
                new PatMaxMatchingService(loader, () => null, preFilterPipeline)),
            new PrimitiveShapeDetectionViewModel(primitiveOverride ?? new PrimitiveShapeDetectionService(loader, () => null, preFilterPipeline: preFilterPipeline)),
            new EdgeFewShotViewModel(new EdgeFewShotService(loader, () => null, preFilterPipeline)),
            new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null, _ppOcrFixture.Service, preFilterPipeline), ocvService));

        return new MainWindowViewModel(
            display,
            new ViewportLayout(TestViewportWidth, TestViewportHeight),
            loader,
            new LivePreview(display, _ => throw new NotSupportedException()),
            new StubFilterPreview(),
            inspections,
            preFilterPipeline: preFilterPipeline);
    }

    private sealed class RecordingPrimitiveService : IPrimitiveShapeDetectionService
    {
        public List<RoiGeometry?> InspectedRegions { get; } = [];

        public Task<PrimitiveDetectionOutput> DetectAsync(
            ImageReference image,
            bool live,
            RoiGeometry? region,
            PrimitiveDetectionOptions options,
            CancellationToken token,
            RoiExclusion? exclusion = null)
        {
            InspectedRegions.Add(region);
            PointD center = region?.Center ?? new PointD(100, 100);
            PrimitiveShapeDetection detected = new PrimitiveShapeDetection(
                PrimitiveShapeKind.Rectangle,
                new(center.X, center.Y),
                0.0,
                0.96,
                1200.0,
                [
                    new(center.X - 20, center.Y - 15),
                    new(center.X + 20, center.Y - 15),
                    new(center.X + 20, center.Y + 15),
                    new(center.X - 20, center.Y + 15)
                ]);
            return Task.FromResult(new PrimitiveDetectionOutput([detected], 1, false, 0, 1.2));
        }
    }

    private sealed class StubDisplay : IImageDisplay, IFramePresenter, IDisposable
    {
        public ITileSource? Source { get; private set; }

        public long SetSource(ITileSource source)
        {
            Source = source;
            return 1;
        }

        public Task RestartAsync()
        {
            return Task.CompletedTask;
        }

        public void PublishFrame(VideoFrame frame)
        {
        }

        public void ClearFrames()
        {
        }

        public void Dispose()
        {
            Source?.Dispose();
        }
    }

    private sealed class StubLoader : IImageLoader
    {
        public Task<ITileSource> LoadAsync(
            ImageReference reference,
            IProgress<string> progress,
            CancellationToken token)
        {
            throw new NotSupportedException("StubLoader does not load disk images in unit tests.");
        }
    }

    private sealed class StubFilterPreview : IRoiFilterPreview
    {
        public Task<FilterPreviewResult> ProcessAsync(FilterRequest request, CancellationToken token)
        {
            throw new NotSupportedException("StubFilterPreview does not run filter pipelines in unit tests.");
        }
    }
}
