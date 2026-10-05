using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D12;

namespace VisionCanvas.Rendering;

/// <summary>Explicit diagnostics only. Normal presentation never reads pixels back to the CPU.</summary>
internal sealed class FrameReadback : IDisposable
{
    private readonly ID3D12Resource _buffer;
    private readonly PlacedSubresourceFootPrint _footprint;
    private readonly int _width;

    private readonly int _height;

    public unsafe FrameReadback(Dx12Device gpu, Dx12Surface surface)
    {
        _width = surface.Width;
        _height = surface.Height;
        var layout = new PlacedSubresourceFootPrint();
        gpu.Device.GetCopyableFootprints(surface.CurrentBuffer.Description, 0, 1, 0, &layout, null, null, out var bytes);
        _footprint = layout;
        _buffer = gpu.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(bytes), ResourceStates.CopyDest);
        surface.Commands.ResourceBarrierTransition(surface.CurrentBuffer, ResourceStates.RenderTarget, ResourceStates.CopySource);
        surface.Commands.CopyTextureRegion(new TextureCopyLocation(_buffer, layout), 0, 0, 0, new TextureCopyLocation(surface.CurrentBuffer, 0));
        surface.Commands.ResourceBarrierTransition(surface.CurrentBuffer, ResourceStates.CopySource, ResourceStates.RenderTarget);
    }

    public unsafe void Save(string path)
    {
        var pixels = new byte[checked(_width * _height * 4)];
        var source = _buffer.Map<byte>(0);
        for (var row = 0;
row < _height;
row++)
        {
            new ReadOnlySpan<byte>(source + (row * _footprint.Footprint.RowPitch), _width * 4).CopyTo(pixels.AsSpan(row * _width * 4));
        }

        _buffer.Unmap(0);
        var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, pixels, _width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose() => _buffer.Dispose();
}
