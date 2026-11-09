// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/MotionCorrectionCommand.cs
namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 기구적 회전 중심(CoR) 레버암 보정 및 UVW 병렬 스테이지 역기구학이 반영된 최종 모션 보정 명령.
/// </summary>
public readonly record struct MotionCorrectionCommand(
    double DeltaX_Mm,
    double DeltaY_Mm,
    double DeltaThetaRad,
    double DeltaU_Mm,
    double DeltaV_Mm,
    double DeltaW_Mm)
{
    private const double RadiansToDegrees = 180.0 / System.Math.PI;

    /// <summary>
    /// 회전 보정량의 각도(Degree) 표현.
    /// </summary>
    public double DeltaThetaDeg => DeltaThetaRad * RadiansToDegrees;
}
