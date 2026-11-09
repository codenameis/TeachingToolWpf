// File: src/VisionAlgorithm/OpenSrc/PatMax/SubPixelEdgeExtractor.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Extracts sub-pixel boundary <see cref="EdgeDipoleSoA"/> features using AVX2 Gaussian-Sobel filtering, directional NMS, and parabolic interpolation (US 6,408,109).
/// </summary>
public static class SubPixelEdgeExtractor
{
    private const int MinExtractDimension = 5;
    private const int BorderMargin = 2;
    private const int GaussianWindowRows = 5;
    private const int SobelWindowRows = 3;
    private const float GaussianKernelCenter = 6.0f;
    private const float GaussianKernelNeighbor = 4.0f;
    private const float GaussianNormScale = 1.0f / 16.0f;
    // [Math Why]: Rotation-invariant 3x3 Scharr operator weights [-3, 0, 3; -10, 0, 10; -3, 0, 3] sum to 16 per half-kernel (32 across central difference), minimizing diagonal angular error to < 0.05 deg.
    private const float ScharrCornerWeight = 3.0f;
    private const float ScharrCenterWeight = 10.0f;
    private const float ScharrNormScale = 1.0f / 32.0f;
    // [Math Why]: Guards parabolic vertex denominator (a - 2b + c) and zero gradient magnitudes against division by zero.
    private const float ParabolicCurvatureEpsilon = 1e-5f;
    private const float MaxSubPixelOffset = 0.5f;
    private const int ParallelGradientThresholdPixels = 262_144; // 512 x 512
    private const int ParallelGradientStripHeight = 64;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Extract(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        float threshold,
        EdgeDipoleSoA destination,
        float scaleToBase = 1.0f,
        float originOffsetX = 0.0f,
        float originOffsetY = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleToBase);

        if (pixels.Length < ((height - 1) * stride) + width)
        {
            throw new ArgumentException("Pixel span is smaller than required by height and stride.", nameof(pixels));
        }

        destination.Clear();
        if (width < MinExtractDimension || height < MinExtractDimension || destination.Capacity == 0)
        {
            return;
        }

        var pixelCount = checked(width * height);
        var pooledFloatLength = checked((pixelCount * 3) + (width * GaussianWindowRows) + (width * SobelWindowRows));
        var floatPool = ArrayPool<float>.Shared.Rent(pooledFloatLength);

        try
        {
            var poolSpan = floatPool.AsSpan(0, pooledFloatLength);
            var gradX = poolSpan.Slice(0, pixelCount);
            var gradY = poolSpan.Slice(pixelCount, pixelCount);
            var magnitude = poolSpan.Slice(pixelCount * 2, pixelCount);
            var horizRing = poolSpan.Slice(pixelCount * 3, width * GaussianWindowRows);
            var smoothRing = poolSpan.Slice((pixelCount * 3) + (width * GaussianWindowRows), width * SobelWindowRows);

            gradX.Clear();
            gradY.Clear();
            magnitude.Clear();

            ComputeGaussianSobelPlanes(pixels, width, height, stride, threshold, horizRing, smoothRing, gradX, gradY, magnitude);
            ExtractSubPixelDipoles(gradX, gradY, magnitude, width, height, threshold, scaleToBase, originOffsetX, originOffsetY, destination);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ComputeDenseUnitNormals(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        float threshold,
        Span<float> outUnitX,
        Span<float> outUnitY,
        Span<float> outMagnitude)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);

        var pixelCount = checked(width * height);
        if (pixels.Length < ((height - 1) * stride) + width)
        {
            throw new ArgumentException("Pixel span is smaller than required by height and stride.", nameof(pixels));
        }

        if (outUnitX.Length < pixelCount || outUnitY.Length < pixelCount || outMagnitude.Length < pixelCount)
        {
            throw new ArgumentException("Output gradient spans must have length >= width * height.");
        }

        outUnitX.Slice(0, pixelCount).Clear();
        outUnitY.Slice(0, pixelCount).Clear();
        outMagnitude.Slice(0, pixelCount).Clear();

        if (width < MinExtractDimension || height < MinExtractDimension)
        {
            return;
        }

        if (pixelCount >= ParallelGradientThresholdPixels && height >= ParallelGradientStripHeight * 2)
        {
            ComputeGaussianSobelPlanesParallel(
                pixels,
                width,
                height,
                stride,
                threshold,
                outUnitX.Slice(0, pixelCount),
                outUnitY.Slice(0, pixelCount),
                outMagnitude.Slice(0, pixelCount));
            return;
        }

