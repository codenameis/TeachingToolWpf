using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Rendering;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Views;

public partial class RepeatedRoiWindow : Window
{
    private readonly CancellationTokenSource _loading = new();
    internal RepeatedRoiViewModel Model
    {
        get;
    }
    internal string Adapter { get; private set; } = "";

    public RepeatedRoiWindow() : this(new("sample:8192:8192", "8K 반복 ROI 시험", 8192, 8192))
    {
    }

    public RepeatedRoiWindow(ImageReference image)
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Contains("--software-rendering"))
        {
            Viewport.DevicePreference = RenderDevicePreference.Software;
        }
        Model = new(Viewport, new(), image);
        DataContext = Model;
        Viewport.InputFailed += ex => Model.Status = ex.Message;
        Viewport.Tiles.Failed += message => Dispatcher.BeginInvoke(() => Model.Status = message);
        Viewport.RendererStatusChanged += status =>
        {
            Adapter = status;
            Model.Status = status;
        };
        Viewport.MetricsChanged += metrics =>
        {
            var gpu = metrics.GpuMilliseconds is { } elapsed ? $"{elapsed:F2} ms" : "미측정 (WARP)";
            Model.Performance = $"CPU {metrics.CpuMilliseconds:F2} ms · GPU {gpu} · 최근 프레임 간격 {metrics.FrameIntervalMilliseconds:F2} ms";
        };
        PreviewKeyDown += (_, args) =>
        {
            if (Keyboard.FocusedElement is not TextBoxBase)
            {
                args.Handled = Model.HandleKey(args.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
        };
        Closed += (_, _) =>
        {
            _loading.Cancel();
            _loading.Dispose();
            Model.Dispose();
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await Model.LoadImageAsync(ImageLoaderHost.Create(System.IO.Path.GetFullPath("artifacts/image-cache")), _loading.Token);
                if (Environment.GetCommandLineArgs().Contains("--repeated-roi-benchmark"))
                {
                    await Diagnostics.RepeatedRoiRun.RunAsync(this, Viewport, Model);
                }
            }
            catch (OperationCanceledException)
            {
                Model.Status = "읽기 취소";
            }
            catch (Exception ex)
            {
                Model.Status = ex.Message;
                if (Environment.GetCommandLineArgs().Contains("--repeated-roi-benchmark"))
                {
                    System.IO.Directory.CreateDirectory("artifacts");
                    System.IO.File.WriteAllText("artifacts/repeated-roi-error.txt", ex.ToString());
                    Environment.ExitCode = 1;
                    Close();
                }
            }
        };
    }

}
