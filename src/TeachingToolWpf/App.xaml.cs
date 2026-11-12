using System.IO;
using System.Windows;
using CpuMatching.Cuda;
using Microsoft.Win32;
using TeachingToolWpf.Diagnostics;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using TeachingToolWpf.Views;
using VisionAlgorithm.Verification;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionCompute.ILGPU;
using VisionImaging;
using VisionOcr.PpOcr;

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
        var preFilterPipeline = new Imaging.Matching.PreInspectionFilterPipeline();
        var pattern = new ShapeMatchingViewModel(
            new Imaging.Matching.ShapeMatchingService(loader, window.Viewport.RetainFrame, inspectionCompute.Rent, inspectionCompute.PrepareAsync,
                Imaging.Matching.ShapeSearchPipeline.Paper, preFilterPipeline),
            new Imaging.Matching.TemplateMatchingService(loader, window.Viewport.RetainFrame, preFilterPipeline),
            new Imaging.Matching.CpuTemplateMatchingService(loader, window.Viewport.RetainFrame, templateCompute.Rent, preFilterPipeline),
            new Imaging.Matching.PatMaxMatchingService(loader, window.Viewport.RetainFrame, preFilterPipeline));
        var shapes = new PrimitiveShapeDetectionViewModel(new Imaging.Matching.PrimitiveShapeDetectionService(loader,
            window.Viewport.RetainFrame, () => new CudaPrimitivePreprocessor(), preFilterPipeline));
        var fewShot = new EdgeFewShotViewModel(new Imaging.Matching.EdgeFewShotService(loader, window.Viewport.RetainFrame, preFilterPipeline));
        var ppOcrService = new PpOcrInferenceService(PpOcrModelOptions.FromDirectory(ResolvePpOcrModelDirectory()));
        var ocvService = new Imaging.Matching.TaughtFontOcvService(loader, window.Viewport.RetainFrame, preFilterPipeline);
        var suite = new InspectionSuiteViewModel(
            new Imaging.Matching.InspectionSuiteService(loader, window.Viewport.RetainFrame, ppOcrService, preFilterPipeline, new Master8kSuiteVerificationRunner()),
            ocvService,
            selectOcvFontOpenPath: () => SelectOcvFontOpenPath(window),
            selectOcvFontSavePath: () => SelectOcvFontSavePath(window),
            confirmApplyPpOcrToOcv: (roiName, text, conf) => ConfirmApplyPpOcrToOcv(window, roiName, text, conf));
        var inspections = new InspectionToolsViewModel(pattern, shapes, fewShot, suite);
        var model = new MainWindowViewModel(window.Viewport, new(), loader, live, filterPreview, inspections,
            selectImage: () => ImageFilePicker.Select(window),
            preFilterPipeline: preFilterPipeline);
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
        window.Closed += (_, _) => ResourceCleanup.DisposeAll(model, inspectionCompute, templateCompute, ppOcrService);
        if (!DiagnosticStartup.TryAttach(window, window.Viewport, model, args, () => inspectionCompute.InitializationMilliseconds))
        {
            window.Loaded += (_, _) => model.Initialize.Execute(null);
        }
        window.Show();
    }

    private static string ResolvePpOcrModelDirectory()
    {
        string? envOverride = Environment.GetEnvironmentVariable("PPOCR_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return Path.GetFullPath(envOverride);
        }

        string current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
        {
            string candidate = Path.Combine(current, "models", "ppocr");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
        }

        return Path.Combine(AppContext.BaseDirectory, "models", "ppocr");
    }

    private static string? SelectOcvFontOpenPath(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "티칭 골든 폰트 불러오기",
            Filter = "Taught OCV Font (*.ocvfont.json)|*.ocvfont.json|JSON Files (*.json)|*.json",
            DefaultExt = ".ocvfont.json"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static string? SelectOcvFontSavePath(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "티칭 골든 폰트 저장",
            Filter = "Taught OCV Font (*.ocvfont.json)|*.ocvfont.json|JSON Files (*.json)|*.json",
            DefaultExt = ".ocvfont.json",
            FileName = "taught-font.ocvfont.json"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static bool ConfirmApplyPpOcrToOcv(Window owner, string roiName, string recognizedText, float meanConfidence)
        => MessageBox.Show(
            owner,
            $"선택 ROI '{roiName}'의 PP-OCR 판독 결과 '{recognizedText}' (평균 신뢰도 {meanConfidence:P1})를 OCV 기대 문자열(기준값)로 적용하시겠습니까?\n\n※ 오인식된 문자열로 골든 폰트를 티칭하면 실제 불량이 정상 통과될 수 있으므로 인쇄 내용과 일치하는지 반드시 확인해 주세요.",
            "OCV 기대 문자열 적용 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

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
