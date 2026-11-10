// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJPixelProbeResult.cs
namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Immutable result of probing a single pixel coordinate and its 3x3 neighborhood on the canvas.
/// </summary>
public readonly record struct ImageJPixelProbeResult(
    bool IsInsideImage,
    int X,
    int Y,
    byte GrayValue,
    float Mean3x3,
    byte Min3x3,
    byte Max3x3,
    float SobelGradient)
{
    public static ImageJPixelProbeResult OutOfBounds(int x, int y) => new(
        IsInsideImage: false,
        X: x,
        Y: y,
        GrayValue: 0,
        Mean3x3: 0.0f,
        Min3x3: 0,
        Max3x3: 0,
        SobelGradient: 0.0f);
}
