using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VisionImaging;

namespace TeachingToolWpf;

public partial class MainWindow : Window
{
    private readonly Presentation.EditorViewModel _model;
    private readonly Controls.RoiListBinding _selectionBinding;
    private readonly VisionCompute.ILGPU.CudaInspectionSession _inspectionCompute = new();

    public MainWindow()
    {
        InitializeComponent();
        var smoke = Environment.GetCommandLineArgs().Contains("--smoke");
        var inspectionSmoke = Environment.GetCommandLineArgs().Contains("--inspection-smoke");
        var largeImage = Environment.GetCommandLineArgs().Contains("--large-image");
        var memoryCheck = Environment.GetCommandLineArgs().Contains("--memory-check");
        var paperLatency = Environment.GetCommandLineArgs().Contains("--paper-latency-smoke");
        var rotationInspection = Environment.GetCommandLineArgs().Contains("--rotation-inspection-smoke");
        var cache = smoke || inspectionSmoke || largeImage || memoryCheck || paperLatency || rotationInspection ? System.IO.Path.GetFullPath("artifacts/image-cache") : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachingToolWpf", "ImageCache");
        VisionImaging.VipsRuntime.Configure();
        var live = new Presentation.LivePreview(Viewport, kind => new VisionImaging.SyntheticFrameSource(kind));
        var loader = new VisionImaging.ImageLoader(cache);
        _model = new(Viewport, loader, live, new VisionCanvas.Imaging.Filters.RoiFilterPreview(loader, Viewport.RetainFrame),
            new Imaging.Matching.ShapeMatchingService(loader, Viewport.RetainFrame, _inspectionCompute.Rent, _inspectionCompute.PrepareAsync,
                Imaging.Matching.ShapeSearchPipeline.Paper),
            new Imaging.Matching.TemplateMatchingService(loader, Viewport.RetainFrame),
            new Imaging.Matching.PrimitiveShapeDetectionService(loader, Viewport.RetainFrame));
        live.Failed += ex => Dispatcher.BeginInvoke(() => _model.Fail(ex));
        DataContext = _model;
        Viewport.Input = _model.Input;
        Viewport.InputEnabled = () => _model.CanNavigate;
        Viewport.KeyAction = _model.HandleKey;
        Viewport.InputFailed += _model.Fail;
        Viewport.OverlayPresented += _model.Matching.RecordOverlayPresentation;
        Viewport.RendererStatusChanged += status => _model.GpuStatus = status;
        Viewport.MetricsChanged += value => _model.Performance = $"CPU {value.CpuMilliseconds:F1} ms · GPU {value.GpuMilliseconds:F1} ms · 표시 드롭 {value.Dropped}";
        Viewport.Tiles.Failed += message => Dispatcher.BeginInvoke(() => _model.Status = message);
        _selectionBinding = new(Regions.List, _model);
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || args.OriginalSource is TextBox)
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

        Closed += (_, _) =>
        {
            try
            {
                _selectionBinding.Dispose();
                _model.Dispose();
            }
            finally
            {
                _inspectionCompute.Dispose();
            }
        };
    }
}
