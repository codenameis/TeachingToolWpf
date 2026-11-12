using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CpuMatching.Cuda;
using Microsoft.Win32;
using TeachingToolWpf.Fluent2.TeachingReadiness;
using TeachingToolWpf.Fluent2.ViewModels;
using TeachingToolWpf.Fluent2.Views;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Services;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.Verification;
using VisionCanvas.Imaging.Filters;
using VisionCanvas.Rendering;
using VisionCompute.ILGPU;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionOcr.PpOcr;
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
        PreInspectionFilterPipeline preFilterPipeline = new PreInspectionFilterPipeline();

        ShapeMatchingViewModel pattern = new ShapeMatchingViewModel(
            new ShapeMatchingService(
                loader,
                window.Viewport.RetainFrame,
                inspectionCompute.Rent,
                inspectionCompute.PrepareAsync,
                ShapeSearchPipeline.Paper,
                preFilterPipeline),
            new TemplateMatchingService(loader, window.Viewport.RetainFrame, preFilterPipeline),
            new CpuTemplateMatchingService(loader, window.Viewport.RetainFrame, templateCompute.Rent, preFilterPipeline),
            new PatMaxMatchingService(loader, window.Viewport.RetainFrame, preFilterPipeline));

        PrimitiveShapeDetectionViewModel shapes = new PrimitiveShapeDetectionViewModel(
            new PrimitiveShapeDetectionService(
                loader,
                window.Viewport.RetainFrame,
                () => new CudaPrimitivePreprocessor(),
                preFilterPipeline));

        EdgeFewShotViewModel fewShot = new EdgeFewShotViewModel(
            new EdgeFewShotService(loader, window.Viewport.RetainFrame, preFilterPipeline));

        PpOcrInferenceService ppOcrService = new PpOcrInferenceService(
            PpOcrModelOptions.FromDirectory(ResolvePpOcrModelDirectory()));
        TaughtFontOcvService ocvService = new TaughtFontOcvService(loader, window.Viewport.RetainFrame, preFilterPipeline);

        InspectionSuiteViewModel suite = new InspectionSuiteViewModel(
            new InspectionSuiteService(loader, window.Viewport.RetainFrame, ppOcrService, preFilterPipeline, new Master8kSuiteVerificationRunner()),
            ocvService,
            selectOcvFontOpenPath: () => SelectOcvFontOpenPath(window),
            selectOcvFontSavePath: () => SelectOcvFontSavePath(window),
            confirmApplyPpOcrToOcv: (roiName, text, conf) => ConfirmApplyPpOcrToOcv(window, roiName, text, conf));

        InspectionToolsViewModel inspections = new InspectionToolsViewModel(pattern, shapes, fewShot, suite);
        MainWindowViewModel studioModel = new MainWindowViewModel(
            window.Viewport,
            new(),
            loader,
            live,
            filterPreview,
            inspections,
            selectImage: () => TeachingToolWpf.Views.ImageFilePicker.Select(window),
            preFilterPipeline: preFilterPipeline);

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
            ResourceCleanup.DisposeAll(studioModel, inspectionCompute, templateCompute, ppOcrService);
        };

        window.Loaded += (_, _) => studioModel.Initialize.Execute(null);
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
        => System.Windows.MessageBox.Show(
            owner,
            $"선택 ROI '{roiName}'의 PP-OCR 판독 결과 '{recognizedText}' (평균 신뢰도 {meanConfidence:P1})를 OCV 기대 문자열(기준값)로 적용하시겠습니까?\n\n※ 오인식된 문자열로 골든 폰트를 티칭하면 실제 불량이 정상 통과될 수 있으므로 인쇄 내용과 일치하는지 반드시 확인해 주세요.",
            "OCV 기대 문자열 적용 확인",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    private static string GetImageCachePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TeachingToolWpf",
            "ImageCache");
    }
}
