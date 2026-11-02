using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeachingToolWpf.Controls;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Diagnostics;

internal static class WindowCapture
{
    public static void Save(Window window, Dx12Viewport viewport, string viewportPng, string output)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var visual = new DrawingVisual();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(viewportPng));
        image.EndInit();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(window), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            var origin = viewport.TransformToAncestor(window).Transform(new Point());
            context.DrawImage(image, new Rect(origin, new Size(viewport.ActualWidth, viewport.ActualHeight)));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output);
        encoder.Save(stream);
    }
}
