// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJFilterKind.cs
namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Spatial, morphological, and background-correction operations available in the ImageJ Process menu.
/// </summary>
public enum ImageJFilterKind : byte
{
    None = 0,
    GaussianBlur3x3 = 1,
    GaussianBlur5x5 = 2,
    Median3x3 = 3,
    MeanSmooth3x3 = 4,
    UnsharpSharpen3x3 = 5,
    SobelEdgeMagnitude = 6,
    VarianceTexture3x3 = 7,
    MorphErode3x3 = 8,
    MorphDilate3x3 = 9,
    MorphOpen3x3 = 10,
    MorphClose3x3 = 11,
    MorphGradient3x3 = 12,
    RollingBallTopHat = 13,
    BlackBottomHat = 14,
    Invert = 15,
    EnhanceContrastStretch = 16,
    HistogramEqualize = 17
}
