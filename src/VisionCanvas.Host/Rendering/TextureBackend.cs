using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

/// <summary>장면 배치와 캐시가 사용하는 GPU 비의존 텍스처 계약.</summary>
internal interface IRenderTexture : IDisposable
{
    PixelKind Kind
    {
        get;
    }
    int Width
    {
        get;
    }
    int Height
    {
        get;
    }
    bool IsOpaque
    {
        get;
    }
    bool HasPixels
    {
        get;
    }
    long Bytes
    {
        get;
    }
    long AllocatedBytes
    {
        get;
    }
}

internal interface IRenderSurface
{
    int Width
    {
        get;
    }

    int Height
    {
        get;
    }

    int FrameIndex
    {
        get;
    }

    void Retire(IDisposable resource);
}

internal interface ITexturePipeline
{
    IRenderTexture CreateTexture(int width, int height, PixelKind kind);
    IRenderTexture Upload(PixelBuffer pixels, IRenderSurface surface);
    void Update(IRenderTexture texture, PixelBuffer pixels, IRenderSurface surface);
}

internal static class TextureTransparency
{
    public static bool PixelsAreOpaque(PixelBuffer pixels)
    {
        if (pixels.Kind is PixelKind.Gray8 or PixelKind.Gray16)
        {
            return true;
        }
        var bytes = pixels.Span;
        for (var alpha = 3; alpha < bytes.Length; alpha += 4)
        {
            if (bytes[alpha] != byte.MaxValue)
            {
                return false;
            }
        }
        return true;
    }
}
