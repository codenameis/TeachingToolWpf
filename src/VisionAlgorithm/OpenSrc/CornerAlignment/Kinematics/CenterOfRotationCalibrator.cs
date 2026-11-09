// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Kinematics/CenterOfRotationCalibrator.cs
using System;
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;

/// <summary>
/// X, Y 축을 고정한 채 Theta 축만 다중 각도로 회전시켰을 때 관측된 물리 코너 궤적 좌표들로부터
/// Taubin(1991) 대수적 원 피팅 및 Gauss-Newton 기하 비선형 최적화를 통해
/// 기구적 회전 중심(<c>CenterX_Mm, CenterY_Mm</c>)과 레버 반경(<c>Radius_Mm</c>)을 산출한다.
/// </summary>
public static class CenterOfRotationCalibrator
{
    private const int MinTrajectoryPoints = 3;
    private const int GaussNewtonMaxIterations = 15;
    private const double ConvergenceStepEpsilon = 1e-13;
    private const double DeterminantEpsilon = 1e-15;

    /// <summary>
    /// 배정밀도(<see cref="double"/>) 코너 회전 궤적 점군으로부터 회전 중심(CoR) 좌표와 회전 반경, RMS 잔차를 산출한다.
    /// </summary>
    public static (double CenterX_Mm, double CenterY_Mm, double Radius_Mm, double RmsResidual_Mm, bool IsValid) CalibrateCenterOfRotation(
        ReadOnlySpan<(double X_Mm, double Y_Mm)> rotatedCornerPositions)
    {
        int n = rotatedCornerPositions.Length;
        if (n < MinTrajectoryPoints)
        {
            return (0.0, 0.0, 0.0, double.PositiveInfinity, false);
        }

        double meanX = 0.0;
        double meanY = 0.0;
        for (int i = 0; i < n; i++)
        {
            meanX += rotatedCornerPositions[i].X_Mm;
            meanY += rotatedCornerPositions[i].Y_Mm;
        }

        double invN = 1.0 / n;
        meanX *= invN;
        meanY *= invN;

        // 1단계: 중심화 좌표계에서의 Taubin(1991) / Kasa 3x3 정규방정식 닫힌 해로 초기 회전 중심 산출
        if (!TrySolveCenteredAlgebraicCircle(rotatedCornerPositions, meanX, meanY, out double uc, out double vc, out double radius))
        {
            return (0.0, 0.0, 0.0, double.PositiveInfinity, false);
        }

        double cx = meanX + uc;
        double cy = meanY + vc;

        // 2단계: 소각도 원호(예: -5° ~ +5°)에서도 무편향 정밀도를 보장하는 Gauss-Newton 기하 거리 최소화
        RefineGeometricCircleGaussNewton(rotatedCornerPositions, ref cx, ref cy, ref radius);

        double sumSqRadialErr = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = rotatedCornerPositions[i].X_Mm - cx;
            double dy = rotatedCornerPositions[i].Y_Mm - cy;
            double r = Math.Sqrt((dx * dx) + (dy * dy));
            double err = r - radius;
            sumSqRadialErr += err * err;
        }

