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
using TeachingToolWpf.Models;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Presentation;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using Xunit;

namespace TeachingToolWpf.Fluent2.Tests;

public sealed class Fluent2StudioViewModelTests
{
    private const int TestImageDimension = 1000;
    private const int TestViewportWidth = 1000;
    private const int TestViewportHeight = 700;

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
        Assert.Equal(10, CountSubstrings(File.ReadAllText(Path.Combine(viewsDir, "FluentSuiteCard.xaml")), "<Slider "));
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

    private static MainWindowViewModel CreateStudio(
        StubDisplay display,
        IPrimitiveShapeDetectionService? primitiveOverride = null)
    {
        StubLoader rawLoader = new StubLoader();
        IImageLoader loader = new SourceImageLoader(rawLoader);
        InspectionToolsViewModel inspections = new InspectionToolsViewModel(
            new ShapeMatchingViewModel(
                new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper),
                new TemplateMatchingService(loader, () => null),
                new CpuTemplateMatchingService(loader, () => null),
                new PatMaxMatchingService(loader, () => null)),
            new PrimitiveShapeDetectionViewModel(primitiveOverride ?? new PrimitiveShapeDetectionService(loader, () => null)),
            new EdgeFewShotViewModel(new EdgeFewShotService(loader, () => null)),
            new InspectionSuiteViewModel(new InspectionSuiteService(loader, () => null)));

        return new MainWindowViewModel(
            display,
            new ViewportLayout(TestViewportWidth, TestViewportHeight),
            loader,
            new LivePreview(display, _ => throw new NotSupportedException()),
            new StubFilterPreview(),
            inspections);
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
