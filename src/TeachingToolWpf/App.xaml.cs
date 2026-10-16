using System.Windows;
using CpuMatching.Cuda;
using TeachingToolWpf.Diagnostics;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionCompute.ILGPU;
using VisionImaging;

namespace TeachingToolWpf;

/// <summary>앱의 조립 지점. 구현체 생성과 종료 순서를 한곳에서 관리한다.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--repeated-roi-benchmark"))
        {
            OpenRepeatedRoiWindow(e.Args);
            return;
        }
        OpenMainWindow(e.Args);
    }

    private void OpenRepeatedRoiWindow(string[] args)
    {
        var window = new RepeatedRoiWindow();
        MainWindow = window;
        if (args.Contains("--software-rendering"))
        {
            window.Viewport.DevicePreference = RenderDevicePreference.Software;
        }
        var loader = ImageLoaderHost.Create(System.IO.Path.GetFullPath("artifacts/image-cache"));
        var model = new RepeatedRoiViewModel(window.Viewport, new(), new("sample:8192:8192", "8K 반복 ROI 시험", 8192, 8192));
        window.Bind(model);
        window.Closed += (_, _) => model.Dispose();
        RepeatedRoiRun.Attach(window, window.Viewport, model, loader);
        window.Show();
    }

    private void OpenMainWindow(string[] args)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Title += ImageLoaderHost.Backend == "Streaming" ? " · 자체 파일 로더" : " · Skia JPEG Scanline";
        if (args.Contains("--software-rendering"))
        {
            window.Viewport.DevicePreference = RenderDevicePreference.Software;
        }
        var loader = ImageLoaderHost.Create(GetImageCachePath(args));
        var live = new LivePreview(window.Viewport, kind => new SyntheticFrameSource(kind));
        var inspectionCompute = new CudaInspectionSession();
        var templateCompute = new CudaTemplateScoreSession();
        IndustrialVision.Filters.Gpu.CudaBackend? filterGpu = null;
        var filterPreview = new RoiFilterPreview(loader, window.Viewport.RetainFrame,
            () => filterGpu ??= new IndustrialVision.Filters.Gpu.CudaBackend());
        var tdcm = new Imaging.Matching.TdcmMatchingService(loader, window.Viewport.RetainFrame,
            options => options.Backend == PoseMatch.Tdcm.TdcmBackend.TorchSharp
                ? new PoseMatch.Tdcm.TorchSharp.TorchTdcmMatcher(options.Threads, options.Device, options.WeightsPath)
                : new PoseMatch.Tdcm.TdcmMatcher(options.Threads, options.Device));
        var pattern = new ShapeMatchingViewModel(
            new Imaging.Matching.ShapeMatchingService(loader, window.Viewport.RetainFrame, inspectionCompute.Rent, inspectionCompute.PrepareAsync,
                Imaging.Matching.ShapeSearchPipeline.Paper),
            new Imaging.Matching.TemplateMatchingService(loader, window.Viewport.RetainFrame),
            tdcm, new Imaging.Matching.CpuTemplateMatchingService(loader, window.Viewport.RetainFrame, templateCompute.Rent));
        var shapes = new PrimitiveShapeDetectionViewModel(new Imaging.Matching.PrimitiveShapeDetectionService(loader, window.Viewport.RetainFrame));
        var inspections = new InspectionToolsViewModel(pattern, shapes);
        var model = new MainWindowViewModel(window.Viewport, new(), loader, live, filterPreview, inspections,
            selectImage: () => ImageFilePicker.Select(window));
        window.Bind(model);

        var filtersDrained = false;
        var drainingFilters = false;
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
            // Keep the dispatcher alive until cancellation has drained native/GPU work.
            model.Filters.Dispose();
            // WhenIdle may already be complete; leave Closing before calling Close again.
            await Task.Yield();
            await model.Filters.WhenIdle;
            filterPreview.Dispose();
            filterGpu?.Dispose();
            filtersDrained = true;
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            try
            {
                model.Dispose();
            }
            finally
            {
                try
                {
                    tdcm.Dispose();
                }
                finally
                {
                    try
                    {
                        inspectionCompute.Dispose();
                    }
                    finally
                    {
                        templateCompute.Dispose();
                    }
                }
            }
        };
        if (!DiagnosticStartup.TryAttach(window, window.Viewport, model, args, () => inspectionCompute.InitializationMilliseconds))
        {
            window.Loaded += (_, _) => model.Initialize.Execute(null);
        }
        window.Show();
    }

    private static string GetImageCachePath(string[] args)
    {
        var smoke = args.Contains("--smoke");
        var inspectionSmoke = args.Contains("--inspection-smoke");
        var largeImage = args.Contains("--large-image");
        var memoryCheck = args.Contains("--memory-check");
        var paperLatency = args.Contains("--paper-latency-smoke");
        var rotationInspection = args.Contains("--rotation-inspection-smoke");
        var cache = smoke || inspectionSmoke || largeImage || memoryCheck || paperLatency || rotationInspection ? System.IO.Path.GetFullPath("artifacts/image-cache") : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachingToolWpf", "ImageCache");
        var cadenceCache = Array.IndexOf(args, "--cadence-cache");
        if (cadenceCache >= 0 && cadenceCache + 1 < args.Length)
        {
            cache = System.IO.Path.GetFullPath(args[cadenceCache + 1]);
        }
        return cache;
    }
}
