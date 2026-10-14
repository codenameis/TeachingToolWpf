using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class CrosshairSmokeScenario
{
    internal static async Task Run(Window window, MainWindowViewModel model, Dx12Viewport viewport)
    {
        var settings = model.GuideSettings;
        var originalView = model.Session.State.Viewport;
        var originalWidth = window.Width;
        var originalVisible = settings.IsVisible;
        var originalColor = settings.Color;
        var originalThickness = settings.Thickness;
        var before = model.Session.State;
        var historyBytes = model.Session.HistoryBytes;
        var button = Descendants(window).OfType<Button>().Single(item => AutomationProperties.GetName(item) == "크로스헤어 표시 전환");
        var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        try
        {
            settings.IsVisible = false;
            settings.Color = "#FFEA00";
            settings.Thickness = "3";
            settings.Apply.Execute(null);
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(settings.IsVisible && (button.Content as string) == "크로스헤어 끄기", "button shows the guide and updates its label");
            await Capture("image-center", true);
            model.Session.SetViewport(originalView.ZoomAt(new(80, 60), 1.2).Pan(new(25, -15)));
            await Capture("pan-zoom", true);
            window.Width += 100;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Capture("resize", true);
            await viewport.RestartAsync();
            await Capture("restart", true);
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Require(!settings.IsVisible, "button hides the guide");
            await Capture("hidden", false);
            Require(ReferenceEquals(before.Rois, model.Session.State.Rois) && historyBytes == model.Session.HistoryBytes,
                "guide controls do not alter ROI geometry or history");
        }
        finally
        {
            settings.Color = originalColor;
            settings.Thickness = originalThickness;
            settings.Apply.Execute(null);
            settings.IsVisible = originalVisible;
            window.Width = originalWidth;
            model.Session.SetViewport(originalView);
        }

        async Task Capture(string name, bool visible)
        {
            var path = Path.GetFullPath($"artifacts/crosshair-{name}.png");
            viewport.Capture(path);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(50);
                try
                {
                    using var stream = File.OpenRead(path);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    var image = model.Session.State.Image ?? throw new InvalidOperationException("missing image");
                    var center = model.Session.State.Viewport.ImageToScreen(new(image.Width / 2d, image.Height / 2d));
                    var x = (int)Math.Round(center.X * viewport.Dpi);
                    var y = (int)Math.Round(center.Y * viewport.Dpi);
                    foreach (var point in new PointD[] { new(24, y), new(bitmap.PixelWidth - 24, y), new(x, 24), new(x, bitmap.PixelHeight - 24) })
                    {
                        Require(point.X >= 0 && point.X < bitmap.PixelWidth && point.Y >= 0 && point.Y < bitmap.PixelHeight,
                            "probe stays inside the visible image axes");
                        var found = false;
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dx = -1; dx <= 1; dx++)
                            {
                                var offset = ((((int)point.Y + dy) * bitmap.PixelWidth) + (int)point.X + dx) * 4;
                                found |= pixels[offset] < 8 && Math.Abs(pixels[offset + 1] - 234) < 8 && pixels[offset + 2] > 247;
                            }
                        }
                        Require(found == visible, name + ": full-canvas image-centered line pixels/visibility");
                    }
                    return;
                }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30))
                {
                    // 비동기 캡처 파일 저장만 기다린다. 픽셀 판정 실패는 재시도하지 않는다.
                }
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Crosshair smoke: " + message);
        }
    }
}
