using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using VisionCanvas.Wgpu.Rendering;
using VisionImaging.Abstractions;

namespace VisionCanvas.Wgpu.Tests;

public sealed class PixelTests
{
    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public Task TexturePixelsKeepFormatAndTopLeftOrientation(PixelKind kind) => Sta.RunAsync(() =>
    {
        var window = new Window { Width = 96, Height = 96, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var path = Path.Combine(Path.GetTempPath(), $"wgpu-pixels-{Guid.NewGuid():N}.png");
        try
        {
            window.Show();
            using var device = new WgpuDevice(new WindowInteropHelper(window).Handle);
            using var surface = new WgpuSurface(device, 96, 96);
            using var first = new WgpuFrameResources(device);
            using var second = new WgpuFrameResources(device);
            using var sprites = new WgpuSpritePipeline(device, surface, [first, second]);
            using var roi = new WgpuPipeline(device, surface, "Roi.wgsl", false);
            using var masks = new WgpuPipeline(device, surface, "Mask.wgsl", false);
            Assert.True(surface.Begin(surface.Width, surface.Height));
            using var pixels = new PixelBuffer(2, 2, kind);
            byte[] levels = [32, 96, 160, 224];
            for (var index = 0; index < 4; index++)
            {
                var channels = pixels.Length / 4;
                pixels.Span.Slice(index * channels, channels).Fill(levels[index]);
                if (kind == PixelKind.Gray16)
                {
                    // 서로 다른 상·하위 바이트로 잘못된 Gray8 업로드도 구분한다.
                    pixels.Span[index * channels] = (byte)(255 - levels[index]);
                }
                if (channels == 4)
                {
                    var red = kind == PixelKind.Rgba32 ? 0 : 2;
                    var blue = kind == PixelKind.Rgba32 ? 2 : 0;
                    pixels.Span[(index * channels) + red] = levels[index];
                    pixels.Span[(index * channels) + 1] = 90;
                    pixels.Span[(index * channels) + blue] = (byte)(255 - levels[index]);
                    pixels.Span[(index * channels) + 3] = 255;
                }
            }
            using var texture = sprites.Upload(pixels, surface);
            var mesh = new SpriteMesh();
            mesh.Begin(surface.Width, surface.Height);
            mesh.Rectangle(texture, new(0, 0, surface.Width, surface.Height), Vector4.One, true);
            surface.BeginDrawing();
            sprites.Draw(mesh, surface, -1, () => { });
            surface.EndDrawing();
            using var readback = new WgpuReadback(device, surface);
            surface.Present();
            device.WaitIdle();
            readback.Save(path);
            using var stream = File.OpenRead(path);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var result = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(result, bitmap.PixelWidth * 4, 0);
            for (var index = 0; index < 4; index++)
            {
                var x = bitmap.PixelWidth * ((index % 2 * 2) + 1) / 4;
                var y = bitmap.PixelHeight * ((index / 2 * 2) + 1) / 4;
                var offset = ((y * bitmap.PixelWidth) + x) * 4;
                if (kind is PixelKind.Rgba32 or PixelKind.Bgra32)
                {
                    Assert.InRange(result[offset], 254 - levels[index], 256 - levels[index]);
                    Assert.InRange(result[offset + 1], 89, 91);
                    Assert.InRange(result[offset + 2], levels[index] - 1, levels[index] + 1);
                }
                else
                {
                    var expected = kind == PixelKind.Gray16
                        ? (int)Math.Round(((levels[index] * 256) + 255 - levels[index]) / 257d)
                        : levels[index];
                    Assert.InRange(result[offset], expected - 1, expected + 1);
                    Assert.Equal(result[offset], result[offset + 1]);
                    Assert.Equal(result[offset], result[offset + 2]);
                }
            }
        }
        finally
        {
            window.Close();
            File.Delete(path);
        }
        return Task.CompletedTask;
    });

}

