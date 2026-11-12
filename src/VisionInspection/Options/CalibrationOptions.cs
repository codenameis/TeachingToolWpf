namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 7번 9-Point 평면 호모그래피 및 Brown-Conrady 렌즈 왜곡 교정 전용 옵션 및 범위 상수.
/// </summary>
public sealed record CalibrationOptions(
    float GridPitchMm = CalibrationOptions.DefaultGridPitchMm,
    float LensK1 = CalibrationOptions.DefaultLensK1)
{
    public const float DefaultGridPitchMm = 10.0f;
    public const float MinGridPitchMm = 0.5f;
    public const float MaxGridPitchMm = 500.0f;

    public const float DefaultLensK1 = -0.05f;
    public const float MinLensK1 = -0.5f;
    public const float MaxLensK1 = 0.5f;

    public CalibrationOptions Validate() => new(
        GridPitchMm: Math.Clamp(GridPitchMm, MinGridPitchMm, MaxGridPitchMm),
        LensK1: Math.Clamp(LensK1, MinLensK1, MaxLensK1));
}
