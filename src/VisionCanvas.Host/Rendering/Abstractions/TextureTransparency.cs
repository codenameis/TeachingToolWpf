using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>정적 텍스처 업로드 시 픽셀 버퍼의 불투명 여부를 판정하는 공통 보조.</summary>
public static class TextureTransparency
{
    public static bool PixelsAreOpaque(PixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
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
