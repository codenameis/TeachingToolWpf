using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering.Abstractions;

/// <summary>장면 배치와 캐시가 사용하는 그래픽스 API 비의존 텍스처 계약.</summary>
public interface IRenderTexture : IDisposable
{
    PixelKind Kind { get; }

    int Width { get; }

    int Height { get; }

    bool IsOpaque { get; }

    bool HasPixels { get; }

    long Bytes { get; }

    long AllocatedBytes { get; }
}
