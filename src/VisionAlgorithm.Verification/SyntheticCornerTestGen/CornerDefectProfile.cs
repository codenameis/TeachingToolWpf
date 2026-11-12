// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/SyntheticCornerTestGen/CornerDefectProfile.cs
namespace VisionAlgorithm.OpenSrc.CornerAlignment.SyntheticCornerTestGen;

/// <summary>
/// 절차적 합성 코너 테스트 영상에 주입할 4대 물리적 코너/에지 결함 및 광학·센서 노이즈 프로파일.
/// </summary>
public readonly record struct CornerDefectProfile(
    float ChamferLength,
    float CornerRadius,
    float ChippingDepth,
    float BurrHeight,
    float NoiseSigma,
    float ChippingLength = CornerDefectProfile.DefaultChippingLength,
    float BurrLength = CornerDefectProfile.DefaultBurrLength,
    float OpticalPsfSigma = CornerDefectProfile.DefaultOpticalPsfSigma)
{
    public const float DefaultChippingLength = 12.0f;
    public const float DefaultBurrLength = 10.0f;
    public const float DefaultOpticalPsfSigma = 1.05f;

    /// <summary>
    /// 결함이 없는 이상적인 서브픽셀 코너 프로파일.
    /// </summary>
    public static CornerDefectProfile Ideal => new(
        ChamferLength: 0.0f,
        CornerRadius: 0.0f,
        ChippingDepth: 0.0f,
        BurrHeight: 0.0f,
        NoiseSigma: 0.0f);

    /// <summary>
    /// 명세서 Test 1 기준 가혹 결함 프로파일 (모따기 8px + 라운딩 6px + 측면 칩핑 3.5px + 측면 버 4.0px + 센서 노이즈).
    /// </summary>
    public static CornerDefectProfile SevereIndustrialDefect => new(
        ChamferLength: 8.0f,
        CornerRadius: 6.0f,
        ChippingDepth: 3.5f,
        BurrHeight: 4.0f,
        NoiseSigma: 1.2f,
        ChippingLength: DefaultChippingLength,
        BurrLength: DefaultBurrLength,
        OpticalPsfSigma: DefaultOpticalPsfSigma);
}
