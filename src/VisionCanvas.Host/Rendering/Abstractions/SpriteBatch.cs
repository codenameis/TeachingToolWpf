namespace VisionCanvas.Rendering.Abstractions;

/// <summary>동일 텍스처 및 샘플링 모드로 묶인 연속 정점 드로우 배치.</summary>
public readonly record struct SpriteBatch(IRenderTexture Texture, int Start, int Count, bool PointSampling);
