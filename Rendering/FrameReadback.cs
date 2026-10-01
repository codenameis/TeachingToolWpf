using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D12;

namespace TeachingToolWpf.Rendering;

/// <summary>Explicit diagnostics only. Normal presentation never reads pixels back to the CPU.</summary>
internal sealed class FrameReadback : IDisposable
{
    private readonly ID3D12Resource buffer;
    private readonly PlacedSubresourceFootPrint footprint;
    private readonly int width, height;
    public unsafe FrameReadback(Dx12Device gpu, Dx12Surface surface)
    {
        width = surface.Width; height = surface.Height;
        var layout = new PlacedSubresourceFootPrint();
        gpu.Device.GetCopyableFootprints(surface.CurrentBuffer.Description, 0, 1, 0, &layout, null, null, out var bytes);
        footprint = layout;
        buffer = gpu.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(bytes), ResourceStates.CopyDest);
        surface.Commands.ResourceBarrierTransition(surface.CurrentBuffer, ResourceStates.RenderTarget, ResourceStates.CopySource);
        surface.Commands.CopyTextureRegion(new TextureCopyLocation(buffer, layout), 0, 0, 0, new TextureCopyLocation(surface.CurrentBuffer, 0));
        surface.Commands.ResourceBarrierTransition(surface.CurrentBuffer, ResourceStates.CopySource, ResourceStates.RenderTarget);
    }
    public unsafe void Save(string path)
    {
        var pixels = new byte[checked(width * height * 4)];
        var source = buffer.Map<byte>(0);
        for (var row = 0; row < height; row++) new ReadOnlySpan<byte>(source + row * footprint.Footprint.RowPitch, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
        buffer.Unmap(0);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
    public void Dispose() => buffer.Dispose();
}
