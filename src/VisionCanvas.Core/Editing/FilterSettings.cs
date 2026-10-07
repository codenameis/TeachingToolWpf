namespace VisionCanvas.Core.Editing;

public enum RoiFilterKind
{
    None, Binarize, Edge,
    Gaussian, Erosion, Dilation, Opening, Closing, WhiteTopHat, BlackHat,
    NativeThreshold, GaussianBlackHatThreshold
}

public enum RoiFilterExecution
{
    CpuOnly, CpuGpu
}

/// <summary>ROI recipe values only; native image processing stays outside Core.</summary>
public sealed record FilterSettings(RoiFilterKind Kind, double Threshold = 128, double EdgeLow = 50, double EdgeHigh = 150,
    int Radius = 3, double Sigma = 1, RoiFilterExecution Execution = RoiFilterExecution.CpuOnly)
{
    public bool IsIndustrial => Kind >= RoiFilterKind.Gaussian;
    public bool UsesRadius => IsIndustrial && Kind != RoiFilterKind.NativeThreshold;
    public bool UsesSigma => Kind is RoiFilterKind.Gaussian or RoiFilterKind.GaussianBlackHatThreshold;
    public bool UsesNativeThreshold => Kind is RoiFilterKind.NativeThreshold or RoiFilterKind.GaussianBlackHatThreshold;

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Execution) ||
            (UsesRadius && Radius is < 1 or > 31) ||
            (UsesSigma && (!double.IsFinite(Sigma) || Sigma <= 0 || Sigma > 100)) ||
            (UsesNativeThreshold && (!double.IsFinite(Threshold) || Threshold is < 0 or > 65535)) ||
            (Kind == RoiFilterKind.Binarize && (!double.IsFinite(Threshold) || Threshold is < 0 or > 255)) ||
            (Kind == RoiFilterKind.Edge && (!double.IsFinite(EdgeLow) || !double.IsFinite(EdgeHigh) || EdgeLow < 0 || EdgeHigh > 255 || EdgeLow >= EdgeHigh)))
        {
            throw new ArgumentException("임계값: 기존 0–255 / 원본 0–65535, Edge 하한 < 상한, 반경 정수 1–31, sigma 0 초과–100 이하가 필요합니다.");
        }
    }
}
