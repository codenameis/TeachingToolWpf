namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 13번 2D 다항식 조명 평탄화·비등방성 확산·형태학적 재구성 전용 옵션 및 범위 상수.
/// </summary>
public sealed record ShadingReconOptions(
    int TargetBaseGray = ShadingReconOptions.DefaultTargetBaseGray,
    float AnisotropicDiffusionKappa = ShadingReconOptions.DefaultAnisotropicDiffusionKappa,
    int ReconstructionMarkerRadius = ShadingReconOptions.DefaultReconstructionMarkerRadius,
    int MinimumBlobAreaPixels = ShadingReconOptions.DefaultMinimumBlobAreaPixels)
{
    public const int DefaultTargetBaseGray = 128;
    public const int MinTargetBaseGray = 60;
    public const int MaxTargetBaseGray = 220;
    public const int UiMinTargetBaseGray = 16;
    public const int UiMaxTargetBaseGray = 240;

    public const float DefaultAnisotropicDiffusionKappa = 18.0f;
    public const float MinDiffusionKappa = 4.0f;
    public const float MaxDiffusionKappa = 80.0f;
    public const float UiMinDiffusionKappa = 2.0f;

    public const int DefaultReconstructionMarkerRadius = 2;
    public const int MinMarkerRadius = 1;
    public const int MaxMarkerRadius = 16;
    public const int UiMaxMarkerRadius = 10;

    public const int DefaultMinimumBlobAreaPixels = 8;
    public const int MinPostBlobAreaPixels = 4;

    public ShadingReconOptions Validate() => new(
        TargetBaseGray: Math.Clamp(TargetBaseGray, MinTargetBaseGray, MaxTargetBaseGray),
        AnisotropicDiffusionKappa: Math.Clamp(AnisotropicDiffusionKappa, MinDiffusionKappa, MaxDiffusionKappa),
        ReconstructionMarkerRadius: Math.Clamp(ReconstructionMarkerRadius, MinMarkerRadius, MaxMarkerRadius),
        MinimumBlobAreaPixels: Math.Max(MinPostBlobAreaPixels, MinimumBlobAreaPixels));
}
