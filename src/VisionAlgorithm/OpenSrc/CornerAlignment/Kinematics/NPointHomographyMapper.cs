// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Kinematics/NPointHomographyMapper.cs
using System;
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Kinematics;

/// <summary>
/// Hartley(1997) 등방성 정규화 DLT(Direct Linear Transform) 기반 9-Point 픽셀(px) ↔ 물리 좌표(mm)
/// 3×3 평면 호모그래피 캘리브레이션 및 좌표 변환기.
/// </summary>
public readonly record struct NPointHomographyMapper(
    double H00, double H01, double H02,
    double H10, double H11, double H12,
    double H20, double H21, double H22,
    double RmsReprojectionErrorMm)
{
    private const int MinCalibrationPoints = 4;
    private const double RootTwo = 1.4142135623730951;
    private const double HomographyWThreshold = 1e-12;

    /// <summary>
    /// 픽셀 좌표와 물리 좌표(mm) 대응점 쌍(최소 4점, 표준 9점)으로부터 Hartley 정규화 DLT + 9×9 야코비안 SVD로
    /// 정밀 3×3 호모그래피 변환기를 생성한다.
    /// </summary>
    public static NPointHomographyMapper Calibrate(
        ReadOnlySpan<Vector2> pixelPoints,
        ReadOnlySpan<Vector2> worldMmPoints)
    {
        int n = pixelPoints.Length;
        if (n < MinCalibrationPoints || worldMmPoints.Length != n)
        {
            throw new ArgumentException("호모그래피 보정에는 동일한 개수의 최소 4개 이상 대응점이 필요합니다.");
        }

        ComputeIsotropicNormalization(pixelPoints, out double srcMeanX, out double srcMeanY, out double srcScale);
        ComputeIsotropicNormalization(worldMmPoints, out double dstMeanX, out double dstMeanY, out double dstScale);

        // A^T * A (9x9 대칭 행렬) 누적 구성
        Span<double> ata = stackalloc double[81];
        ata.Clear();

        Span<double> row1 = stackalloc double[9];
        Span<double> row2 = stackalloc double[9];

        for (int i = 0; i < n; i++)
        {
            double x = (pixelPoints[i].X - srcMeanX) * srcScale;
            double y = (pixelPoints[i].Y - srcMeanY) * srcScale;
            double u = (worldMmPoints[i].X - dstMeanX) * dstScale;
            double v = (worldMmPoints[i].Y - dstMeanY) * dstScale;

            row1[0] = -x; row1[1] = -y; row1[2] = -1.0;
            row1[3] = 0.0; row1[4] = 0.0; row1[5] = 0.0;
            row1[6] = u * x; row1[7] = u * y; row1[8] = u;

            row2[0] = 0.0; row2[1] = 0.0; row2[2] = 0.0;
            row2[3] = -x; row2[4] = -y; row2[5] = -1.0;
            row2[6] = v * x; row2[7] = v * y; row2[8] = v;

            AccumulateOuterProduct9(ata, row1);
            AccumulateOuterProduct9(ata, row2);
        }

        Span<double> hNorm = stackalloc double[9];
        SolveSmallestEigenvectorJacobi9(ata, hNorm);

        // 역정규화: H = T_dst^-1 * H_norm * T_src
        double invDstScale = 1.0 / dstScale;
        Span<double> temp = stackalloc double[9];
        for (int c = 0; c < 3; c++)
        {
            double h0c = hNorm[c];
            double h1c = hNorm[3 + c];
            double h2c = hNorm[6 + c];

            temp[c] = (invDstScale * h0c) + (dstMeanX * h2c);
            temp[3 + c] = (invDstScale * h1c) + (dstMeanY * h2c);
            temp[6 + c] = h2c;
        }

        Span<double> h = stackalloc double[9];
        for (int r = 0; r < 3; r++)
        {
            double tr0 = temp[r * 3];
            double tr1 = temp[(r * 3) + 1];
            double tr2 = temp[(r * 3) + 2];

            h[r * 3] = tr0 * srcScale;
            h[(r * 3) + 1] = tr1 * srcScale;
            h[(r * 3) + 2] = (-tr0 * srcMeanX * srcScale) - (tr1 * srcMeanY * srcScale) + tr2;
        }

        double normFactor = Math.Abs(h[8]) > HomographyWThreshold ? 1.0 / h[8] : 1.0;
        for (int k = 0; k < 9; k++)
        {
            h[k] *= normFactor;
        }

        var mapper = new NPointHomographyMapper(
            h[0], h[1], h[2],
            h[3], h[4], h[5],
            h[6], h[7], h[8],
            RmsReprojectionErrorMm: 0.0);

        double sumSqErr = 0.0;
        for (int i = 0; i < n; i++)
        {
            (double wx, double wy) = mapper.MapPixelToWorldMm(pixelPoints[i].X, pixelPoints[i].Y);
            double dx = wx - worldMmPoints[i].X;
            double dy = wy - worldMmPoints[i].Y;
            sumSqErr += (dx * dx) + (dy * dy);
        }

        double rms = Math.Sqrt(sumSqErr / n);
        return mapper with { RmsReprojectionErrorMm = rms };
    }

    /// <summary>
    /// 서브픽셀 영상 좌표 <c>(pixelX, pixelY)</c>를 물리 평면 좌표 <c>(worldX_Mm, worldY_Mm)</c>로 변환한다.
    /// </summary>
    public (double WorldX_Mm, double WorldY_Mm) MapPixelToWorldMm(double pixelX, double pixelY)
    {
        double w = (H20 * pixelX) + (H21 * pixelY) + H22;
        double invW = Math.Abs(w) > HomographyWThreshold ? 1.0 / w : 1.0;
        double wx = ((H00 * pixelX) + (H01 * pixelY) + H02) * invW;
        double wy = ((H10 * pixelX) + (H11 * pixelY) + H12) * invW;
        return (wx, wy);
    }

    /// <summary>
    /// 서브픽셀 영상 좌표 벡터를 물리 좌표 벡터(mm)로 변환한다.
    /// </summary>
    public Vector2 MapPixelToWorldMm(Vector2 pixelPoint)
    {
        (double wx, double wy) = MapPixelToWorldMm(pixelPoint.X, pixelPoint.Y);
        return new Vector2((float)wx, (float)wy);
    }

    private static void ComputeIsotropicNormalization(
        ReadOnlySpan<Vector2> pts,
        out double meanX,
        out double meanY,
        out double scale)
    {
        double sx = 0.0;
        double sy = 0.0;
        for (int i = 0; i < pts.Length; i++)
        {
            sx += pts[i].X;
            sy += pts[i].Y;
        }

        double invN = 1.0 / pts.Length;
        meanX = sx * invN;
        meanY = sy * invN;

        double meanDist = 0.0;
        for (int i = 0; i < pts.Length; i++)
        {
            double dx = pts[i].X - meanX;
            double dy = pts[i].Y - meanY;
            meanDist += Math.Sqrt((dx * dx) + (dy * dy));
        }

        meanDist *= invN;
        scale = meanDist > 1e-9 ? RootTwo / meanDist : 1.0;
    }

    private static void AccumulateOuterProduct9(Span<double> ata, ReadOnlySpan<double> row)
    {
        for (int r = 0; r < 9; r++)
        {
            double rv = row[r];
            int baseIdx = r * 9;
            for (int c = r; c < 9; c++)
            {
                ata[baseIdx + c] += rv * row[c];
            }
        }

        for (int r = 1; r < 9; r++)
        {
            for (int c = 0; c < r; c++)
            {
                ata[(r * 9) + c] = ata[(c * 9) + r];
            }
        }
    }

    private static void SolveSmallestEigenvectorJacobi9(Span<double> a, Span<double> minEigVecOut)
    {
        Span<double> v = stackalloc double[81];
        v.Clear();
        for (int i = 0; i < 9; i++)
        {
            v[(i * 9) + i] = 1.0;
        }

        for (int sweep = 0; sweep < 45; sweep++)
        {
            double maxOffDiag = 0.0;
            int p = 0;
            int q = 1;

            for (int r = 0; r < 8; r++)
            {
                for (int c = r + 1; c < 9; c++)
                {
                    double val = Math.Abs(a[(r * 9) + c]);
                    if (val > maxOffDiag)
                    {
                        maxOffDiag = val;
                        p = r;
                        q = c;
                    }
                }
            }

            if (maxOffDiag < 1e-15)
            {
                break;
            }

            double app = a[(p * 9) + p];
            double aqq = a[(q * 9) + q];
            double apq = a[(p * 9) + q];

            double tau = (aqq - app) / (2.0 * apq);
            double t = tau >= 0.0
                ? 1.0 / (tau + Math.Sqrt(1.0 + (tau * tau)))
                : -1.0 / (-tau + Math.Sqrt(1.0 + (tau * tau)));
            double cos = 1.0 / Math.Sqrt(1.0 + (t * t));
            double sin = t * cos;

            for (int k = 0; k < 9; k++)
            {
                if (k == p || k == q)
                {
                    continue;
                }

                double akp = a[(k * 9) + p];
                double akq = a[(k * 9) + q];
                a[(k * 9) + p] = a[(p * 9) + k] = (cos * akp) - (sin * akq);
                a[(k * 9) + q] = a[(q * 9) + k] = (sin * akp) + (cos * akq);
            }

            a[(p * 9) + p] = (cos * cos * app) - (2.0 * sin * cos * apq) + (sin * sin * aqq);
            a[(q * 9) + q] = (sin * sin * app) + (2.0 * sin * cos * apq) + (cos * cos * aqq);
            a[(p * 9) + q] = a[(q * 9) + p] = 0.0;

            for (int k = 0; k < 9; k++)
            {
                double vkp = v[(k * 9) + p];
                double vkq = v[(k * 9) + q];
                v[(k * 9) + p] = (cos * vkp) - (sin * vkq);
                v[(k * 9) + q] = (sin * vkp) + (cos * vkq);
            }
        }

        int minIdx = 0;
        double minVal = a[0];
        for (int i = 1; i < 9; i++)
        {
            double val = a[(i * 9) + i];
            if (val < minVal)
            {
                minVal = val;
                minIdx = i;
            }
        }

        for (int i = 0; i < 9; i++)
        {
            minEigVecOut[i] = v[(i * 9) + minIdx];
        }
    }
}
