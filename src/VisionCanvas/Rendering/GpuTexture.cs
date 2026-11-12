using VisionCanvas.Rendering.Abstractions;
using VisionImaging.Abstractions;
using Vortice.Direct3D12;

namespace VisionCanvas.Rendering;

internal sealed class GpuTexture(ID3D12Resource resource, int slot, PixelKind kind, int width, int height, Action<int> releaseSlot) : IRenderTexture
{
    public ID3D12Resource Resource { get; } = resource;

    public int Slot { get; } = slot;

    public PixelKind Kind { get; } = kind;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public bool HasPixels { get; set; }

    public bool IsOpaque { get; set; }

    public long Bytes => (long)Width * Height * Kind switch
    {
        PixelKind.Gray8 => 1,
        PixelKind.Gray16 => 2,
        _ => 4
    };

    public long AllocatedBytes { get; init; }

    public void Dispose()
    {
        Resource.Dispose();
        releaseSlot(Slot);
    }
}
