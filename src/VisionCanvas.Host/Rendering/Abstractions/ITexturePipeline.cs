using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>타일·라벨·필터 텍스처의 생성 및 갱신을 담당하는 백엔드 파이프라인 계약.</summary>
public interface ITexturePipeline
{
    IRenderTexture CreateTexture(int width, int height, PixelKind kind);

    IRenderTexture Upload(PixelBuffer pixels, IRenderSurface surface);

    void Update(IRenderTexture texture, PixelBuffer pixels, IRenderSurface surface);
}