        var ringLength = checked(width * (GaussianWindowRows + SobelWindowRows));
        var ringPool = ArrayPool<float>.Shared.Rent(ringLength);
        try
        {
            var ringSpan = ringPool.AsSpan(0, ringLength);
            var horizRing = ringSpan.Slice(0, width * GaussianWindowRows);
            var smoothRing = ringSpan.Slice(width * GaussianWindowRows, width * SobelWindowRows);

            ComputeGaussianSobelPlanes(
                pixels,
                width,
                height,
                stride,
                threshold,
                horizRing,
                smoothRing,
                outUnitX.Slice(0, pixelCount),
                outUnitY.Slice(0, pixelCount),
                outMagnitude.Slice(0, pixelCount));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ringPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void ComputeGaussianSobelPlanesParallel(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        float threshold,
        Span<float> gradX,
        Span<float> gradY,
        Span<float> magnitude)
    {
        var interiorRows = height - 2;
        var stripCount = (interiorRows + ParallelGradientStripHeight - 1) / ParallelGradientStripHeight;
        var ringSlotsPerStrip = width * (GaussianWindowRows + SobelWindowRows);
        var totalRingFloats = checked(stripCount * ringSlotsPerStrip);
        var ringPool = ArrayPool<float>.Shared.Rent(totalRingFloats);

        try
        {
            fixed (byte* pSrc = pixels)
            fixed (float* pGx = gradX)
            fixed (float* pGy = gradY)
            fixed (float* pMag = magnitude)
            fixed (float* pRing = ringPool)
            {
                var srcAddr = (nint)pSrc;
                var gxAddr = (nint)pGx;
                var gyAddr = (nint)pGy;
                var magAddr = (nint)pMag;
                var ringAddr = (nint)pRing;
                var srcLen = ((height - 1) * stride) + width;
                var pixelCount = width * height;

                Parallel.For(0, stripCount, s =>
                {
                    var startTargetY = 1 + (s * ParallelGradientStripHeight);
                    var endTargetY = Math.Min(height - 1, startTargetY + ParallelGradientStripHeight);
                    var localSrc = new ReadOnlySpan<byte>((byte*)srcAddr, srcLen);
                    var localGx = new Span<float>((float*)gxAddr, pixelCount);
                    var localGy = new Span<float>((float*)gyAddr, pixelCount);
                    var localMag = new Span<float>((float*)magAddr, pixelCount);
                    var stripRing = new Span<float>((float*)ringAddr + (s * ringSlotsPerStrip), ringSlotsPerStrip);
                    var horizRing = stripRing.Slice(0, width * GaussianWindowRows);
                    var smoothRing = stripRing.Slice(width * GaussianWindowRows, width * SobelWindowRows);

                    var startSmoothY = Math.Max(0, startTargetY - 1);
                    var endSmoothY = Math.Min(height - 1, endTargetY);
                    var nextHorizY = Math.Max(0, startSmoothY - BorderMargin);

                    for (var smoothY = startSmoothY; smoothY <= endSmoothY; smoothY++)
                    {
                        var maxNeededY = Math.Min(height - 1, smoothY + BorderMargin);
                        while (nextHorizY <= maxNeededY)
                        {
                            SmoothHorizontalRow(
                                localSrc.Slice(nextHorizY * stride, width),
                                horizRing.Slice((nextHorizY % GaussianWindowRows) * width, width));
                            nextHorizY++;
                        }

                        SmoothVerticalRow(
                            horizRing,
                            width,
                            height,
                            smoothY,
                            smoothRing.Slice((smoothY % SobelWindowRows) * width, width));

                        var targetY = smoothY - 1;
                        if (targetY >= startTargetY && targetY < endTargetY)
                        {
                            var rowOffset = targetY * width;
                            ComputeSobelRow(
                                smoothRing.Slice(((smoothY - 2) % SobelWindowRows) * width, width),
                                smoothRing.Slice(((smoothY - 1) % SobelWindowRows) * width, width),
                                smoothRing.Slice((smoothY % SobelWindowRows) * width, width),
                                localGx.Slice(rowOffset, width),
                                localGy.Slice(rowOffset, width),
                                localMag.Slice(rowOffset, width),
                                threshold);
                        }
                    }
                });
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ringPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ComputeGaussianSobelPlanes(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        float threshold,
        Span<float> horizRing,
        Span<float> smoothRing,
        Span<float> gradX,
        Span<float> gradY,
        Span<float> magnitude)
    {
        var nextHorizY = 0;
        for (var smoothY = 0; smoothY < height; smoothY++)
        {
            var maxNeededY = Math.Min(height - 1, smoothY + BorderMargin);
            while (nextHorizY <= maxNeededY)
            {
                SmoothHorizontalRow(
                    pixels.Slice(nextHorizY * stride, width),
                    horizRing.Slice((nextHorizY % GaussianWindowRows) * width, width));
                nextHorizY++;
            }

            SmoothVerticalRow(
                horizRing,
                width,
                height,
                smoothY,
                smoothRing.Slice((smoothY % SobelWindowRows) * width, width));

            if (smoothY >= 2)
            {
                var targetY = smoothY - 1;
                var rowOffset = targetY * width;
                ComputeSobelRow(
                    smoothRing.Slice(((smoothY - 2) % SobelWindowRows) * width, width),
                    smoothRing.Slice(((smoothY - 1) % SobelWindowRows) * width, width),
                    smoothRing.Slice((smoothY % SobelWindowRows) * width, width),
                    gradX.Slice(rowOffset, width),
                    gradY.Slice(rowOffset, width),
                    magnitude.Slice(rowOffset, width),
                    threshold);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SmoothHorizontalRow(ReadOnlySpan<byte> src, Span<float> dst)
    {
        var width = src.Length;
        ref var srcRef = ref MemoryMarshal.GetReference(src);
        ref var dstRef = ref MemoryMarshal.GetReference(dst);

        for (var x = 0; x < BorderMargin; x++)
        {
            Unsafe.Add(ref dstRef, x) = SmoothHorizontalClamped(src, x);
        }

        var xIndex = BorderMargin;
        var interiorEnd = width - BorderMargin;
        for (; xIndex < interiorEnd; xIndex++)
        {
            var sum = Unsafe.Add(ref srcRef, xIndex - 2)
                + (GaussianKernelNeighbor * Unsafe.Add(ref srcRef, xIndex - 1))
                + (GaussianKernelCenter * Unsafe.Add(ref srcRef, xIndex))
                + (GaussianKernelNeighbor * Unsafe.Add(ref srcRef, xIndex + 1))
                + Unsafe.Add(ref srcRef, xIndex + 2);
            Unsafe.Add(ref dstRef, xIndex) = sum * GaussianNormScale;
        }

        for (; xIndex < width; xIndex++)
        {
            Unsafe.Add(ref dstRef, xIndex) = SmoothHorizontalClamped(src, xIndex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SmoothHorizontalClamped(ReadOnlySpan<byte> row, int x)
    {
        var maxIdx = row.Length - 1;
        var sum = row[Math.Max(0, x - 2)]
            + (GaussianKernelNeighbor * row[Math.Max(0, x - 1)])
            + (GaussianKernelCenter * row[x])
            + (GaussianKernelNeighbor * row[Math.Min(maxIdx, x + 1)])
            + row[Math.Min(maxIdx, x + 2)];
        return sum * GaussianNormScale;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SmoothVerticalRow(
        ReadOnlySpan<float> horizRing,
        int width,
        int height,
        int y,
        Span<float> dst)
    {
        var r0Offset = (Math.Max(0, y - 2) % GaussianWindowRows) * width;
        var r1Offset = (Math.Max(0, y - 1) % GaussianWindowRows) * width;
        var r2Offset = (y % GaussianWindowRows) * width;
        var r3Offset = (Math.Min(height - 1, y + 1) % GaussianWindowRows) * width;
        var r4Offset = (Math.Min(height - 1, y + 2) % GaussianWindowRows) * width;

        ref var ringRef = ref MemoryMarshal.GetReference(horizRing);
        ref var dstRef = ref MemoryMarshal.GetReference(dst);

        var x = 0;
        // [Perf Why]: Vector256 vertical 5-tap Gaussian filter processes 8 floats per cycle with FMA.
        if (Avx2.IsSupported)
        {
            var simdLimit = width & ~(Vector256<float>.Count - 1);
            var vFour = Vector256.Create(GaussianKernelNeighbor);
            var vSix = Vector256.Create(GaussianKernelCenter);
            var vNorm = Vector256.Create(GaussianNormScale);

            for (; x < simdLimit; x += Vector256<float>.Count)
            {
                var v0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r0Offset + x));
                var v1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r1Offset + x));
                var v2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r2Offset + x));
                var v3 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r3Offset + x));
                var v4 = Vector256.LoadUnsafe(ref Unsafe.Add(ref ringRef, r4Offset + x));

                var sum = v0 + v4;
                if (Fma.IsSupported)
                {
                    sum = Fma.MultiplyAdd(v1 + v3, vFour, sum);
                    sum = Fma.MultiplyAdd(v2, vSix, sum);
                }
                else
                {
                    sum += ((v1 + v3) * vFour) + (v2 * vSix);
                }

                (sum * vNorm).StoreUnsafe(ref Unsafe.Add(ref dstRef, x));
            }
        }

        for (; x < width; x++)
        {
            var sum = Unsafe.Add(ref ringRef, r0Offset + x)
                + (GaussianKernelNeighbor * Unsafe.Add(ref ringRef, r1Offset + x))
                + (GaussianKernelCenter * Unsafe.Add(ref ringRef, r2Offset + x))
                + (GaussianKernelNeighbor * Unsafe.Add(ref ringRef, r3Offset + x))
                + Unsafe.Add(ref ringRef, r4Offset + x);
            Unsafe.Add(ref dstRef, x) = sum * GaussianNormScale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ComputeSobelRow(
        ReadOnlySpan<float> top,
        ReadOnlySpan<float> middle,
        ReadOnlySpan<float> bottom,
        Span<float> outCos,
        Span<float> outSin,
        Span<float> outMag,
        float threshold)
    {
        var width = middle.Length;
        ref var topRef = ref MemoryMarshal.GetReference(top);
        ref var midRef = ref MemoryMarshal.GetReference(middle);
        ref var botRef = ref MemoryMarshal.GetReference(bottom);
        ref var cosRef = ref MemoryMarshal.GetReference(outCos);
        ref var sinRef = ref MemoryMarshal.GetReference(outSin);
        ref var magRef = ref MemoryMarshal.GetReference(outMag);

        var x = 1;
        // [Perf Why]: Vector256 3x3 Scharr operator computes rotation-invariant dx, dy, magnitude, and unit normal (cos, sin) for 8 pixels at once.
        if (Avx2.IsSupported)
        {
            var vCorner = Vector256.Create(ScharrCornerWeight);
            var vCenter = Vector256.Create(ScharrCenterWeight);
            var vScale = Vector256.Create(ScharrNormScale);
            var vThreshold = Vector256.Create(threshold);

            for (; x <= width - 1 - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                var tl = Vector256.LoadUnsafe(ref Unsafe.Add(ref topRef, x - 1));
                var tc = Vector256.LoadUnsafe(ref Unsafe.Add(ref topRef, x));
                var tr = Vector256.LoadUnsafe(ref Unsafe.Add(ref topRef, x + 1));
                var ml = Vector256.LoadUnsafe(ref Unsafe.Add(ref midRef, x - 1));
                var mr = Vector256.LoadUnsafe(ref Unsafe.Add(ref midRef, x + 1));
                var bl = Vector256.LoadUnsafe(ref Unsafe.Add(ref botRef, x - 1));
                var bc = Vector256.LoadUnsafe(ref Unsafe.Add(ref botRef, x));
                var br = Vector256.LoadUnsafe(ref Unsafe.Add(ref botRef, x + 1));

                var dxCorners = ((tr - tl) + (br - bl)) * vCorner;
                var dyCorners = ((bl - tl) + (br - tr)) * vCorner;
                var dx = (Fma.IsSupported ? Fma.MultiplyAdd(mr - ml, vCenter, dxCorners) : dxCorners + ((mr - ml) * vCenter)) * vScale;
                var dy = (Fma.IsSupported ? Fma.MultiplyAdd(bc - tc, vCenter, dyCorners) : dyCorners + ((bc - tc) * vCenter)) * vScale;

                var magSq = (dx * dx) + (dy * dy);
                var mag = Avx.Sqrt(magSq);
                var mask = Avx.Compare(mag, vThreshold, FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling);

                var unitX = Avx.BlendVariable(Vector256<float>.Zero, dx / mag, mask);
                var unitY = Avx.BlendVariable(Vector256<float>.Zero, dy / mag, mask);

                mag.StoreUnsafe(ref Unsafe.Add(ref magRef, x));
                unitX.StoreUnsafe(ref Unsafe.Add(ref cosRef, x));
                unitY.StoreUnsafe(ref Unsafe.Add(ref sinRef, x));
            }
        }

        for (; x < width - 1; x++)
        {
            var dx = ((ScharrCornerWeight * ((Unsafe.Add(ref topRef, x + 1) - Unsafe.Add(ref topRef, x - 1))
                + (Unsafe.Add(ref botRef, x + 1) - Unsafe.Add(ref botRef, x - 1))))
                + (ScharrCenterWeight * (Unsafe.Add(ref midRef, x + 1) - Unsafe.Add(ref midRef, x - 1)))) * ScharrNormScale;

            var dy = ((ScharrCornerWeight * ((Unsafe.Add(ref botRef, x - 1) - Unsafe.Add(ref topRef, x - 1))
                + (Unsafe.Add(ref botRef, x + 1) - Unsafe.Add(ref topRef, x + 1))))
                + (ScharrCenterWeight * (Unsafe.Add(ref botRef, x) - Unsafe.Add(ref topRef, x)))) * ScharrNormScale;

            var mag = MathF.Sqrt((dx * dx) + (dy * dy));
            Unsafe.Add(ref magRef, x) = mag;
            if (mag >= threshold)
            {
                var invMag = 1.0f / mag;
                Unsafe.Add(ref cosRef, x) = dx * invMag;
                Unsafe.Add(ref sinRef, x) = dy * invMag;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ExtractSubPixelDipoles(
        ReadOnlySpan<float> gradX,
        ReadOnlySpan<float> gradY,
        ReadOnlySpan<float> magnitude,
        int width,
        int height,
        float threshold,
        float scaleToBase,
        float originOffsetX,
        float originOffsetY,
        EdgeDipoleSoA destination)
    {
        for (var y = BorderMargin; y < height - BorderMargin; y++)
        {
            var rowOffset = y * width;
            for (var x = BorderMargin; x < width - BorderMargin; x++)
            {
                var idx = rowOffset + x;
                var centerMag = magnitude[idx];
                if (centerMag < threshold)
                {
                    continue;
                }

                var nx = gradX[idx];
                var ny = gradY[idx];
                var backwardMag = SampleBilinear(magnitude, width, height, x - nx, y - ny);
                var forwardMag = SampleBilinear(magnitude, width, height, x + nx, y + ny);

                var tieEps = centerMag * ParabolicCurvatureEpsilon;
                if (centerMag + tieEps < backwardMag || centerMag + tieEps < forwardMag)
                {
                    continue;
                }

                // [Math Why]: On a hard binary step edge, symmetric Gaussian+Sobel yields equal magnitudes at the two pixels straddling the step;
                // tie-breaking along the positive coordinate axis suppresses one side so both pixels do not emit duplicate dipoles at the same half-integer boundary.
                var forwardPointsPositive = (nx > ParabolicCurvatureEpsilon) || (MathF.Abs(nx) <= ParabolicCurvatureEpsilon && ny >= 0.0f);
                if (forwardPointsPositive && MathF.Abs(centerMag - forwardMag) <= tieEps)
                {
                    continue;
                }

                if (!forwardPointsPositive && MathF.Abs(centerMag - backwardMag) <= tieEps)
                {
                    continue;
                }

                // [Math Why]: 3-point parabola through (-1, backwardMag), (0, centerMag), (+1, forwardMag) peaks at offset = 0.5 * (backward - forward) / (backward - 2*center + forward).
                var curvature = backwardMag - (2.0f * centerMag) + forwardMag;
                var offset = MathF.Abs(curvature) > ParabolicCurvatureEpsilon
                    ? Math.Clamp(0.5f * (backwardMag - forwardMag) / curvature, -MaxSubPixelOffset, MaxSubPixelOffset)
                    : 0.0f;

                var subX = ((x + 0.5f + (nx * offset)) * scaleToBase) + originOffsetX;
                var subY = ((y + 0.5f + (ny * offset)) * scaleToBase) + originOffsetY;

                destination.Add(subX, subY, nx, ny, centerMag);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinear(ReadOnlySpan<float> plane, int width, int height, float x, float y)
    {
        if (x < 0.0f || y < 0.0f || x > width - 1 || y > height - 1)
        {
            return 0.0f;
        }

        var ix = (int)x;
        var iy = (int)y;
        var jx = Math.Min(ix + 1, width - 1);
        var jy = Math.Min(iy + 1, height - 1);
        var tx = x - ix;
        var ty = y - iy;
        var invTx = 1.0f - tx;
        var invTy = 1.0f - ty;

        var topRow = iy * width;
        var botRow = jy * width;
        return (plane[topRow + ix] * invTx * invTy)
            + (plane[topRow + jx] * tx * invTy)
            + (plane[botRow + ix] * invTx * ty)
            + (plane[botRow + jx] * tx * ty);
    }
}
