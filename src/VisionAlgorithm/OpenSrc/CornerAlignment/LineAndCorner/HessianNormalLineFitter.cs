// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/LineAndCorner/HessianNormalLineFitter.cs
using System;
using System.Numerics;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;
using VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.LineAndCorner;

/// <summary>
/// 특이점이 없는 헤세 법선 방정식(<c>nx * x + ny * y - rho = 0</c>) 기반 2×2 가중 공분산 직교 거리 회귀(ODR),
/// 2-Point 최소 샘플링 RANSAC 및 5-Step Tukey Biweight IRLS 직선 피팅기.
/// </summary>
public static class HessianNormalLineFitter
{
    private const int MinPointsForLine = 3;
    private const float TukeyTuningConstant = 4.685f;
    private const float GaussianMadScaleFactor = 1.4826f;
    private const float MinTukeyCutoffPixels = 0.05f;
    private const float MinPairDistanceSquared = 4.0f;
    private const int StackAllocLimit = 256;

    /// <summary>
    /// 추출된 서브픽셀 에지 점군에 대해 2-Point RANSAC으로 칩핑/버 이상치를 제거한 뒤,
    /// Tukey Biweight IRLS 직교 거리 회귀를 수행하고 각 점의 인라이어 여부를 갱신한다.
    /// </summary>
    public static FittedLine2D FitLine(
        Span<SubpixelEdgePoint> points,
        CornerDetectionOptions options,
        Vector2 preferredTangentDirection = default)
    {
        int n = points.Length;
        if (n < MinPointsForLine)
        {
            return FittedLine2D.Invalid;
        }

        float ransacThreshold = MathF.Max(0.05f, options.RansacInlierThresholdPixels);
        int tukeyIterations = Math.Clamp(options.TukeyIterations, 1, 15);

        Span<float> weights = n <= StackAllocLimit ? stackalloc float[n] : new float[n];
        Span<float> absResiduals = n <= StackAllocLimit ? stackalloc float[n] : new float[n];

        // 1단계: 2-Point 최소 샘플링 RANSAC (MSAC 손실 기반 타이브레이킹)
        if (!TryFindBestRansacHypothesis(points, ransacThreshold, out float bestNx, out float bestNy, out float bestRho))
        {
            return FittedLine2D.Invalid;
        }

        int initialInliers = 0;
        for (int i = 0; i < n; i++)
        {
            float r = MathF.Abs((bestNx * points[i].X) + (bestNy * points[i].Y) - bestRho);
            if (r <= ransacThreshold)
            {
                weights[i] = 1.0f;
                initialInliers++;
            }
            else
            {
                weights[i] = 0.0f;
            }
        }

        if (initialInliers < MinPointsForLine)
        {
            return FittedLine2D.Invalid;
        }

        // RANSAC 인라이어 초기 직교 거리 회귀(ODR)
        if (!TrySolveWeightedOrthogonalRegression(points, weights, out float nx, out float ny, out float rho))
        {
            nx = bestNx;
            ny = bestNy;
            rho = bestRho;
        }

        // 2단계: Tukey Biweight 가중 최소자승법(IRLS) 반복 정밀화
        for (int iter = 0; iter < tukeyIterations; iter++)
        {
            int activeCount = 0;
            for (int i = 0; i < n; i++)
            {
                float r = MathF.Abs((nx * points[i].X) + (ny * points[i].Y) - rho);
                if (r <= ransacThreshold * 2.0f)
                {
                    absResiduals[activeCount++] = r;
                }
            }

            if (activeCount < MinPointsForLine)
            {
                break;
            }

            float mad = ComputeMedian(absResiduals.Slice(0, activeCount));
            float cutoff = MathF.Max(TukeyTuningConstant * GaussianMadScaleFactor * mad, MinTukeyCutoffPixels);
            float invCutoff = 1.0f / cutoff;

            int nonZeroWeights = 0;
            for (int i = 0; i < n; i++)
            {
                float r = (nx * points[i].X) + (ny * points[i].Y) - rho;
                float u = MathF.Abs(r) * invCutoff;
                if (u <= 1.0f)
                {
                    float oneMinusUSq = 1.0f - (u * u);
                    weights[i] = oneMinusUSq * oneMinusUSq;
                    nonZeroWeights++;
                }
                else
                {
                    weights[i] = 0.0f;
                }
            }

            if (nonZeroWeights < MinPointsForLine)
            {
                break;
            }

            if (!TrySolveWeightedOrthogonalRegression(points, weights, out float refinedNx, out float refinedNy, out float refinedRho))
            {
                break;
            }

            nx = refinedNx;
            ny = refinedNy;
            rho = refinedRho;
        }

        // 3단계: 최종 인라이어 마킹, RMS 잔차 계산 및 접선 방향 정렬
        int finalInlierCount = 0;
        double sumSqError = 0.0;
        double weightSum = 0.0;
        int firstInlierIdx = -1;
        int lastInlierIdx = -1;

        for (int i = 0; i < n; i++)
        {
            float r = (nx * points[i].X) + (ny * points[i].Y) - rho;
            bool isInlier = weights[i] > 1e-4f && MathF.Abs(r) <= ransacThreshold;
            points[i] = points[i] with { IsInlier = isInlier };
            if (isInlier)
            {
                finalInlierCount++;
                sumSqError += (double)r * r * weights[i];
                weightSum += weights[i];
                if (firstInlierIdx < 0)
                {
                    firstInlierIdx = i;
                }

                lastInlierIdx = i;
            }
        }

        if (finalInlierCount < MinPointsForLine)
        {
            return FittedLine2D.Invalid;
        }

        Vector2 referenceDir = preferredTangentDirection;
        if (referenceDir.LengthSquared() < 1e-6f && firstInlierIdx >= 0 && lastInlierIdx > firstInlierIdx)
        {
            referenceDir = points[lastInlierIdx].Position - points[firstInlierIdx].Position;
        }

        float tx = -ny;
        float ty = nx;
        if (referenceDir.LengthSquared() > 1e-6f && ((tx * referenceDir.X) + (ty * referenceDir.Y)) < 0.0f)
        {
            tx = -tx;
            ty = -ty;
            nx = -nx;
            ny = -ny;
            rho = -rho;
        }

        float rms = weightSum > 1e-9 ? (float)Math.Sqrt(sumSqError / weightSum) : 0.0f;
        return new FittedLine2D(
            Nx: nx,
            Ny: ny,
            Rho: rho,
            TangentX: tx,
            TangentY: ty,
            InlierCount: finalInlierCount,
            RmsResidual: rms,
            IsValid: true);
    }

