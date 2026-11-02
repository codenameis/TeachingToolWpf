using VisionCanvas.Rendering;
using VisionImaging.Abstractions;
using WebGPU;
using static WebGPU.WebGPU;

namespace VisionCanvas.Wgpu.Rendering;

internal sealed unsafe class WgpuTexture : IRenderTexture
{
    public WGPUTexture Handle { get; private set; }

    public WGPUTextureView View { get; private set; }

    public PixelKind Kind { get; }

    public int Width { get; }

    public int Height { get; }

    public bool IsOpaque
    {
        get; set;
    }

    public bool HasPixels
    {
        get; set;
    }

    public long Bytes => checked((long)Width * Height * Kind switch
    {
        PixelKind.Gray8 => 1,
        PixelKind.Gray16 => 2,
        _ => 4
    });

    // WebGPU는 실제 할당 크기를 노출하지 않는다. 행 정렬 추정치이며 캐시 퇴출은 Bytes로 계산한다.
    public long AllocatedBytes => checked((((Bytes / Height) + 255) & ~255L) * Height);

    public WgpuTexture(WgpuDevice gpu, int width, int height, PixelKind kind)
    {
        if (width <= 0 || height <= 0 || width > gpu.Limits.maxTextureDimension2D || height > gpu.Limits.maxTextureDimension2D)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        Width = width;
        Height = height;
        Kind = kind;
        var format = kind switch
        {
            PixelKind.Gray8 => WGPUTextureFormat.R8Unorm,
            PixelKind.Gray16 => (WGPUTextureFormat)WGPUNativeTextureFormat.R16Unorm,
            PixelKind.Rgba32 => WGPUTextureFormat.RGBA8Unorm,
            PixelKind.Bgra32 => WGPUTextureFormat.BGRA8Unorm,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var descriptor = new WGPUTextureDescriptor
        {
            dimension = WGPUTextureDimension._2D,
            size = new() { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
            format = format,
            mipLevelCount = 1,
            sampleCount = 1,
            usage = WGPUTextureUsage.CopyDst | WGPUTextureUsage.TextureBinding
        };
        try
        {
            Handle = wgpuDeviceCreateTexture(gpu.Handle, &descriptor);
            gpu.CheckErrors();
            View = wgpuTextureCreateView(Handle, null);
            gpu.CheckErrors();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (View.IsNotNull)
        {
            wgpuTextureViewRelease(View);
            View = default;
        }
        if (Handle.IsNotNull)
        {
            wgpuTextureRelease(Handle);
            Handle = default;
        }
    }
}
