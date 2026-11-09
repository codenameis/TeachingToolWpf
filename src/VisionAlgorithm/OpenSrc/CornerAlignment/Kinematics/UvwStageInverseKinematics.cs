// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Kinematics/UvwStageInverseKinematics.cs
using System;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;

/// <summary>
/// 3축 병렬 리니어 액추에이터(UVW 스테이지)의 힌지(Hinge) 기하 구조를 기반으로
/// 목표 강체 이동량 <c>(DeltaX, DeltaY, DeltaTheta)</c>과 각 액추에이터 스트로크 <c>(DeltaU, DeltaV, DeltaW)</c> 사이의
/// 역기구학(Inverse Kinematics) 및 순기구학(Forward Kinematics)을 계산한다.
/// </summary>
public sealed class UvwStageInverseKinematics
{
    private const int NewtonMaxIterations = 25;
    private const double NewtonTolerance = 1e-13;
    private const double JacobianDetEpsilon = 1e-15;

    private readonly double _uX, _uY, _cosPhiU, _sinPhiU;
    private readonly double _vX, _vY, _cosPhiV, _sinPhiV;
    private readonly double _wX, _wY, _cosPhiW, _sinPhiW;

    /// <summary>
    /// 3개 액추에이터(U, V, W)의 스테이지 회전 중심 기준 힌지 좌표 <c>(x_i, y_i)</c> 및
    /// 구동 방향각 <c>phi_i</c>(라디안)로 기구학 솔버를 초기화한다.
    /// </summary>
    public UvwStageInverseKinematics(
        (double HingeX_Mm, double HingeY_Mm, double DriveAngleRad) axisU,
        (double HingeX_Mm, double HingeY_Mm, double DriveAngleRad) axisV,
        (double HingeX_Mm, double HingeY_Mm, double DriveAngleRad) axisW)
    {
        _uX = axisU.HingeX_Mm;
        _uY = axisU.HingeY_Mm;
        _cosPhiU = Math.Cos(axisU.DriveAngleRad);
        _sinPhiU = Math.Sin(axisU.DriveAngleRad);

        _vX = axisV.HingeX_Mm;
        _vY = axisV.HingeY_Mm;
        _cosPhiV = Math.Cos(axisV.DriveAngleRad);
        _sinPhiV = Math.Sin(axisV.DriveAngleRad);

        _wX = axisW.HingeX_Mm;
        _wY = axisW.HingeY_Mm;
        _cosPhiW = Math.Cos(axisW.DriveAngleRad);
        _sinPhiW = Math.Sin(axisW.DriveAngleRad);
    }

    /// <summary>
    /// 산업 현장 표준 대칭형 UVW 스테이지 구성을 생성한다
    /// (U: 상단 X 방향, V: 좌하단 X 방향, W: 우하단 Y 방향, 스테이지 반경 <paramref name="stageRadiusMm"/>).
    /// </summary>
    public static UvwStageInverseKinematics CreateStandardStage(double stageRadiusMm = 150.0)
        => new(
            axisU: (0.0, stageRadiusMm, 0.0),
            axisV: (-stageRadiusMm, -stageRadiusMm, 0.0),
            axisW: (stageRadiusMm, -stageRadiusMm, Math.PI * 0.5));

    /// <summary>
    /// 역기구학(Inverse Kinematics): 목표 강체 보정량 <c>(deltaX_Mm, deltaY_Mm, deltaThetaRad)</c>을
    /// 3개 액추에이터의 선형 스트로크 <c>(DeltaU_Mm, DeltaV_Mm, DeltaW_Mm)</c> 및 통합 명령으로 변환한다.
    /// </summary>
    public MotionCorrectionCommand ComputeActuatorStrokes(
        double deltaX_Mm,
        double deltaY_Mm,
        double deltaThetaRad)
    {
        double cosTMinus1 = Math.Cos(deltaThetaRad) - 1.0;
        double sinT = Math.Sin(deltaThetaRad);

        double dU = ComputeSingleAxisStroke(deltaX_Mm, deltaY_Mm, cosTMinus1, sinT, _uX, _uY, _cosPhiU, _sinPhiU);
        double dV = ComputeSingleAxisStroke(deltaX_Mm, deltaY_Mm, cosTMinus1, sinT, _vX, _vY, _cosPhiV, _sinPhiV);
        double dW = ComputeSingleAxisStroke(deltaX_Mm, deltaY_Mm, cosTMinus1, sinT, _wX, _wY, _cosPhiW, _sinPhiW);

        return new MotionCorrectionCommand(
            DeltaX_Mm: deltaX_Mm,
            DeltaY_Mm: deltaY_Mm,
            DeltaThetaRad: deltaThetaRad,
            DeltaU_Mm: dU,
            DeltaV_Mm: dV,
            DeltaW_Mm: dW);
    }

