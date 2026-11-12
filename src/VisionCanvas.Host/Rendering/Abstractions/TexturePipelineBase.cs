using System.Runtime.CompilerServices;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>백엔드 구체 텍스처·표면 타입 검증을 한 곳에서 수행해 산재한 다운캐스트를 제거하는 기반 클래스.</summary>
public abstract class TexturePipelineBase<TTexture, TSurface> : ITexturePipeline
    where TTexture : class, IRenderTexture
    where TSurface : class, IRenderSurface
{
    public abstract TTexture CreateTypedTexture(int width, int height, PixelKind kind);

    public abstract TTexture UploadTyped(PixelBuffer pixels, TSurface surface);

    public abstract void UpdateTyped(TTexture texture, PixelBuffer pixels, TSurface surface);

    public IRenderTexture CreateTexture(int width, int height, PixelKind kind)
        => CreateTypedTexture(width, height, kind);

    public IRenderTexture Upload(PixelBuffer pixels, IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        return UploadTyped(pixels, RequireSurface(surface));
    }

    public void Update(IRenderTexture texture, PixelBuffer pixels, IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        UpdateTyped(RequireTexture(texture), pixels, RequireSurface(surface));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static TTexture RequireTexture(IRenderTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture is TTexture typed)
        {
            return typed;
        }

        throw new ArgumentException($"기대한 텍스처 타입({typeof(TTexture).Name})과 다릅니다.", nameof(texture));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static TSurface RequireSurface(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface is TSurface typed)
        {
            return typed;
        }

        throw new ArgumentException($"기대한 렌더 표면 타입({typeof(TSurface).Name})과 다릅니다.", nameof(surface));
    }
}
