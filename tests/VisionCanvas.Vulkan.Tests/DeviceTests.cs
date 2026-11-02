using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionCanvas.Rendering;
using VisionCanvas.Vulkan.Rendering;
using VisionImaging.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionCanvas.Vulkan.Tests;

public sealed class DeviceTests
{
    [Theory]
    [InlineData(PixelKind.Gray8)]
    [InlineData(PixelKind.Gray16)]
    [InlineData(PixelKind.Rgba32)]
    [InlineData(PixelKind.Bgra32)]
    public Task TexturePixelsKeepFormatAndTopLeftOrientation(PixelKind kind) => Sta.RunAsync(() =>
    {
        var window = new Window { Width = 96, Height = 96, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var path = Path.Combine(Path.GetTempPath(), $"vulkan-pixels-{Guid.NewGuid():N}.png");
        try
        {
            window.Show();
            using var device = new VulkanDevice(new WindowInteropHelper(window).Handle);
            using var surface = new VulkanSurface(device, 96, 96);
            using var first = new VulkanFrameResources(device);
            using var second = new VulkanFrameResources(device);
            using var sprites = new VulkanSpritePipeline(device, surface, [first, second]);
            using var roi = new VulkanPipeline(device, surface, "Roi.hlsl", false);
            using var masks = new VulkanPipeline(device, surface, "Mask.hlsl", false);
            Assert.True(surface.Begin(surface.Width, surface.Height));
            using var pixels = new PixelBuffer(2, 2, kind);
            byte[] levels = [32, 96, 160, 224];
            for (var index = 0; index < 4; index++)
            {
                var channels = pixels.Length / 4;
                pixels.Span.Slice(index * channels, channels).Fill(levels[index]);
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
            using var readback = new VulkanReadback(device, surface);
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
                    Assert.InRange(result[offset], levels[index] - 1, levels[index] + 1);
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

    [Fact]
    public Task HardwareCreatesPresentsAndReleasesSwapchain() => Sta.RunAsync(async () =>
    {
        var window = new Window { Width = 96, Height = 96, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            using var device = new VulkanDevice(new WindowInteropHelper(window).Handle);
            Assert.NotEqual(Vortice.Vulkan.VkPhysicalDeviceType.Cpu, device.Properties.deviceType);
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            using var surface = new VulkanSurface(device, 96, 96);
            for (var frame = 0; frame < 4; frame++)
            {
                Assert.True(surface.Begin(96, 96));
                surface.BeginDrawing();
                surface.EndDrawing();
                surface.Present();
            }
            device.WaitIdle();
            await Task.Yield();
        }
        finally
        {
            window.Close();
        }
    });
}

internal static class Sta
{
    public static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = RunAsync();
            Dispatcher.Run();

            async Task RunAsync()
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WaitAndJoinAsync(completion.Task, thread);

        static async Task WaitAndJoinAsync(Task task, Thread thread)
        {
            await task.WaitAsync(TimeSpan.FromSeconds(60));
            thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
