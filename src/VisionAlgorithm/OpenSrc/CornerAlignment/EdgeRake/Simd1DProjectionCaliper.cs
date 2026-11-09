// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/EdgeRake/Simd1DProjectionCaliper.cs
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.EdgeRake;

/// <summary>
/// AVX2(<see cref="Vector256{Single}"/>) 가속 2D 회전 박스 1D 평균 투영, 가우시안 1차 미분 컨볼루션 및
/// 3점 포물선 보간을 수행하는 서브픽셀 1D 캘리퍼 측정기.
/// </summary>
public static class Simd1DProjectionCaliper
{
    private const int StackAllocLimit = 512;
    private const float MinGaussianSigma = 0.45f;
    private const float MaxGaussianSigma = 8.0f;
    private const float KernelRadiusSigmaMultiplier = 3.0f;
    private const float ParabolicDenominatorEpsilon = 1e-6f;
    private const float MaxSubpixelOffset = 0.5f;
    private const int MinProjectionStrips = 3;
    private const int MaxProjectionStrips = 65;

    /// <summary>
    /// <see cref="ReadOnlyImageView2D{T}"/> 뷰에서 지정된 중심·스캔 방향·투영 폭으로 서브픽셀 에지 1점을 검출한다.
    /// </summary>
    public static bool TryMeasureEdge(
        ReadOnlyImageView2D<byte> image,
        Vector2 caliperCenter,
        Vector2 unitScanDirection,
        float searchLength,
        float projectionWidth,
        CaliperPolarity polarity,
        float gaussianSigma,
        float minimumContrast,
        out SubpixelEdgePoint edgePoint)
        => TryMeasureEdge(
            image.Pixels,
            image.Width,
            image.Height,
            image.Stride,
            caliperCenter,
            unitScanDirection,
            searchLength,
            projectionWidth,
            polarity,
            gaussianSigma,
            minimumContrast,
            out edgePoint);

    /// <summary>
    /// 원시 8비트 그레이스케일 버퍼에서 2D 회전 박스를 스캔 방향으로 1D 평균 투영한 뒤,
    /// 가우시안 1차 미분 극값의 3점 포물선 서브픽셀 보간 좌표를 산출한다.
    /// </summary>
    public static bool TryMeasureEdge(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        Vector2 caliperCenter,
        Vector2 unitScanDirection,
        float searchLength,
        float projectionWidth,
        CaliperPolarity polarity,
        float gaussianSigma,
        float minimumContrast,
        out SubpixelEdgePoint edgePoint)
    {
        edgePoint = default;
        if (width < 4 || height < 4 || stride < width || searchLength < 4.0f)
        {
            return false;
        }

        float dirLenSq = unitScanDirection.LengthSquared();
        if (dirLenSq < 1e-6f)
        {
            return false;
        }

        Vector2 scanDir = MathF.Abs(dirLenSq - 1.0f) > 1e-4f
            ? Vector2.Normalize(unitScanDirection)
            : unitScanDirection;
        Vector2 projDir = new(-scanDir.Y, scanDir.X);

        // 스캔 주축 방향의 샘플 그리드 원점을 정수 픽셀 격자에 정렬하여 양선형 리샘플링 곡률 편향을 제거한다.
        float gridAlignShift = MathF.Abs(scanDir.X) >= MathF.Abs(scanDir.Y)
            ? (MathF.Round(caliperCenter.X) - caliperCenter.X) / scanDir.X
            : (MathF.Round(caliperCenter.Y) - caliperCenter.Y) / scanDir.Y;
        gridAlignShift = Math.Clamp(gridAlignShift, -0.75f, 0.75f);
        Vector2 alignedCenter = caliperCenter + (gridAlignShift * scanDir);

        float sigma = Math.Clamp(gaussianSigma, MinGaussianSigma, MaxGaussianSigma);
        int kernelRadius = Math.Clamp((int)MathF.Ceiling(sigma * KernelRadiusSigmaMultiplier), 2, 24);
        int halfSearchSamples = Math.Max(2, (int)MathF.Ceiling(searchLength * 0.5f));
        int halfTotalSamples = halfSearchSamples + kernelRadius + 2;
        int sampleCount = (2 * halfTotalSamples) + 1;

        Span<float> profile = sampleCount <= StackAllocLimit
            ? stackalloc float[sampleCount]
            : new float[sampleCount];
        Span<float> gradient = sampleCount <= StackAllocLimit
            ? stackalloc float[sampleCount]
            : new float[sampleCount];
        Span<float> weights = (kernelRadius + 1) <= 64
            ? stackalloc float[kernelRadius + 1]
            : new float[kernelRadius + 1];

        ProjectRotatedBox1D(
            image,
            width,
            height,
            stride,
            alignedCenter,
            scanDir,
            projDir,
            halfTotalSamples,
            projectionWidth,
            profile);

        BuildNormalizedGaussianDerivativeWeights(sigma, kernelRadius, weights);
        ComputeGaussianDerivative1D(profile, gradient, weights, kernelRadius);

        int searchStartIdx = halfTotalSamples - halfSearchSamples;
        int searchEndIdx = halfTotalSamples + halfSearchSamples;

        if (!TryFindBestSubpixelPeak(
            gradient,
            searchStartIdx,
            searchEndIdx,
            polarity,
            minimumContrast,
            out int bestIndex,
            out float subpixelDelta,
            out float peakAmplitude))
        {
            return false;
        }

        float localOffsetAlongScan = (bestIndex - halfTotalSamples) + subpixelDelta;
        if (MathF.Abs(localOffsetAlongScan + gridAlignShift) > (searchLength * 0.5f) + 0.5f)
        {
            return false;
        }

        float worldX = alignedCenter.X + (localOffsetAlongScan * scanDir.X);
        float worldY = alignedCenter.Y + (localOffsetAlongScan * scanDir.Y);
        float normalizedScore = Math.Clamp(peakAmplitude / MathF.Max(minimumContrast * 2.0f, 1.0f), 0.0f, 1.0f);

        edgePoint = new SubpixelEdgePoint(
            X: worldX,
            Y: worldY,
            Magnitude: peakAmplitude,
            Score: normalizedScore,
            IsInlier: true);
        return true;
    }

