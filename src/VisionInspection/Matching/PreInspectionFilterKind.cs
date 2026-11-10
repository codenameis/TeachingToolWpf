namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Filter kind identifier for the pre-inspection preprocessing filter pipeline.
/// Matches <c>VisionCanvas.Core.Editing.RoiFilterKind</c> integer values 1:1.
/// </summary>
public enum PreInspectionFilterKind
{
    None = 0,
    Binarize = 1,
    Edge = 2,
    Gaussian = 3,
    Erosion = 4,
    Dilation = 5,
    Opening = 6,
    Closing = 7,
    WhiteTopHat = 8,
    BlackHat = 9,
    NativeThreshold = 10,
    GaussianBlackHatThreshold = 11,
    Clahe = 12,
    GuidedFilter = 13,
    Bilateral = 14,
    Median = 15,
    AdaptiveThreshold = 16,
    UnsharpMask = 17,
    SobelGradient = 18,
    WarpAffineShear = 19,
    WarpPerspectiveKeystone = 20,
    WarpPolarUnwrap = 21,
    WarpLensUndistort = 22,
    FftNotch = 23,
    Homomorphic = 24,
    GaborTexture = 25
}
