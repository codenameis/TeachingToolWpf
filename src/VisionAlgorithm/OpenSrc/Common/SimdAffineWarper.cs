// File: src/VisionAlgorithm/OpenSrc/Common/SimdAffineWarper.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Unified 1-pass rotation- and anisotropic-scale-rectified ROI warper using exact pixel-center convention
/// ($(x + 0.5, y + 0.5)$) and AVX2-accelerated bilinear interpolation with scalar fallback.
/// </summary>
public static class SimdAffineWarper
{
    // [Perf Why]: AVX2 processes 8 float lanes per iteration.
    private const int Avx2LaneCount = 8;

    private static readonly Vector256<float> LaneOffsets = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);

    /// <summary>
    /// Warps a rectangular region from an 8-bit <see cref="ReadOnlyImageView2D{T}"/> centered at <paramref name="fixturePose"/>
    /// into <paramref name="destination"/> of dimensions <paramref name="roiWidth"/> $\times$ <paramref name="roiHeight"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WarpAffineBilinear(
        ReadOnlyImageView2D<byte> sourceView,
        in FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
    {
        WarpRectifiedRoi(sourceView, fixturePose, roiWidth, roiHeight, destination);
    }

    /// <summary>
    /// Warps a rectangular region from an 8-bit <see cref="ReadOnlyImageView2D{T}"/> centered at <paramref name="fixturePose"/>
    /// into an 8-bit <paramref name="destination"/> of dimensions <paramref name="roiWidth"/> $\times$ <paramref name="roiHeight"/>.
    /// </summary>
    public static void WarpAffineBilinear(
        ReadOnlyImageView2D<byte> sourceView,
        in FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<byte> destination,
        Span<float> floatScratch)
    {
        int len = checked(roiWidth * roiHeight);
        Span<float> temp = floatScratch.Slice(0, len);
        WarpRectifiedRoi(sourceView, fixturePose, roiWidth, roiHeight, temp);
        for (int i = 0; i < len; i++)
        {
            destination[i] = (byte)Math.Clamp((int)MathF.Round(temp[i]), 0, 255);
        }
    }

    /// <summary>
    /// Warps a rectangular region from an 8-bit <see cref="ReadOnlyImageView2D{T}"/> centered at <paramref name="fixturePose"/>
    /// into <paramref name="destination"/> of dimensions <paramref name="roiWidth"/> $\times$ <paramref name="roiHeight"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WarpRectifiedRoi(
        ReadOnlyImageView2D<byte> sourceView,
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
    {
        sourceView.EnsureValid();
        WarpRectifiedRoi(
            sourceView.Pixels,
            sourceView.Width,
            sourceView.Height,
            sourceView.Stride,
            fixturePose,
            roiWidth,
            roiHeight,
            destination);
    }

    /// <summary>
    /// Warps a rectangular region from a 32-bit float <see cref="ReadOnlyImageView2D{T}"/> centered at <paramref name="fixturePose"/>
    /// into <paramref name="destination"/> of dimensions <paramref name="roiWidth"/> $\times$ <paramref name="roiHeight"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WarpRectifiedRoi(
        ReadOnlyImageView2D<float> sourceView,
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
    {
        sourceView.EnsureValid();
        WarpRectifiedRoi(
            sourceView.Pixels,
            sourceView.Width,
            sourceView.Height,
            sourceView.Stride,
            fixturePose,
            roiWidth,
            roiHeight,
            destination);
    }

    /// <summary>
    /// Warps a rectangular region from an 8-bit source image centered at <paramref name="fixturePose"/>
    /// into <paramref name="destination"/> of dimensions <paramref name="roiWidth"/> $\times$ <paramref name="roiHeight"/>.
    /// </summary>
    public static void WarpRectifiedRoi(
        ReadOnlySpan<byte> sourceImage,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
    {
        fixturePose.EnsureValid();
        ValidateInputs(sourceImage.Length, sourceWidth, sourceHeight, sourceStride, roiWidth, roiHeight, destination.Length);

        ComputeAffineSteps(
            fixturePose,
            roiWidth,
            roiHeight,
            out float baseWorldX,
            out float baseWorldY,
            out float stepX,
            out float stepY,
            out float rowStepX,
            out float rowStepY);

        int maxX = sourceWidth - 1;
        int maxY = sourceHeight - 1;

        ref byte srcRef = ref MemoryMarshal.GetReference(sourceImage);
        ref float dstRef = ref MemoryMarshal.GetReference(destination);

        if (Avx2.IsSupported && roiWidth >= Avx2LaneCount)
        {
            WarpByteAvx2(ref srcRef, sourceStride, maxX, maxY, baseWorldX, baseWorldY, stepX, stepY, rowStepX, rowStepY, roiWidth, roiHeight, ref dstRef);
            return;
        }

        WarpByteScalar(ref srcRef, sourceStride, maxX, maxY, baseWorldX, baseWorldY, stepX, stepY, rowStepX, rowStepY, roiWidth, roiHeight, ref dstRef);
    }

    /// <summary>
    /// Warps a rectangular region from a 32-bit float source image centered at <paramref name="fixturePose"/>
    /// into <paramref name="destination"/> of dimensions <paramref name="roiWidth"/> $\times$ <paramref name="roiHeight"/>.
    /// </summary>
    public static void WarpRectifiedRoi(
        ReadOnlySpan<float> sourceImage,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
    {
        fixturePose.EnsureValid();
        ValidateInputs(sourceImage.Length, sourceWidth, sourceHeight, sourceStride, roiWidth, roiHeight, destination.Length);

        ComputeAffineSteps(
            fixturePose,
            roiWidth,
            roiHeight,
            out float baseWorldX,
            out float baseWorldY,
            out float stepX,
            out float stepY,
            out float rowStepX,
            out float rowStepY);

        int maxX = sourceWidth - 1;
        int maxY = sourceHeight - 1;

        ref float srcRef = ref MemoryMarshal.GetReference(sourceImage);
        ref float dstRef = ref MemoryMarshal.GetReference(destination);

        if (Avx2.IsSupported && roiWidth >= Avx2LaneCount)
        {
            WarpFloatAvx2(ref srcRef, sourceStride, maxX, maxY, baseWorldX, baseWorldY, stepX, stepY, rowStepX, rowStepY, roiWidth, roiHeight, ref dstRef);
            return;
        }

        WarpFloatScalar(ref srcRef, sourceStride, maxX, maxY, baseWorldX, baseWorldY, stepX, stepY, rowStepX, rowStepY, roiWidth, roiHeight, ref dstRef);
    }

    /// <summary>
    /// Alias for <see cref="WarpRectifiedRoi(ReadOnlyImageView2D{byte}, FixturePose2D, int, int, Span{float})"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WarpAffineBilinear(
        ReadOnlyImageView2D<byte> sourceView,
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
        => WarpRectifiedRoi(sourceView, fixturePose, roiWidth, roiHeight, destination);

    /// <summary>
    /// Alias for <see cref="WarpRectifiedRoi(ReadOnlySpan{byte}, int, int, int, FixturePose2D, int, int, Span{float})"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WarpAffineBilinear(
        ReadOnlySpan<byte> sourceImage,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        Span<float> destination)
        => WarpRectifiedRoi(sourceImage, sourceWidth, sourceHeight, sourceStride, fixturePose, roiWidth, roiHeight, destination);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateInputs(
        int sourceLength,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        int roiWidth,
        int roiHeight,
        int destinationLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, sourceWidth);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiHeight, 0);

        long requiredSourceElements = ((long)(sourceHeight - 1) * sourceStride) + sourceWidth;
        if (sourceLength < requiredSourceElements)
        {
            throw new ArgumentException("Source buffer is smaller than the specified dimensions and stride.");
        }

        int totalOutputPixels = checked(roiWidth * roiHeight);
        if (destinationLength < totalOutputPixels)
        {
            throw new ArgumentException("Destination buffer is smaller than roiWidth * roiHeight.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeAffineSteps(
        FixturePose2D fixturePose,
        int roiWidth,
        int roiHeight,
        out float baseWorldX,
        out float baseWorldY,
        out float stepX,
        out float stepY,
        out float rowStepX,
        out float rowStepY)
    {
        stepX = fixturePose.ScaleX * fixturePose.CosTheta;
        stepY = fixturePose.ScaleX * fixturePose.SinTheta;
        rowStepX = -fixturePose.ScaleY * fixturePose.SinTheta;
        rowStepY = fixturePose.ScaleY * fixturePose.CosTheta;

        // [Math Why]: Local pixel u in [0, roiWidth - 1] has center offset (u + 0.5) - (roiWidth * 0.5).
        // Subtracting 0.5 from continuous world coordinates maps pixel center (c + 0.5, r + 0.5) to array index (c, r).
        float localStartX = 0.5f - (roiWidth * 0.5f);
        float localStartY = 0.5f - (roiHeight * 0.5f);
        baseWorldX = (fixturePose.CenterX - 0.5f) + (localStartX * stepX) + (localStartY * rowStepX);
        baseWorldY = (fixturePose.CenterY - 0.5f) + (localStartX * stepY) + (localStartY * rowStepY);
    }

    private static void WarpByteAvx2(
        ref byte srcRef,
        int sourceStride,
        int maxX,
        int maxY,
        float baseWorldX,
        float baseWorldY,
        float stepX,
        float stepY,
        float rowStepX,
        float rowStepY,
        int roiWidth,
        int roiHeight,
        ref float dstRef)
    {
        Vector256<float> vStepX = Vector256.Create(stepX);
        Vector256<float> vStepY = Vector256.Create(stepY);
        Vector256<float> vZero = Vector256<float>.Zero;
        Vector256<float> vOne = Vector256.Create(1f);
        Vector256<float> vMaxX = Vector256.Create((float)maxX);
        Vector256<float> vMaxY = Vector256.Create((float)maxY);
        Vector256<int> vZeroInt = Vector256<int>.Zero;
        Vector256<int> vMaxXInt = Vector256.Create(maxX);
        Vector256<int> vMaxYInt = Vector256.Create(maxY);

        Span<int> idxX0 = stackalloc int[Avx2LaneCount];
        Span<int> idxX1 = stackalloc int[Avx2LaneCount];
        Span<int> idxY0 = stackalloc int[Avx2LaneCount];
        Span<int> idxY1 = stackalloc int[Avx2LaneCount];
        Span<float> p00Buf = stackalloc float[Avx2LaneCount];
        Span<float> p10Buf = stackalloc float[Avx2LaneCount];
        Span<float> p01Buf = stackalloc float[Avx2LaneCount];
        Span<float> p11Buf = stackalloc float[Avx2LaneCount];

        int simdLimit = roiWidth - (roiWidth % Avx2LaneCount);

        for (int v = 0; v < roiHeight; v++)
        {
            float rowStartX = baseWorldX + (v * rowStepX);
            float rowStartY = baseWorldY + (v * rowStepY);
            int dstRowOffset = v * roiWidth;

            int u = 0;
            for (; u < simdLimit; u += Avx2LaneCount)
            {
                Vector256<float> vU = Avx2.Add(Vector256.Create((float)u), LaneOffsets);
                Vector256<float> vSx = Avx2.Add(Vector256.Create(rowStartX), Avx2.Multiply(vU, vStepX));
                Vector256<float> vSy = Avx2.Add(Vector256.Create(rowStartY), Avx2.Multiply(vU, vStepY));

                vSx = Avx2.Min(Avx2.Max(vSx, vZero), vMaxX);
                vSy = Avx2.Min(Avx2.Max(vSy, vZero), vMaxY);

                Vector256<float> vFloorX = Avx.Floor(vSx);
                Vector256<float> vFloorY = Avx.Floor(vSy);
                Vector256<float> vFx = Avx2.Subtract(vSx, vFloorX);
                Vector256<float> vFy = Avx2.Subtract(vSy, vFloorY);
                Vector256<float> vOneMinusFx = Avx2.Subtract(vOne, vFx);
                Vector256<float> vOneMinusFy = Avx2.Subtract(vOne, vFy);

                Vector256<int> vX0 = Avx2.Min(Avx2.Max(Avx2.ConvertToVector256Int32WithTruncation(vFloorX), vZeroInt), vMaxXInt);
                Vector256<int> vY0 = Avx2.Min(Avx2.Max(Avx2.ConvertToVector256Int32WithTruncation(vFloorY), vZeroInt), vMaxYInt);
                Vector256<int> vX1 = Avx2.Min(Avx2.Add(vX0, Vector256.Create(1)), vMaxXInt);
                Vector256<int> vY1 = Avx2.Min(Avx2.Add(vY0, Vector256.Create(1)), vMaxYInt);

                vX0.CopyTo(idxX0);
                vX1.CopyTo(idxX1);
                vY0.CopyTo(idxY0);
                vY1.CopyTo(idxY1);

                for (int lane = 0; lane < Avx2LaneCount; lane++)
                {
                    int r0 = idxY0[lane] * sourceStride;
                    int r1 = idxY1[lane] * sourceStride;
                    int c0 = idxX0[lane];
                    int c1 = idxX1[lane];

                    p00Buf[lane] = Unsafe.Add(ref srcRef, r0 + c0);
                    p10Buf[lane] = Unsafe.Add(ref srcRef, r0 + c1);
                    p01Buf[lane] = Unsafe.Add(ref srcRef, r1 + c0);
                    p11Buf[lane] = Unsafe.Add(ref srcRef, r1 + c1);
                }

                Vector256<float> vP00 = Vector256.Create<float>(p00Buf);
                Vector256<float> vP10 = Vector256.Create<float>(p10Buf);
                Vector256<float> vP01 = Vector256.Create<float>(p01Buf);
                Vector256<float> vP11 = Vector256.Create<float>(p11Buf);

                Vector256<float> vTop = Avx2.Add(Avx2.Multiply(vP00, vOneMinusFx), Avx2.Multiply(vP10, vFx));
                Vector256<float> vBottom = Avx2.Add(Avx2.Multiply(vP01, vOneMinusFx), Avx2.Multiply(vP11, vFx));
                Vector256<float> vVal = Avx2.Add(Avx2.Multiply(vTop, vOneMinusFy), Avx2.Multiply(vBottom, vFy));

                Unsafe.WriteUnaligned(
                    ref Unsafe.As<float, byte>(ref Unsafe.Add(ref dstRef, dstRowOffset + u)),
                    vVal);
            }

            for (; u < roiWidth; u++)
            {
                float sx = Math.Clamp(rowStartX + (u * stepX), 0f, maxX);
                float sy = Math.Clamp(rowStartY + (u * stepY), 0f, maxY);
                Unsafe.Add(ref dstRef, dstRowOffset + u) = SampleBilinearByteScalar(ref srcRef, sourceStride, maxX, maxY, sx, sy);
            }
        }
    }

    private static void WarpFloatAvx2(
        ref float srcRef,
        int sourceStride,
        int maxX,
        int maxY,
        float baseWorldX,
        float baseWorldY,
        float stepX,
        float stepY,
        float rowStepX,
        float rowStepY,
        int roiWidth,
        int roiHeight,
        ref float dstRef)
    {
        Vector256<float> vStepX = Vector256.Create(stepX);
        Vector256<float> vStepY = Vector256.Create(stepY);
        Vector256<float> vZero = Vector256<float>.Zero;
        Vector256<float> vOne = Vector256.Create(1f);
        Vector256<float> vMaxX = Vector256.Create((float)maxX);
        Vector256<float> vMaxY = Vector256.Create((float)maxY);
        Vector256<int> vZeroInt = Vector256<int>.Zero;
        Vector256<int> vMaxXInt = Vector256.Create(maxX);
        Vector256<int> vMaxYInt = Vector256.Create(maxY);

        Span<int> idxX0 = stackalloc int[Avx2LaneCount];
        Span<int> idxX1 = stackalloc int[Avx2LaneCount];
        Span<int> idxY0 = stackalloc int[Avx2LaneCount];
        Span<int> idxY1 = stackalloc int[Avx2LaneCount];
        Span<float> p00Buf = stackalloc float[Avx2LaneCount];
        Span<float> p10Buf = stackalloc float[Avx2LaneCount];
        Span<float> p01Buf = stackalloc float[Avx2LaneCount];
        Span<float> p11Buf = stackalloc float[Avx2LaneCount];

        int simdLimit = roiWidth - (roiWidth % Avx2LaneCount);

        for (int v = 0; v < roiHeight; v++)
        {
            float rowStartX = baseWorldX + (v * rowStepX);
            float rowStartY = baseWorldY + (v * rowStepY);
            int dstRowOffset = v * roiWidth;

            int u = 0;
            for (; u < simdLimit; u += Avx2LaneCount)
            {
                Vector256<float> vU = Avx2.Add(Vector256.Create((float)u), LaneOffsets);
                Vector256<float> vSx = Avx2.Add(Vector256.Create(rowStartX), Avx2.Multiply(vU, vStepX));
                Vector256<float> vSy = Avx2.Add(Vector256.Create(rowStartY), Avx2.Multiply(vU, vStepY));

                vSx = Avx2.Min(Avx2.Max(vSx, vZero), vMaxX);
                vSy = Avx2.Min(Avx2.Max(vSy, vZero), vMaxY);

                Vector256<float> vFloorX = Avx.Floor(vSx);
                Vector256<float> vFloorY = Avx.Floor(vSy);
                Vector256<float> vFx = Avx2.Subtract(vSx, vFloorX);
                Vector256<float> vFy = Avx2.Subtract(vSy, vFloorY);
                Vector256<float> vOneMinusFx = Avx2.Subtract(vOne, vFx);
                Vector256<float> vOneMinusFy = Avx2.Subtract(vOne, vFy);

                Vector256<int> vX0 = Avx2.Min(Avx2.Max(Avx2.ConvertToVector256Int32WithTruncation(vFloorX), vZeroInt), vMaxXInt);
                Vector256<int> vY0 = Avx2.Min(Avx2.Max(Avx2.ConvertToVector256Int32WithTruncation(vFloorY), vZeroInt), vMaxYInt);
                Vector256<int> vX1 = Avx2.Min(Avx2.Add(vX0, Vector256.Create(1)), vMaxXInt);
                Vector256<int> vY1 = Avx2.Min(Avx2.Add(vY0, Vector256.Create(1)), vMaxYInt);

                vX0.CopyTo(idxX0);
                vX1.CopyTo(idxX1);
                vY0.CopyTo(idxY0);
                vY1.CopyTo(idxY1);

                for (int lane = 0; lane < Avx2LaneCount; lane++)
                {
                    int r0 = idxY0[lane] * sourceStride;
                    int r1 = idxY1[lane] * sourceStride;
                    int c0 = idxX0[lane];
                    int c1 = idxX1[lane];

                    p00Buf[lane] = Unsafe.Add(ref srcRef, r0 + c0);
                    p10Buf[lane] = Unsafe.Add(ref srcRef, r0 + c1);
                    p01Buf[lane] = Unsafe.Add(ref srcRef, r1 + c0);
                    p11Buf[lane] = Unsafe.Add(ref srcRef, r1 + c1);
                }

                Vector256<float> vP00 = Vector256.Create<float>(p00Buf);
                Vector256<float> vP10 = Vector256.Create<float>(p10Buf);
                Vector256<float> vP01 = Vector256.Create<float>(p01Buf);
                Vector256<float> vP11 = Vector256.Create<float>(p11Buf);

                Vector256<float> vTop = Avx2.Add(Avx2.Multiply(vP00, vOneMinusFx), Avx2.Multiply(vP10, vFx));
                Vector256<float> vBottom = Avx2.Add(Avx2.Multiply(vP01, vOneMinusFx), Avx2.Multiply(vP11, vFx));
                Vector256<float> vVal = Avx2.Add(Avx2.Multiply(vTop, vOneMinusFy), Avx2.Multiply(vBottom, vFy));

                Unsafe.WriteUnaligned(
                    ref Unsafe.As<float, byte>(ref Unsafe.Add(ref dstRef, dstRowOffset + u)),
                    vVal);
            }

            for (; u < roiWidth; u++)
            {
                float sx = Math.Clamp(rowStartX + (u * stepX), 0f, maxX);
                float sy = Math.Clamp(rowStartY + (u * stepY), 0f, maxY);
                Unsafe.Add(ref dstRef, dstRowOffset + u) = SampleBilinearFloatScalar(ref srcRef, sourceStride, maxX, maxY, sx, sy);
            }
        }
    }

    private static void WarpByteScalar(
        ref byte srcRef,
        int sourceStride,
        int maxX,
        int maxY,
        float baseWorldX,
        float baseWorldY,
        float stepX,
        float stepY,
        float rowStepX,
        float rowStepY,
        int roiWidth,
        int roiHeight,
        ref float dstRef)
    {
        for (int v = 0; v < roiHeight; v++)
        {
            float rowStartX = baseWorldX + (v * rowStepX);
            float rowStartY = baseWorldY + (v * rowStepY);
            int dstRowOffset = v * roiWidth;

            for (int u = 0; u < roiWidth; u++)
            {
                float sx = Math.Clamp(rowStartX + (u * stepX), 0f, maxX);
                float sy = Math.Clamp(rowStartY + (u * stepY), 0f, maxY);
                Unsafe.Add(ref dstRef, dstRowOffset + u) = SampleBilinearByteScalar(ref srcRef, sourceStride, maxX, maxY, sx, sy);
            }
        }
    }

    private static void WarpFloatScalar(
        ref float srcRef,
        int sourceStride,
        int maxX,
        int maxY,
        float baseWorldX,
        float baseWorldY,
        float stepX,
        float stepY,
        float rowStepX,
        float rowStepY,
        int roiWidth,
        int roiHeight,
        ref float dstRef)
    {
        for (int v = 0; v < roiHeight; v++)
        {
            float rowStartX = baseWorldX + (v * rowStepX);
            float rowStartY = baseWorldY + (v * rowStepY);
            int dstRowOffset = v * roiWidth;

            for (int u = 0; u < roiWidth; u++)
            {
                float sx = Math.Clamp(rowStartX + (u * stepX), 0f, maxX);
                float sy = Math.Clamp(rowStartY + (u * stepY), 0f, maxY);
                Unsafe.Add(ref dstRef, dstRowOffset + u) = SampleBilinearFloatScalar(ref srcRef, sourceStride, maxX, maxY, sx, sy);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinearByteScalar(
        ref byte srcRef,
        int sourceStride,
        int maxX,
        int maxY,
        float sx,
        float sy)
    {
        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        float fx = sx - x0;
        float fy = sy - y0;

        int cx0 = Math.Clamp(x0, 0, maxX);
        int cy0 = Math.Clamp(y0, 0, maxY);
        int cx1 = Math.Min(cx0 + 1, maxX);
        int cy1 = Math.Min(cy0 + 1, maxY);

        int r0 = cy0 * sourceStride;
        int r1 = cy1 * sourceStride;

        float p00 = Unsafe.Add(ref srcRef, r0 + cx0);
        float p10 = Unsafe.Add(ref srcRef, r0 + cx1);
        float p01 = Unsafe.Add(ref srcRef, r1 + cx0);
        float p11 = Unsafe.Add(ref srcRef, r1 + cx1);

        float top = p00 + ((p10 - p00) * fx);
        float bottom = p01 + ((p11 - p01) * fx);
        return top + ((bottom - top) * fy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinearFloatScalar(
        ref float srcRef,
        int sourceStride,
        int maxX,
        int maxY,
        float sx,
        float sy)
    {
        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        float fx = sx - x0;
        float fy = sy - y0;

        int cx0 = Math.Clamp(x0, 0, maxX);
        int cy0 = Math.Clamp(y0, 0, maxY);
        int cx1 = Math.Min(cx0 + 1, maxX);
        int cy1 = Math.Min(cy0 + 1, maxY);

        int r0 = cy0 * sourceStride;
        int r1 = cy1 * sourceStride;

        float p00 = Unsafe.Add(ref srcRef, r0 + cx0);
        float p10 = Unsafe.Add(ref srcRef, r0 + cx1);
        float p01 = Unsafe.Add(ref srcRef, r1 + cx0);
        float p11 = Unsafe.Add(ref srcRef, r1 + cx1);

        float top = p00 + ((p10 - p00) * fx);
        float bottom = p01 + ((p11 - p01) * fx);
        return top + ((bottom - top) * fy);
    }
}