    /// <summary>
    /// 2D 회전 박스의 투영 폭(<paramref name="projectionWidth"/>) 방향으로 양선형 보간 샘플들을 평균 투영하여
    /// 1D 프로파일을 생성한다. <see cref="Vector256{Single}"/>이 지원되는 경우 8개 스캔 위치를 병렬 보간한다.
    /// </summary>
    private static void ProjectRotatedBox1D(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        Vector2 center,
        Vector2 scanDir,
        Vector2 projDir,
        int halfTotalSamples,
        float projectionWidth,
        Span<float> profileOut)
    {
        int sampleCount = profileOut.Length;
        profileOut.Clear();

        float clampedProjWidth = MathF.Max(1.0f, projectionWidth);
        int stripCount = Math.Clamp((int)MathF.Round(clampedProjWidth) | 1, MinProjectionStrips, MaxProjectionStrips);
        float stripStep = stripCount > 1 ? clampedProjWidth / (stripCount - 1) : 0.0f;
        float halfProj = clampedProjWidth * 0.5f;

        float maxValidX = width - 2.001f;
        float maxValidY = height - 2.001f;
        ref byte imgRef = ref MemoryMarshal.GetReference(image);

        Vector256<float> vRamp = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);
        Vector256<float> vZero = Vector256<float>.Zero;
        Vector256<float> vMaxX = Vector256.Create(maxValidX);
        Vector256<float> vMaxY = Vector256.Create(maxValidY);
        Vector256<float> vScanDx = Vector256.Create(scanDir.X);
        Vector256<float> vScanDy = Vector256.Create(scanDir.Y);

        Span<int> xiBuf = stackalloc int[8];
        Span<int> yiBuf = stackalloc int[8];
        Span<float> p00Buf = stackalloc float[8];
        Span<float> p10Buf = stackalloc float[8];
        Span<float> p01Buf = stackalloc float[8];
        Span<float> p11Buf = stackalloc float[8];