        double rms = Math.Sqrt(sumSqRadialErr * invN);
        bool valid = double.IsFinite(cx) && double.IsFinite(cy) && radius > 1e-6;
        return (cx, cy, radius, rms, valid);
    }

    /// <summary>
    /// <see cref="Vector2"/> 코너 회전 궤적 점군에 대한 오버로드.
    /// </summary>
    public static (double CenterX_Mm, double CenterY_Mm, double Radius_Mm, double RmsResidual_Mm, bool IsValid) CalibrateCenterOfRotation(
        ReadOnlySpan<Vector2> rotatedCornerPositions)
    {
        int n = rotatedCornerPositions.Length;
        Span<(double X_Mm, double Y_Mm)> pts = n <= 64
            ? stackalloc (double X_Mm, double Y_Mm)[n]
            : new (double X_Mm, double Y_Mm)[n];
        for (int i = 0; i < n; i++)
        {
            pts[i] = (rotatedCornerPositions[i].X, rotatedCornerPositions[i].Y);
        }

        return CalibrateCenterOfRotation(pts);
    }

    private static bool TrySolveCenteredAlgebraicCircle(
        ReadOnlySpan<(double X_Mm, double Y_Mm)> pts,
        double meanX,
        double meanY,
        out double uc,
        out double vc,
        out double radius)
    {
        uc = 0.0;
        vc = 0.0;
        radius = 0.0;

        int n = pts.Length;
        double suu = 0.0, svv = 0.0, suv = 0.0;
        double suuu = 0.0, svvv = 0.0, suuv = 0.0, suvv = 0.0;

        for (int i = 0; i < n; i++)
        {
            double u = pts[i].X_Mm - meanX;
            double v = pts[i].Y_Mm - meanY;
            double uu = u * u;
            double vv = v * v;

            suu += uu;
            svv += vv;
            suv += u * v;
            suuu += uu * u;
            svvv += vv * v;
            suuv += uu * v;
            suvv += u * vv;
        }

        // 선형 시스템:
        // suu * uc + suv * vc = 0.5 * (suuu + suvv)
        // suv * uc + svv * vc = 0.5 * (svvv + suuv)
        double det = (suu * svv) - (suv * suv);
        if (Math.Abs(det) <= DeterminantEpsilon)
        {
            return false;
        }

        double rhs1 = 0.5 * (suuu + suvv);
        double rhs2 = 0.5 * (svvv + suuv);
        double invDet = 1.0 / det;

        uc = ((rhs1 * svv) - (rhs2 * suv)) * invDet;
        vc = ((suu * rhs2) - (suv * rhs1)) * invDet;

        double rSq = (uc * uc) + (vc * vc) + ((suu + svv) / n);
        if (rSq <= 0.0)
        {
            return false;
        }

        radius = Math.Sqrt(rSq);
        return true;
    }

    private static void RefineGeometricCircleGaussNewton(
        ReadOnlySpan<(double X_Mm, double Y_Mm)> pts,
        ref double cx,
        ref double cy,
        ref double radius)
    {
        int n = pts.Length;
        for (int iter = 0; iter < GaussNewtonMaxIterations; iter++)
        {
            double j00 = 0.0, j01 = 0.0, j02 = 0.0;
            double j11 = 0.0, j12 = 0.0, j22 = n;
            double b0 = 0.0, b1 = 0.0, b2 = 0.0;

            for (int i = 0; i < n; i++)
            {
                double dx = cx - pts[i].X_Mm;
                double dy = cy - pts[i].Y_Mm;
                double dist = Math.Sqrt((dx * dx) + (dy * dy));
                if (dist < 1e-12)
                {
                    continue;
                }

                double jx = dx / dist;
                double jy = dy / dist;
                double residual = dist - radius;

                j00 += jx * jx;
                j01 += jx * jy;
                j02 -= jx;
                j11 += jy * jy;
                j12 -= jy;

                b0 -= jx * residual;
                b1 -= jy * residual;
                b2 += residual;
            }

            if (!TrySolveSymmetric3x3(j00, j01, j02, j11, j12, j22, b0, b1, b2, out double dCx, out double dCy, out double dR))
            {
                break;
            }

            cx += dCx;
            cy += dCy;
            radius += dR;

            if (((dCx * dCx) + (dCy * dCy) + (dR * dR)) < ConvergenceStepEpsilon)
            {
                break;
            }
        }
    }

    private static bool TrySolveSymmetric3x3(
        double a00, double a01, double a02,
        double a11, double a12, double a22,
        double b0, double b1, double b2,
        out double x0, out double x1, out double x2)
    {
        x0 = 0.0;
        x1 = 0.0;
        x2 = 0.0;

        double c00 = (a11 * a22) - (a12 * a12);
        double c01 = (a02 * a12) - (a01 * a22);
        double c02 = (a01 * a12) - (a02 * a11);
        double det = (a00 * c00) + (a01 * c01) + (a02 * c02);

        if (Math.Abs(det) <= DeterminantEpsilon)
        {
            return false;
        }

        double c11 = (a00 * a22) - (a02 * a02);
        double c12 = (a01 * a02) - (a00 * a12);
        double c22 = (a00 * a11) - (a01 * a01);

        double invDet = 1.0 / det;
        x0 = ((c00 * b0) + (c01 * b1) + (c02 * b2)) * invDet;
        x1 = ((c01 * b0) + (c11 * b1) + (c12 * b2)) * invDet;
        x2 = ((c02 * b0) + (c12 * b1) + (c22 * b2)) * invDet;
        return true;
    }
}
