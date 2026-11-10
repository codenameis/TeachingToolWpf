namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Configuration parameters for 2D FFT Periodic Notch filtering, Homomorphic illumination-reflectance separation,
/// and 2D Gabor directional texture filter banks.
/// </summary>
public readonly record struct FftHomomorphicGaborSpec(
    int FftMinRadius,
    double FftNotchSigma,
    double FftPeakThresholdRatio,
    double HomomorphicGammaLow,
    double HomomorphicGammaHigh,
    double HomomorphicSigma,
    double GaborWavelength,
    double GaborOrientationDeg,
    double GaborSigma,
    double GaborAspectRatio)
{
    public const int DefaultFftMinRadius = 6;
    public const double DefaultFftNotchSigma = 3.5;
    public const double DefaultFftPeakThresholdRatio = 4.5;
    public const double DefaultHomomorphicGammaLow = 0.35;
    public const double DefaultHomomorphicGammaHigh = 1.85;
    public const double DefaultHomomorphicSigma = 18.0;
    public const double DefaultGaborWavelength = 8.0;
    public const double DefaultGaborOrientationDeg = 45.0;
    public const double DefaultGaborSigma = 3.5;
    public const double DefaultGaborAspectRatio = 0.5;

    public static FftHomomorphicGaborSpec Default => new(
        DefaultFftMinRadius,
        DefaultFftNotchSigma,
        DefaultFftPeakThresholdRatio,
        DefaultHomomorphicGammaLow,
        DefaultHomomorphicGammaHigh,
        DefaultHomomorphicSigma,
        DefaultGaborWavelength,
        DefaultGaborOrientationDeg,
        DefaultGaborSigma,
        DefaultGaborAspectRatio);
}
