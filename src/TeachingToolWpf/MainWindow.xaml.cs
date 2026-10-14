using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionImaging;

namespace TeachingToolWpf;

public partial class MainWindow : Window
{
    private readonly Presentation.EditorViewModel _model;
    private readonly Controls.RoiListBinding _selectionBinding;
    private readonly Controls.CrosshairLayerBinding _guideBinding;
    private Views.ConfigPage? _configPage;
    private readonly VisionCompute.ILGPU.CudaInspectionSession _inspectionCompute = new();
    private readonly VisionCanvas.Imaging.Filters.RoiFilterPreview _filterPreview;
    private IndustrialVision.Filters.Gpu.CudaBackend? _filterGpu;

    public MainWindow()
    {
        InitializeComponent();
        Title += ImageLoaderHost.Backend == "Streaming" ? " · 자체 파일 로더" : " · Skia JPEG Scanline";
        if (Environment.GetCommandLineArgs().Contains("--software-rendering"))
        {
            Viewport.DevicePreference = VisionCanvas.Rendering.RenderDevicePreference.Software;
        }
        var smoke = Environment.GetCommandLineArgs().Contains("--smoke");
        var inspectionSmoke = Environment.GetCommandLineArgs().Contains("--inspection-smoke");
        var largeImage = Environment.GetCommandLineArgs().Contains("--large-image");
        var memoryCheck = Environment.GetCommandLineArgs().Contains("--memory-check");
        var paperLatency = Environment.GetCommandLineArgs().Contains("--paper-latency-smoke");
        var rotationInspection = Environment.GetCommandLineArgs().Contains("--rotation-inspection-smoke");
        var cache = smoke || inspectionSmoke || largeImage || memoryCheck || paperLatency || rotationInspection ? System.IO.Path.GetFullPath("artifacts/image-cache") : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachingToolWpf", "ImageCache");
        var cadenceCache = Array.IndexOf(Environment.GetCommandLineArgs(), "--cadence-cache");
        if (cadenceCache >= 0 && cadenceCache + 1 < Environment.GetCommandLineArgs().Length)
        {
            cache = System.IO.Path.GetFullPath(Environment.GetCommandLineArgs()[cadenceCache + 1]);
        }
        var live = new Presentation.LivePreview(Viewport, kind => new VisionImaging.SyntheticFrameSource(kind));
        var loader = ImageLoaderHost.Create(cache);
        _filterPreview = new(loader, Viewport.RetainFrame, () => _filterGpu ??= new IndustrialVision.Filters.Gpu.CudaBackend());
        _model = new(Viewport, loader, live, _filterPreview,
            new Imaging.Matching.ShapeMatchingService(loader, Viewport.RetainFrame, _inspectionCompute.Rent, _inspectionCompute.PrepareAsync,
                Imaging.Matching.ShapeSearchPipeline.Paper),
            new Imaging.Matching.TemplateMatchingService(loader, Viewport.RetainFrame),
            new Imaging.Matching.PrimitiveShapeDetectionService(loader, Viewport.RetainFrame));
        live.Failed += ex => Dispatcher.BeginInvoke(() => _model.Fail(ex));
        DataContext = _model;
        _guideBinding = new(Viewport, _model.GuideSettings);
        Viewport.Input = _model.Input;
        Viewport.InputEnabled = () => _model.CanNavigate;
        Viewport.KeyAction = _model.HandleKey;
        Viewport.InputFailed += _model.Fail;
        Viewport.OverlayPresented += _model.Matching.RecordOverlayPresentation;
        Viewport.RendererStatusChanged += status => _model.GpuStatus = status;
        Viewport.MetricsChanged += value => _model.Performance = value.GpuMilliseconds is { } gpuMilliseconds
            ? $"CPU {value.CpuMilliseconds:F1} ms · GPU {gpuMilliseconds:F1} ms · 표시 드롭 {value.Dropped}"
            : $"CPU 제출 {value.CpuMilliseconds:F1} ms · 장치 시간 미측정 · 표시 드롭 {value.Dropped}";
        Viewport.Tiles.Failed += message => Dispatcher.BeginInvoke(() => _model.Status = message);
        _selectionBinding = new(Regions.List, _model);
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.RangeBase ||
                args.OriginalSource is TextBox or System.Windows.Controls.Primitives.RangeBase)
            {
                return;
            }

            args.Handled = _model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        if (rotationInspection)
        {
            Diagnostics.RotationInspectionSmoke.Attach(this, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--shape-target-smoke") || paperLatency)
        {
            Diagnostics.ShapeTargetSmoke.Attach(this, Viewport, _model, () => _inspectionCompute.InitializationMilliseconds);
        }
        else if (inspectionSmoke)
        {
            Diagnostics.InspectionSmoke.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--roi-rotation-benchmark"))
        {
            Diagnostics.RoiRotationRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--frame-cadence-check"))
        {
            Diagnostics.FrameCadenceRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--main-repeated-benchmark"))
        {
            Diagnostics.RepeatedDocumentRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--selection-benchmark"))
        {
            Diagnostics.SelectionRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--responsive-editing"))
        {
            Diagnostics.ResponsiveEditingRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--roi-drag-benchmark"))
        {
            Diagnostics.RoiDragRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--roi-render-benchmark"))
        {
            Diagnostics.RoiRenderRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--clipboard-check"))
        {
            Diagnostics.ClipboardRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--roi-benchmark"))
        {
            Diagnostics.RoiInteractionRun.Attach(this, Viewport, _model);
        }
        else if (memoryCheck)
        {
            Diagnostics.ImageReplacementRun.Attach(this, Viewport, _model);
        }
        else if (largeImage)
        {
            Diagnostics.LargeImageRun.Attach(this, Viewport, _model);
        }
        else if (Environment.GetCommandLineArgs().Contains("--benchmark"))
        {
            Diagnostics.BenchmarkRun.Attach(this, Viewport, _model);
        }
        else if (smoke)
        {
            Diagnostics.RenderSmoke.Attach(this, Viewport, _model);
        }
        else
        {
            Loaded += async (_, _) =>
            {
                try
                {
                    await _model.InitializeAsync();
                }
                catch (Exception ex)
                {
                    _model.Fail(ex);
                }
            };
        }

        var filtersDrained = false;
        var drainingFilters = false;
        Closing += async (_, args) =>
        {
            if (filtersDrained)
            {
                return;
            }
            args.Cancel = true;
            if (drainingFilters)
            {
                return;
            }
            drainingFilters = true;
            // Keep the dispatcher alive until cancellation has drained native/GPU work.
            _model.Filters.Dispose();
            // WhenIdle may already be complete; leave Closing before calling Close again.
            await Task.Yield();
            await _model.Filters.WhenIdle;
            _filterPreview.Dispose();
            _filterGpu?.Dispose();
            filtersDrained = true;
            Close();
        };
        Closed += (_, _) =>
        {
            try
            {
                _selectionBinding.Dispose();
                _guideBinding.Dispose();
                _model.Dispose();
            }
            finally
            {
                _inspectionCompute.Dispose();
            }
        };
    }

    private void OpenConfiguration(object sender, RoutedEventArgs e)
    {
        if (_configPage is null)
        {
            _configPage = new()
            {
                Owner = this,
                DataContext = _model
            };
            _configPage.Closed += (_, _) => _configPage = null;
            _configPage.Show();
        }
        else
        {
            if (_configPage.WindowState == WindowState.Minimized)
            {
                _configPage.WindowState = WindowState.Normal;
            }
            _configPage.Activate();
        }
    }
}
