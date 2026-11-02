using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using VisionCanvas.Wgpu.Rendering;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionCanvas.Wgpu.Tests;

public sealed class DeviceTests
{
    [Fact]
    public Task CreatesHardwareDeviceAndReleasesItTwice() => Sta.RunAsync(() =>
    {
        var window = new Window { Width = 96, Height = 96, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show();
            using var device = new WgpuDevice(new WindowInteropHelper(window).Handle);
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.True(device.SupportsGray16);
            var version = WebGPU.WebGPU.wgpuGetVersion();
            File.WriteAllText(SampleTests.ArtifactPath("device.json"), JsonSerializer.Serialize(new
            {
                device.Name,
                device.Driver,
                NativeVersion = $"{version >> 24}.{(version >> 16) & 255}.{(version >> 8) & 255}.{version & 255}",
                Backend = "DX12",
                device.SupportsGray16,
                MaxTextureDimension2D = device.Limits.maxTextureDimension2D,
                LogicalTileEvictionTargetMiB = 256
            }, new JsonSerializerOptions { WriteIndented = true }));
            using (var surface = new WgpuSurface(device, 96, 96))
            {
                for (var index = 0; index < 4; index++)
                {
                    Assert.True(surface.Begin(96, 96));
                    surface.BeginDrawing();
                    surface.EndDrawing();
                    surface.Present();
                }
            }
            device.WaitIdle();
            device.CheckErrors();
            device.Dispose();
            device.Dispose();
        }
        finally
        {
            window.Close();
        }
        return Task.CompletedTask;
    });
}
