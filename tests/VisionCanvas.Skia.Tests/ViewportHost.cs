using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VisionCanvas.Controls;
using VisionCanvas.Rendering;

namespace VisionCanvas.Skia.Tests;

internal sealed record Capture(int Width, int Height, byte[] Pixels);

internal sealed class ViewportHost : IDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ViewportControlBase Viewport
    {
        get;
    }

    public Window Window
    {
        get;
    }

    public List<string> Errors { get; } = [];

    public Task Ready => _ready.Task.WaitAsync(TimeSpan.FromSeconds(20));

    public ViewportHost(ViewportControlBase viewport)
    {
        Viewport = viewport;
        Window = new()
        {
            Content = viewport,
            Width = 600,
            Height = 400,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize
        };
        viewport.RendererStatusChanged += status =>
        {
            if (status.StartsWith("렌더 오류:", StringComparison.Ordinal))
            {
                Errors.Add(status);
                _ready.TrySetException(new InvalidOperationException(status));
            }
            else
            {
                _ready.TrySetResult();
            }
        };
        viewport.Tiles.Failed += message => viewport.Dispatcher.BeginInvoke(() => Errors.Add(message));
        Window.Show();
        Window.UpdateLayout();
    }

    public async Task<Capture> CaptureAsync(RenderScene scene)
    {
        var path = Path.Combine(Path.GetTempPath(), $"skia-scene-{Guid.NewGuid():N}.png");
        try
        {
            Viewport.Submit(scene with
            {
                Width = Viewport.ActualWidth,
                Height = Viewport.ActualHeight,
                Dpi = Viewport.Dpi
            });
            Viewport.Capture(path);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(25))
            {
                Assert.Empty(Errors);
                await Task.Delay(20);
                try
                {
                    using var stream = File.OpenRead(path);
                    var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
                }
                catch (IOException)
                {
                    // 파일 준비/PNG 저장 완료를 기다린다.
                }
            }
            throw new TimeoutException("Skia Canvas 캡처 시간 초과");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        Window.Close();
        Viewport.Dispose();
    }
}