        for (int stripIdx = 0; stripIdx < stripCount; stripIdx++)
        {
            float vOffset = stripCount == 1 ? 0.0f : -halfProj + (stripIdx * stripStep);
            float stripOriginX = center.X + (vOffset * projDir.X);
            float stripOriginY = center.Y + (vOffset * projDir.Y);

            int s = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<float> vOriginX = Vector256.Create(stripOriginX);
                Vector256<float> vOriginY = Vector256.Create(stripOriginY);

                for (; s <= sampleCount - 8; s += 8)
                {
                    Vector256<float> vU = Vector256.Create((float)(s - halfTotalSamples)) + vRamp;
                    Vector256<float> vWx = Vector256.Min(Vector256.Max(vOriginX + (vU * vScanDx), vZero), vMaxX);
                    Vector256<float> vWy = Vector256.Min(Vector256.Max(vOriginY + (vU * vScanDy), vZero), vMaxY);

                    Vector256<float> vFloorX = Vector256.Floor(vWx);
                    Vector256<float> vFloorY = Vector256.Floor(vWy);
                    Vector256<float> vFx = vWx - vFloorX;
                    Vector256<float> vFy = vWy - vFloorY;

                    Vector256<int> vXi = Vector256.ConvertToInt32(vFloorX);
                    Vector256<int> vYi = Vector256.ConvertToInt32(vFloorY);
                    vXi.CopyTo(xiBuf);
                    vYi.CopyTo(yiBuf);

                    for (int lane = 0; lane < 8; lane++)
                    {
                        int rowOffset = (yiBuf[lane] * stride) + xiBuf[lane];
                        p00Buf[lane] = Unsafe.Add(ref imgRef, rowOffset);
                        p10Buf[lane] = Unsafe.Add(ref imgRef, rowOffset + 1);
                        p01Buf[lane] = Unsafe.Add(ref imgRef, rowOffset + stride);
                        p11Buf[lane] = Unsafe.Add(ref imgRef, rowOffset + stride + 1);
                    }

                    Vector256<float> vP00 = Vector256.Create<float>(p00Buf);
                    Vector256<float> vP10 = Vector256.Create<float>(p10Buf);
                    Vector256<float> vP01 = Vector256.Create<float>(p01Buf);
                    Vector256<float> vP11 = Vector256.Create<float>(p11Buf);

                    Vector256<float> vTop = vP00 + (vFx * (vP10 - vP00));
                    Vector256<float> vBot = vP01 + (vFx * (vP11 - vP01));
                    Vector256<float> vInterp = vTop + (vFy * (vBot - vTop));

                    Vector256<float> vCurrent = Vector256.Create<float>(profileOut.Slice(s, 8));
                    (vCurrent + vInterp).CopyTo(profileOut.Slice(s, 8));
                }
            }

