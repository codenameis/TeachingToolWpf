namespace VisionCanvas.Core.Editing;

public enum RoiFilterKind
{
    None, Binarize, Edge,
    Gaussian, Erosion, Dilation, Opening, Closing, WhiteTopHat, BlackHat,
    NativeThreshold, GaussianBlackHatThreshold,
    Clahe, GuidedFilter, Bilateral, Median, AdaptiveThreshold, UnsharpMask, SobelGradient,
    WarpAffineShear, WarpPerspectiveKeystone, WarpPolarUnwrap, WarpLensUndistort,
    FftNotch, Homomorphic, GaborTexture
}
