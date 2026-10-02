using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TeachingToolWpf
{
    public partial class MainWindow : Window
    {
        private readonly Presentation.EditorViewModel model;
        private readonly Controls.RoiListBinding selectionBinding;
        public MainWindow()
        {
            InitializeComponent();
            var smoke = Environment.GetCommandLineArgs().Contains("--smoke");
            var largeImage = Environment.GetCommandLineArgs().Contains("--large-image");
            var memoryCheck = Environment.GetCommandLineArgs().Contains("--memory-check");
            var cache = smoke || largeImage || memoryCheck ? System.IO.Path.GetFullPath("artifacts/image-cache") : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeachingToolWpf", "ImageCache");
            Imaging.VipsRuntime.Configure();
            var live = new Presentation.LivePreview(Viewport, kind => new Imaging.SyntheticFrameSource(kind));
            var loader = new Imaging.ImageLoader(cache);
            model = new(Viewport, loader, live, new Imaging.Filters.RoiFilterPreview(loader, Viewport.RetainFrame));
            live.Failed += ex => Dispatcher.BeginInvoke(() => model.Fail(ex));
            DataContext = model;
            Viewport.Input = model.Input; Viewport.InputEnabled = () => model.CanEdit; Viewport.KeyAction = model.HandleKey;
            Viewport.InputFailed += model.Fail;
            Viewport.RendererStatusChanged += status => model.GpuStatus = status;
            Viewport.MetricsChanged += value => model.Performance = $"CPU {value.CpuMilliseconds:F1} ms · GPU {value.GpuMilliseconds:F1} ms · 표시 드롭 {value.Dropped}";
            Viewport.Tiles.Failed += message => Dispatcher.BeginInvoke(() => model.Status = message);
            selectionBinding = new(Regions.List, model);
            PreviewKeyDown += (_, args) =>
            {
                if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || args.OriginalSource is TextBox) return;
                args.Handled = model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            };
            if (Environment.GetCommandLineArgs().Contains("--roi-benchmark")) Diagnostics.RoiInteractionRun.Attach(this, Viewport, model);
            else if (memoryCheck) Diagnostics.ImageReplacementRun.Attach(this, Viewport, model);
            else if (largeImage) Diagnostics.LargeImageRun.Attach(this, Viewport, model);
            else if (Environment.GetCommandLineArgs().Contains("--benchmark")) Diagnostics.BenchmarkRun.Attach(this, Viewport, model);
            else if (smoke) Diagnostics.RenderSmoke.Attach(this, Viewport, model);
            else Loaded += async (_, _) => { try { await model.InitializeAsync(); } catch (Exception ex) { model.Fail(ex); } };
            Closed += (_, _) => { selectionBinding.Dispose(); model.Dispose(); };
        }
    }
}
