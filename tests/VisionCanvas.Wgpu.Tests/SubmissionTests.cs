using System.Buffers.Binary;
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

public sealed class SubmissionTests
{
    [Fact]
    public Task SixQueuedFramesKeepTheirOwnGray16PixelsAcrossSlotReuse() => Sta.RunAsync(() =>
    {
        var window = new Window { Width = 96, Height = 96, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var path = Path.Combine(Path.GetTempPath(), $"wgpu-queued-{Guid.NewGuid():N}.png");
        try
        {
            window.Show();
            using var device = new WgpuDevice(new WindowInteropHelper(window).Handle);
            using var surface = new WgpuSurface(device, 96, 96);
            using var first = new WgpuFrameResources(device);
            using var second = new WgpuFrameResources(device);
            WgpuFrameResources[] frames = [first, second];
            using var sprites = new WgpuSpritePipeline(device, surface, frames);
            using var texture = sprites.CreateTexture(17, 9, PixelKind.Gray16);
            using var pixels = new PixelBuffer(17, 9, PixelKind.Gray16);
            var captures = new List<WgpuReadback>();
            try
            {
                for (var frame = 0; frame < 6; frame++)
                {
                    Assert.True(surface.Begin(96, 96));
                    frames[surface.FrameIndex].Reset();
                    for (var index = 0; index < pixels.Length / 2; index++)
                    {
                        var value = Value(frame, index);
                        BinaryPrimitives.WriteUInt16LittleEndian(pixels.Span.Slice(index * 2, 2), value);
                    }
                    sprites.Update(texture, pixels, surface);
                    var mesh = new SpriteMesh();
                    mesh.Begin(96, 96);
                    mesh.Rectangle(texture, new(0, 0, 96, 96), Vector4.One, true);
                    surface.BeginDrawing();
                    sprites.Draw(mesh, surface, -1, () => { });
                    surface.EndDrawing();
                    captures.Add(new(device, surface));
                    surface.Present();
                }
                // 모든 제출 뒤에 읽는다. 매 프레임 WaitIdle/Save로 슬롯 재사용을 직렬화하지 않는다.
                device.WaitIdle();
                for (var frame = 0; frame < captures.Count; frame++)
                {
                    captures[frame].Save(path);
                    using var stream = File.OpenRead(path);
                    var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var result = new byte[96 * 96 * 4];
                    bitmap.CopyPixels(result, 96 * 4, 0);
                    for (var row = 0; row < 9; row++)
                    {
                        for (var column = 0; column < 17; column++)
                        {
                            var x = (int)((column + 0.5) * 96 / 17);
                            var y = (int)((row + 0.5) * 96 / 9);
                            var expected = (byte)Math.Round(Value(frame, (row * 17) + column) / 257d);
                            var offset = ((y * 96) + x) * 4;
                            if (row == 0 && column == 0)
                            {
                                Assert.Equal(expected, result[offset]);
                            }
                            else
                            {
                                // UNORM16→float→UNORM8 표시 양자화는 기존 픽셀 시험과 같이 ±1을 허용한다.
                                Assert.InRange(result[offset], expected - 1, expected + 1);
                            }
                            Assert.Equal(result[offset], result[offset + 1]);
                            Assert.Equal(result[offset], result[offset + 2]);
                        }
                    }
                }
                device.CheckErrors();
            }
            finally
            {
                device.WaitIdle();
                foreach (var capture in captures)
                {
                    capture.Dispose();
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

    // 첫 픽셀은 상위 8비트 절단과 정규화 반올림을 구분한다.
    private static ushort Value(int frame, int index) => index == 0
        ? (ushort)(255 + (frame * 257))
        : (ushort)(((frame * 997) + (index * 431)) % 65536);
}