            for (; s < sampleCount; s++)
            {
                float u = s - halfTotalSamples;
                float wx = Math.Clamp(stripOriginX + (u * scanDir.X), 0.0f, maxValidX);
                float wy = Math.Clamp(stripOriginY + (u * scanDir.Y), 0.0f, maxValidY);
                profileOut[s] += SampleBilinearScalar(ref imgRef, stride, wx, wy);
            }
        }

        float invStripCount = 1.0f / stripCount;
        int idx = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> vScale = Vector256.Create(invStripCount);
            for (; idx <= sampleCount - 8; idx += 8)
            {
                Vector256<float> vVal = Vector256.Create<float>(profileOut.Slice(idx, 8));
                (vVal * vScale).CopyTo(profileOut.Slice(idx, 8));
            }
        }

        for (; idx < sampleCount; idx++)
        {
            profileOut[idx] *= invStripCount;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinearScalar(ref byte imgRef, int stride, float x, float y)
    {
        int x0 = (int)x;
        int y0 = (int)y;
        float fx = x - x0;
        float fy = y - y0;

        int rowOffset = (y0 * stride) + x0;
        float p00 = Unsafe.Add(ref imgRef, rowOffset);
        float p10 = Unsafe.Add(ref imgRef, rowOffset + 1);
        float p01 = Unsafe.Add(ref imgRef, rowOffset + stride);
        float p11 = Unsafe.Add(ref imgRef, rowOffset + stride + 1);

        float top = p00 + (fx * (p10 - p00));
        float bot = p01 + (fx * (p11 - p01));
        return top + (fy * (bot - top));
    }

    private static void BuildNormalizedGaussianDerivativeWeights(float sigma, int kernelRadius, Span<float> weightsOut)
    {
        float invTwoSigmaSq = 1.0f / (2.0f * sigma * sigma);
        float weightSum = 0.0f;
        weightsOut[0] = 0.0f;

        for (int m = 1; m <= kernelRadius; m++)
        {
            float w = m * MathF.Exp(-(m * m) * invTwoSigmaSq);
            weightsOut[m] = w;
            weightSum += w;
        }

        float invSum = weightSum > 1e-8f ? 1.0f / weightSum : 1.0f;
        for (int m = 1; m <= kernelRadius; m++)
        {
            weightsOut[m] *= invSum;
        }
    }

    private static void ComputeGaussianDerivative1D(
        ReadOnlySpan<float> profile,
        Span<float> gradientOut,
        ReadOnlySpan<float> weights,
        int kernelRadius)
    {
        int n = profile.Length;
        gradientOut.Clear();

        for (int i = kernelRadius; i < n - kernelRadius; i++)
        {
            float sum = 0.0f;
            for (int m = 1; m <= kernelRadius; m++)
            {
                sum += weights[m] * (profile[i + m] - profile[i - m]);
            }

            gradientOut[i] = sum;
        }
    }

    private static bool TryFindBestSubpixelPeak(
        ReadOnlySpan<float> gradient,
        int startIdx,
        int endIdx,
        CaliperPolarity polarity,
        float minimumContrast,
        out int bestIndex,
        out float subpixelDelta,
        out float peakAmplitude)
    {
        bestIndex = -1;
        subpixelDelta = 0.0f;
        peakAmplitude = 0.0f;

        for (int k = startIdx; k <= endIdx; k++)
        {
            float gk = gradient[k];
            float gm = gradient[k - 1];
            float gp = gradient[k + 1];

            bool isCandidate = polarity switch
            {
                CaliperPolarity.DarkToLight => gk >= minimumContrast && gk >= gm && gk >= gp,
                CaliperPolarity.LightToDark => -gk >= minimumContrast && gk <= gm && gk <= gp,
                _ => MathF.Abs(gk) >= minimumContrast && MathF.Abs(gk) >= MathF.Abs(gm) && MathF.Abs(gk) >= MathF.Abs(gp)
            };

            if (!isCandidate)
            {
                continue;
            }

            float absGm = MathF.Abs(gm);
            float absGk = MathF.Abs(gk);
            float absGp = MathF.Abs(gp);

            // 가우시안 1차 미분 응답 곡선은 G(u) = A * exp(-(u - delta)^2 / (2 * Sigma^2)) 형태이므로
            // 이웃 3점이 동일 부호일 때 로그 변환 값에 3점 포물선 보간 공식을 적용하면 S-커브 위상 편향이 0으로 소거된다.
            float vMinus = (absGm > 1.0f && absGp > 1.0f && (gm * gk > 0.0f) && (gp * gk > 0.0f)) ? MathF.Log(absGm) : absGm;
            float vCenter = (absGm > 1.0f && absGp > 1.0f && (gm * gk > 0.0f) && (gp * gk > 0.0f)) ? MathF.Log(absGk) : absGk;
            float vPlus = (absGm > 1.0f && absGp > 1.0f && (gm * gk > 0.0f) && (gp * gk > 0.0f)) ? MathF.Log(absGp) : absGp;

            float denom = 2.0f * (vMinus - (2.0f * vCenter) + vPlus);
            float delta = 0.0f;
            float interpolatedValue = absGk;
            if (MathF.Abs(denom) > ParabolicDenominatorEpsilon)
            {
                delta = Math.Clamp((vMinus - vPlus) / denom, -MaxSubpixelOffset, MaxSubpixelOffset);
                float linearDenom = 2.0f * (absGm - (2.0f * absGk) + absGp);
                if (MathF.Abs(linearDenom) > ParabolicDenominatorEpsilon)
                {
                    interpolatedValue = absGk - (0.25f * (absGm - absGp) * delta);
                }
            }

            float amp = MathF.Abs(interpolatedValue);
            if (amp > peakAmplitude)
            {
                peakAmplitude = amp;
                bestIndex = k;
                subpixelDelta = delta;
            }
        }

        return bestIndex >= startIdx;
    }
}
