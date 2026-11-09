// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Kinematics/RigidBodyAlignSolver.cs
using System;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;

/// <summary>
/// 기구적 회전 중심(Center of Rotation, CoR)과 관측된 코너 사이의 레버암(Lever-Arm) 스윙 궤적을
/// 정확히 보상하여 단 1회(1-Shot) 이동으로 마스터 포즈에 일치시키는 강체 정합 솔버.
/// </summary>
public static class RigidBodyAlignSolver
{
    /// <summary>
    /// 마스터 티칭 포즈 <c>(masterX_Mm, masterY_Mm, masterThetaRad)</c>,
    /// 현재 측정된 부품 포즈 <c>(currentX_Mm, currentY_Mm, currentThetaRad)</c>,
    /// 기구 회전 중심 <c>(corX_Mm, corY_Mm)</c>으로부터 레버암 효과가 보상된 1-Shot 정합 이동량
    /// <c>(DeltaX_Mm, DeltaY_Mm, DeltaThetaRad)</c>을 계산한다.
    /// </summary>
    public static (double DeltaX_Mm, double DeltaY_Mm, double DeltaThetaRad) ComputeOneShotAlignment(
        double masterX_Mm,
        double masterY_Mm,
        double masterThetaRad,
        double currentX_Mm,
        double currentY_Mm,
        double currentThetaRad,
        double corX_Mm,
        double corY_Mm)
    {
        double deltaThetaRad = NormalizeAngleMinusPiToPi(masterThetaRad - currentThetaRad);
        double cosT = Math.Cos(deltaThetaRad);
        double sinT = Math.Sin(deltaThetaRad);

        // 회전 중심(CoR)을 기준으로 현재 특징점이 deltaTheta만큼 먼저 회전했을 때 이동하는 위치 (X'_cur, Y'_cur)
        double relX = currentX_Mm - corX_Mm;
        double relY = currentY_Mm - corY_Mm;

        double rotatedCurrentX = corX_Mm + (cosT * relX) - (sinT * relY);
        double rotatedCurrentY = corY_Mm + (sinT * relX) + (cosT * relY);

        // 회전 후 위치에서 마스터 위치까지 필요한 순수 병진 보정량
        double deltaX = masterX_Mm - rotatedCurrentX;
        double deltaY = masterY_Mm - rotatedCurrentY;

        return (deltaX, deltaY, deltaThetaRad);
    }

    /// <summary>
    /// 현재 좌표 <c>(currentX_Mm, currentY_Mm, currentThetaRad)</c>에 대해 회전 중심 <c>(corX_Mm, corY_Mm)</c> 기준 회전
    /// <paramref name="deltaThetaRad"/> 및 병진 이동 <c>(deltaX_Mm, deltaY_Mm)</c>을 실제로 적용한 후의 최종 포즈를 계산한다.
    /// </summary>
    public static (double FinalX_Mm, double FinalY_Mm, double FinalThetaRad) SimulateMotionAroundCor(
        double currentX_Mm,
        double currentY_Mm,
        double currentThetaRad,
        double corX_Mm,
        double corY_Mm,
        double deltaX_Mm,
        double deltaY_Mm,
        double deltaThetaRad)
    {
        double cosT = Math.Cos(deltaThetaRad);
        double sinT = Math.Sin(deltaThetaRad);

        double relX = currentX_Mm - corX_Mm;
        double relY = currentY_Mm - corY_Mm;

        double finalX = corX_Mm + (cosT * relX) - (sinT * relY) + deltaX_Mm;
        double finalY = corY_Mm + (sinT * relX) + (cosT * relY) + deltaY_Mm;
        double finalTheta = NormalizeAngleMinusPiToPi(currentThetaRad + deltaThetaRad);
        return (finalX, finalY, finalTheta);
    }

    private static double NormalizeAngleMinusPiToPi(double angleRad)
    {
        double twoPi = 2.0 * Math.PI;
        double wrapped = (angleRad + Math.PI) % twoPi;
        if (wrapped < 0.0)
        {
            wrapped += twoPi;
        }

        return wrapped - Math.PI;
    }
}
