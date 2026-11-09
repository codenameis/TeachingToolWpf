using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CpuMatching.Cuda;
using TeachingToolWpf.Fluent2.TeachingReadiness;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.Fluent2.Views;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionCompute.ILGPU;
using VisionImaging;
using VisionImaging.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TeachingToolWpf.Fluent2;

/// <summary>Fluent 2 Studio 앱의 조립 지점(Composition Root). WPF-UI 테마, 서비스·뷰모델 생성과 종료 수명을 한곳에서 관리한다.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true);
        OpenFluent2Studio(e.Args);
    }

    private void OpenFluent2Studio(string[] args)
    {
        Fluent2StudioWindow window = new Fluent2StudioWindow();
        MainWindow = window;
        window.Title += ImageLoaderHost.Backend == "Streaming"
            ? " · 자체 파일 로더"
            : " · Skia JPEG Scanline";

        if (args.Contains("--software-rendering"))
        {
            window.Viewport.DevicePreference = RenderDevicePreference.Software;
        }

        IImageLoader loader = ImageLoaderHost.Create(GetImageCachePath());
        LivePreview live = new LivePreview(window.Viewport, kind => new SyntheticFrameSource(kind));
        CudaInspectionSession inspectionCompute = new CudaInspectionSession();
        CudaTemplateScoreSession templateCompute = new CudaTemplateScoreSession();
        IndustrialVision.Filters.Gpu.CudaBackend? filterGpu = null;
        RoiFilterPreview filterPreview = new RoiFilterPreview(
            loader,
            window.Viewport.RetainFrame,
            () => filterGpu ??= new IndustrialVision.Filters.Gpu.CudaBackend());

        ShapeMatchingViewModel pattern = new ShapeMatchingViewModel(
            new ShapeMatchingService(
                loader,
                window.Viewport.RetainFrame,
                inspectionCompute.Rent,
                inspectionCompute.PrepareAsync,
                ShapeSearchPipeline.Paper),
            new TemplateMatchingService(loader, window.Viewport.RetainFrame),
            new CpuTemplateMatchingService(loader, window.Viewport.RetainFrame, templateCompute.Rent),
            new PatMaxMatchingService(loader, window.Viewport.RetainFrame));

        PrimitiveShapeDetectionViewModel shapes = new PrimitiveShapeDetectionViewModel(
            new PrimitiveShapeDetectionService(
                loader,
                window.Viewport.RetainFrame,
                () => new CudaPrimitivePreprocessor()));

        EdgeFewShotViewModel fewShot = new EdgeFewShotViewModel(
            new EdgeFewShotService(loader, window.Viewport.RetainFrame));

        InspectionSuiteViewModel suite = new InspectionSuiteViewModel(
            new InspectionSuiteService(loader, window.Viewport.RetainFrame));

        InspectionToolsViewModel inspections = new InspectionToolsViewModel(pattern, shapes, fewShot, suite);
        MainWindowViewModel studioModel = new MainWindowViewModel(
            window.Viewport,
            new(),
            loader,
            live,
            filterPreview,
            inspections,
            selectImage: () => TeachingToolWpf.Views.ImageFilePicker.Select(window));

        SubPixelTeachingReadinessEngine readinessEngine = new SubPixelTeachingReadinessEngine();
        Fluent2StudioViewModel viewModel = new Fluent2StudioViewModel(studioModel, readinessEngine);
        window.Bind(viewModel);

        bool filtersDrained = false;
        bool drainingFilters = false;
        window.Closing += async (_, closeArgs) =>
        {
            if (filtersDrained)
            {
                return;
            }

            closeArgs.Cancel = true;
            if (drainingFilters)
            {
                return;
            }

            drainingFilters = true;
            studioModel.Filters.Dispose();
            await Task.Yield();
            await studioModel.Filters.WhenIdle;
            filterPreview.Dispose();
            filterGpu?.Dispose();
            filtersDrained = true;
            window.Close();
        };

        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            ResourceCleanup.DisposeAll(studioModel, inspectionCompute, templateCompute);
        };

        window.Loaded += (_, _) => studioModel.Initialize.Execute(null);
        window.Show();
    }

    private static string GetImageCachePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachingToolWpf",
            "ImageCache");
    }
}
