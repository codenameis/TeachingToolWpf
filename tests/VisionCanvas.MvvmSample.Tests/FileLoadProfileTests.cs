using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using VisionCanvas.Core.Editing;
using VisionCanvas.MvvmSample.ViewModels;
using VisionCanvas.MvvmSample.Views;
using VisionCanvas.Presentation;
using VisionImaging;
using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace VisionCanvas.MvvmSample.Tests;

public sealed partial class EditorWindowTests
{
    // 대용량 실측은 명시적으로 요청했을 때만 실행한다. 샘플 앱에는 진단 분기를 추가하지 않는다.
    [FileLoadProfile]
    public Task MeasureKen32CommandThroughRenderedCapture() => OnStaAsync(async () =>
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("VISIONCANVAS_LOAD_PROFILE")!, RepositoryRoot());
        Assert.False(Directory.Exists(root), "기존 캐시와 보고서는 덮어쓰지 않습니다.");
        Directory.CreateDirectory(root);
        var file = Path.Combine(RepositoryRoot(), "ken32.png");
        Assert.True(File.Exists(file));
        var window = new EditorWindow { ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
        var editor = new EditorSession();
        var layout = new ViewportLayout();
        var loader = new TimedLoader(new ImageLoader(Path.Combine(root, "cache")));
        using var model = new EditorViewModel(editor, new(editor), layout,
            new(editor, window.Viewport, layout, loader), new Picker { Reference = new(file, "ken32.png", 0, 0) });
        window.DataContext = model;
        window.Closing += (_, _) => model.Dispose();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        window.Viewport.RendererStatusChanged += status =>
        {
            if (status.StartsWith("DX12 ·", StringComparison.Ordinal))
            {
                ready.TrySetResult(status);
            }
            if (status.Contains("오류", StringComparison.Ordinal))
            {
                errors.Add(status);
            }
        };
        window.Viewport.Tiles.Failed += errors.Add;
        try
        {
            var startup = Stopwatch.StartNew();
            window.Show();
            var adapter = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var rendererReady = startup.Elapsed.TotalMilliseconds;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var runs = new List<object>();
            for (var run = 0; run < 2; run++)
            {
                var watch = Stopwatch.StartNew();
                await model.OpenCommand.ExecuteAsync();
                var committed = watch.Elapsed.TotalMilliseconds;
                Assert.Equal(17280, model.Scene.Image?.Width);
                Assert.Equal(30720, model.Scene.Image?.Height);
                Assert.True(model.IsInputEnabled);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Assert.Same(model.Scene, window.Viewport.Scene);
                var capture = Path.Combine(root, $"capture-{run}.png");
                window.Viewport.Capture(capture);
                // Capture는 현재 장면의 overview 준비와 GPU fence 완료를 기다린다.
                // PNG 파일 닫힘까지 기다리므로 아래 값은 화면 표시 지연의 상한이며 캡처 비용도 포함한다.
                await UntilAsync(() => IsClosedCapture(capture));
                runs.Add(new
                {
                    Cache = run == 0 ? "fresh" : "reuse",
                    LoaderMilliseconds = loader.Milliseconds,
                    CommandToCommitMilliseconds = committed,
                    CommandToCaptureMilliseconds = watch.Elapsed.TotalMilliseconds
                });
            }
            Assert.Empty(errors);
            using var binary = File.OpenRead(typeof(RawPyramidSource).Assembly.Location);
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                Adapter = adapter,
                RendererReadyMilliseconds = rendererReady,
                Runs = runs,
                PyramidBinarySha256 = Convert.ToHexString(SHA256.HashData(binary)),
                Note = "Offscreen WPF/DX12. Command starts after renderer initialization. Capture includes GPU readback, PNG encoding and 20ms polling; not physical monitor scan-out. OS file cache not cleared."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            model.Dispose();
            window.Close();
        }
    });

    private static bool IsClosedCapture(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return file.Length > 1000;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed class TimedLoader(IImageLoader inner) : IImageLoader
    {
        public double Milliseconds
        {
            get; private set;
        }
        public async Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var source = await inner.LoadAsync(reference, progress, token);
            Milliseconds = watch.Elapsed.TotalMilliseconds;
            return source;
        }
    }
}

public sealed class FileLoadProfileAttribute : FactAttribute
{
    public FileLoadProfileAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VISIONCANVAS_LOAD_PROFILE")))
        {
            Skip = "수동 실측: VISIONCANVAS_LOAD_PROFILE에 새 결과 폴더를 지정하세요.";
        }
    }
}
