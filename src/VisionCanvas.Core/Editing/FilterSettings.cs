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

public enum RoiFilterExecution
{
    CpuOnly, CpuGpu
}

/// <summary>ROI recipe values only; native image processing stays outside Core.</summary>
public sealed record FilterSettings(RoiFilterKind Kind, double Threshold = 128, double EdgeLow = 50, double EdgeHigh = 150,
    int Radius = 3, double Sigma = 1, RoiFilterExecution Execution = RoiFilterExecution.CpuOnly)
{
    public bool IsEnabled { get; init; } = true;

    public static bool IsIndustrialKind(RoiFilterKind kind) =>
        kind is >= RoiFilterKind.Gaussian and <= RoiFilterKind.GaussianBlackHatThreshold;

    public static bool IsPreprocessingOrWarpKind(RoiFilterKind kind) =>
        kind >= RoiFilterKind.Clahe;

    public static bool UsesRadiusKind(RoiFilterKind kind) =>
        (IsIndustrialKind(kind) && kind != RoiFilterKind.NativeThreshold) ||
        kind is RoiFilterKind.Clahe or RoiFilterKind.GuidedFilter or RoiFilterKind.Bilateral
            or RoiFilterKind.Median or RoiFilterKind.AdaptiveThreshold or RoiFilterKind.UnsharpMask
            or RoiFilterKind.FftNotch or RoiFilterKind.Homomorphic or RoiFilterKind.GaborTexture;

    public static bool UsesSigmaKind(RoiFilterKind kind) =>
        kind is RoiFilterKind.Gaussian or RoiFilterKind.GaussianBlackHatThreshold
            or RoiFilterKind.Clahe or RoiFilterKind.GuidedFilter or RoiFilterKind.Bilateral
            or RoiFilterKind.AdaptiveThreshold or RoiFilterKind.UnsharpMask or RoiFilterKind.SobelGradient
            or RoiFilterKind.WarpAffineShear or RoiFilterKind.WarpPerspectiveKeystone or RoiFilterKind.WarpLensUndistort
            or RoiFilterKind.FftNotch or RoiFilterKind.Homomorphic or RoiFilterKind.GaborTexture;

    public static bool UsesNativeThresholdKind(RoiFilterKind kind) =>
        kind is RoiFilterKind.NativeThreshold or RoiFilterKind.GaussianBlackHatThreshold;

    public static bool UsesByteThresholdKind(RoiFilterKind kind) =>
        kind is RoiFilterKind.Binarize or RoiFilterKind.WarpAffineShear or RoiFilterKind.WarpPerspectiveKeystone
            or RoiFilterKind.WarpPolarUnwrap or RoiFilterKind.WarpLensUndistort
            or RoiFilterKind.FftNotch or RoiFilterKind.Homomorphic or RoiFilterKind.GaborTexture;

    public bool IsIndustrial => IsIndustrialKind(Kind);
    public bool IsPreprocessingOrWarp => IsPreprocessingOrWarpKind(Kind);
    public bool UsesRadius => UsesRadiusKind(Kind);
    public bool UsesSigma => UsesSigmaKind(Kind);
    public bool UsesNativeThreshold => UsesNativeThresholdKind(Kind);
    public bool UsesByteThreshold => UsesByteThresholdKind(Kind);

    public string FormatSummary()
    {
        var prefix = IsEnabled ? string.Empty : "[우회] ";
        return prefix + Kind switch
        {
            RoiFilterKind.None => "없음 · 원본",
            RoiFilterKind.Binarize => $"Binarize (T={Threshold:F0})",
            RoiFilterKind.Edge => $"Canny ({EdgeLow:F0}..{EdgeHigh:F0})",
            RoiFilterKind.FftNotch => $"2D FFT Notch (rMin={Radius}, σ={Sigma:F1}, T={Threshold:F0})",
            RoiFilterKind.Homomorphic => $"Homomorphic (r={Radius}, γH={Sigma:F2}, T={Threshold:F0})",
            RoiFilterKind.GaborTexture => $"2D Gabor (λ={Radius * 2}, σ={Sigma:F1}, θ={Threshold:F0}°)",
            _ => $"{Kind} (r={Radius}, σ={Sigma:F1}, T={Threshold:F0})"
        };
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Execution) ||
            (UsesRadius && Radius is < 1 or > 31) ||
            (UsesSigma && (!double.IsFinite(Sigma) || Sigma <= 0 || Sigma > 100)) ||
            (UsesNativeThreshold && (!double.IsFinite(Threshold) || Threshold is < 0 or > 65535)) ||
            (UsesByteThreshold && (!double.IsFinite(Threshold) || Threshold is < 0 or > 255)) ||
            (Kind == RoiFilterKind.Edge && (!double.IsFinite(EdgeLow) || !double.IsFinite(EdgeHigh) || EdgeLow < 0 || EdgeHigh > 255 || EdgeLow >= EdgeHigh)))
        {
            throw new ArgumentException("임계값: 기존 0–255 / 원본 0–65535, Edge 하한 < 상한, 반경 정수 1–31, sigma 0 초과–100 이하가 필요합니다.");
        }
    }
}
