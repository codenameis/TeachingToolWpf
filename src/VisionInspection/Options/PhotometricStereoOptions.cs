namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 19번 4방향 포토메트릭 스테레오 법선·알베도·3D 곡률 결함 검사 전용 옵션 및 범위 상수.
/// </summary>
public sealed record PhotometricStereoOptions(
    float SlantAngleDeg = PhotometricStereoOptions.DefaultSlantAngleDeg,
    float CurvatureThreshold = PhotometricStereoOptions.DefaultCurvatureThreshold,
    int MaxDefectPixels = PhotometricStereoOptions.DefaultMaxDefectPixels)
{
    public const float DefaultSlantAngleDeg = 45.0f;
    public const float MinSlantAngleDeg = 15.0f;
    public const float MaxSlantAngleDeg = 75.0f;

    public const float DefaultCurvatureThreshold = 0.08f;
    public const double MinCurvatureThreshold = 0.02;
    public const double MaxCurvatureThreshold = 0.35;
    public const float UiMinCurvatureThreshold = 0.01f;
    public const float UiMaxCurvatureThreshold = 0.45f;

    public const int DefaultMaxDefectPixels = 6;
    public const int MinMaxDefectPixels = 1;
    public const int MaxMaxDefectPixels = 500;

    public PhotometricStereoOptions Validate() => new(
        SlantAngleDeg: Math.Clamp(SlantAngleDeg, MinSlantAngleDeg, MaxSlantAngleDeg),
        CurvatureThreshold: (float)Math.Clamp((double)CurvatureThreshold, MinCurvatureThreshold, MaxCurvatureThreshold),
        MaxDefectPixels: Math.Clamp(MaxDefectPixels, MinMaxDefectPixels, MaxMaxDefectPixels));
}