    /// <summary>
    /// 순기구학(Forward Kinematics): 액추에이터 스트로크 <c>(deltaU_Mm, deltaV_Mm, deltaW_Mm)</c>로부터
    /// 비선형 힌지 기구학 방정식의 Newton-Raphson 해를 구해 실제 스테이지 강체 변위 <c>(DeltaX_Mm, DeltaY_Mm, DeltaThetaRad)</c>를 역산한다.
    /// </summary>
    public (double DeltaX_Mm, double DeltaY_Mm, double DeltaThetaRad, bool Converged) SolveForwardKinematics(
        double deltaU_Mm,
        double deltaV_Mm,
        double deltaW_Mm)
    {
        double dx = 0.0;
        double dy = 0.0;
        double dTheta = 0.0;

        for (int iter = 0; iter < NewtonMaxIterations; iter++)
        {
            double cosT = Math.Cos(dTheta);
            double sinT = Math.Sin(dTheta);
            double cosTMinus1 = cosT - 1.0;

            double fU = ComputeSingleAxisStroke(dx, dy, cosTMinus1, sinT, _uX, _uY, _cosPhiU, _sinPhiU) - deltaU_Mm;
            double fV = ComputeSingleAxisStroke(dx, dy, cosTMinus1, sinT, _vX, _vY, _cosPhiV, _sinPhiV) - deltaV_Mm;
            double fW = ComputeSingleAxisStroke(dx, dy, cosTMinus1, sinT, _wX, _wY, _cosPhiW, _sinPhiW) - deltaW_Mm;

            if (((fU * fU) + (fV * fV) + (fW * fW)) < NewtonTolerance * NewtonTolerance)
            {
                return (dx, dy, dTheta, true);
            }

            double jUTheta = ((-_uX * sinT) - (_uY * cosT)) * _cosPhiU + ((_uX * cosT) - (_uY * sinT)) * _sinPhiU;
            double jVTheta = ((-_vX * sinT) - (_vY * cosT)) * _cosPhiV + ((_vX * cosT) - (_vY * sinT)) * _sinPhiV;
            double jWTheta = ((-_wX * sinT) - (_wY * cosT)) * _cosPhiW + ((_wX * cosT) - (_wY * sinT)) * _sinPhiW;

            if (!TrySolve3x3(
                _cosPhiU, _sinPhiU, jUTheta,
                _cosPhiV, _sinPhiV, jVTheta,
                _cosPhiW, _sinPhiW, jWTheta,
                -fU, -fV, -fW,
                out double stepX, out double stepY, out double stepTheta))
            {
                return (dx, dy, dTheta, false);
            }

            dx += stepX;
            dy += stepY;
            dTheta += stepTheta;

            if (((stepX * stepX) + (stepY * stepY) + (stepTheta * stepTheta)) < NewtonTolerance * NewtonTolerance)
            {
                return (dx, dy, dTheta, true);
            }
        }

        return (dx, dy, dTheta, false);
    }

    private static double ComputeSingleAxisStroke(
        double dx,
        double dy,
        double cosTMinus1,
        double sinT,
        double hingeX,
        double hingeY,
        double cosPhi,
        double sinPhi)
    {
        double dispX = dx + (hingeX * cosTMinus1) - (hingeY * sinT);
        double dispY = dy + (hingeX * sinT) + (hingeY * cosTMinus1);
        return (dispX * cosPhi) + (dispY * sinPhi);
    }

    private static bool TrySolve3x3(
        double a00, double a01, double a02,
        double a10, double a11, double a12,
        double a20, double a21, double a22,
        double b0, double b1, double b2,
        out double x0, out double x1, out double x2)
    {
        x0 = 0.0;
        x1 = 0.0;
        x2 = 0.0;

        double c00 = (a11 * a22) - (a12 * a21);
        double c01 = (a12 * a20) - (a10 * a22);
        double c02 = (a10 * a21) - (a11 * a20);
        double det = (a00 * c00) + (a01 * c01) + (a02 * c02);
        if (Math.Abs(det) <= JacobianDetEpsilon)
        {
            return false;
        }

        double invDet = 1.0 / det;
        double det0 = (b0 * ((a11 * a22) - (a12 * a21))) - (a01 * ((b1 * a22) - (a12 * b2))) + (a02 * ((b1 * a21) - (a11 * b2)));
        double det1 = (a00 * ((b1 * a22) - (a12 * b2))) - (b0 * ((a10 * a22) - (a12 * a20))) + (a02 * ((a10 * b2) - (b1 * a20)));
        double det2 = (a00 * ((a11 * b2) - (b1 * a21))) - (a01 * ((a10 * b2) - (b1 * a20))) + (b0 * ((a10 * a21) - (a11 * a20)));

        x0 = det0 * invDet;
        x1 = det1 * invDet;
        x2 = det2 * invDet;
        return true;
    }
}