    private static bool TryFindBestRansacHypothesis(
        ReadOnlySpan<SubpixelEdgePoint> points,
        float inlierThreshold,
        out float bestNx,
        out float bestNy,
        out float bestRho)
    {
        bestNx = 0.0f;
        bestNy = 1.0f;
        bestRho = 0.0f;

        int n = points.Length;
        float thresholdSq = inlierThreshold * inlierThreshold;
        int bestInliers = 0;
        float bestMsacLoss = float.PositiveInfinity;

        int outerStride = Math.Max(1, n / 28);
        for (int i = 0; i < n - 1; i += outerStride)
        {
            Vector2 p1 = points[i].Position;
            for (int j = i + 1; j < n; j++)
            {
                Vector2 p2 = points[j].Position;
                Vector2 delta = p2 - p1;
                float distSq = delta.LengthSquared();
                if (distSq < MinPairDistanceSquared)
                {
                    continue;
                }

                float invLen = 1.0f / MathF.Sqrt(distSq);
                float nx = -delta.Y * invLen;
                float ny = delta.X * invLen;
                float rho = (nx * p1.X) + (ny * p1.Y);

                int inliers = 0;
                float msacLoss = 0.0f;
                for (int k = 0; k < n; k++)
                {
                    float r = (nx * points[k].X) + (ny * points[k].Y) - rho;
                    float rSq = r * r;
                    if (rSq <= thresholdSq)
                    {
                        inliers++;
                        msacLoss += rSq;
                    }
                    else
                    {
                        msacLoss += thresholdSq;
                    }
                }

                if (inliers > bestInliers || (inliers == bestInliers && msacLoss < bestMsacLoss))
                {
                    bestInliers = inliers;
                    bestMsacLoss = msacLoss;
                    bestNx = nx;
                    bestNy = ny;
                    bestRho = rho;
                }
            }
        }

        return bestInliers >= MinPointsForLine;
    }

    /// <summary>
    /// 가중치가 부여된 점군의 2×2 공분산 행렬 고유벡터 닫힌 해로 헤세 법선 직선을 산출한다:
    /// <c>alpha = 0.5 * atan2(-2 * Cxy, Cyy - Cxx)</c>.
    /// </summary>
    private static bool TrySolveWeightedOrthogonalRegression(
        ReadOnlySpan<SubpixelEdgePoint> points,
        ReadOnlySpan<float> weights,
        out float nx,
        out float ny,
        out float rho)
    {
        nx = 0.0f;
        ny = 1.0f;
        rho = 0.0f;

        double sumW = 0.0;
        double sumWx = 0.0;
        double sumWy = 0.0;

        for (int i = 0; i < points.Length; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }

            sumW += w;
            sumWx += w * points[i].X;
            sumWy += w * points[i].Y;
        }

        if (sumW <= 1e-9)
        {
            return false;
        }

        double meanX = sumWx / sumW;
        double meanY = sumWy / sumW;

        double cxx = 0.0;
        double cyy = 0.0;
        double cxy = 0.0;

        for (int i = 0; i < points.Length; i++)
        {
            double w = weights[i];
            if (w <= 0.0)
            {
                continue;
            }

            double dx = points[i].X - meanX;
            double dy = points[i].Y - meanY;
            cxx += w * dx * dx;
            cyy += w * dy * dy;
            cxy += w * dx * dy;
        }

        if ((cxx + cyy) <= 1e-12)
        {
            return false;
        }

        double alpha = 0.5 * Math.Atan2(-2.0 * cxy, cyy - cxx);
        double cosA = Math.Cos(alpha);
        double sinA = Math.Sin(alpha);
        double computedRho = (cosA * meanX) + (sinA * meanY);

        nx = (float)cosA;
        ny = (float)sinA;
        rho = (float)computedRho;
        return true;
    }

    private static float ComputeMedian(Span<float> values)
    {
        values.Sort();
        int count = values.Length;
        int mid = count >> 1;
        return (count & 1) == 1
            ? values[mid]
            : 0.5f * (values[mid - 1] + values[mid]);
    }
}
