// File: src/VisionOcr.PpOcr/PpOcrPreprocessor.cs
using System;
using System.Buffers;
using System.Runtime.Intrinsics;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionOcr.PpOcr;

/// <summary>
/// Official PaddleOCR PP-OCRv4 preprocessing routines for DBNet detection and SVTR-LCNet recognition,
/// with pooled zero-allocation batch tensor builders and <see cref="Vector256{T}"/> SIMD planar normalization.
/// </summary>
public static class PpOcrPreprocessor
{
    public const float RecNormalizeOffset = 0.5f;
    public const float RecNormalizeScale = 0.5f;
    public const float ByteMaxReciprocal = 1.0f / 255.0f;

    // [Math Why]: Official ImageNet RGB mean/std constants used by PP-OCRv4 DBNet detector.
    public const float DetMeanChannel0 = 0.485f;
    public const float DetMeanChannel1 = 0.456f;
    public const float DetMeanChannel2 = 0.406f;
    public const float DetStdChannel0 = 0.229f;
    public const float DetStdChannel1 = 0.224f;
    public const float DetStdChannel2 = 0.225f;

    private const int ChannelCount = 3;
    private const int SecondChannelPlaneMultiplier = 2;
    private const int MinUpscaleTriggerSidePx = 64;
    private const int MaxBorderPadPixels = 64;
    private const int GrayHistogramBinCount = 256;
    private const int Vector256FloatCount = 8;
    private const int Vector256ByteCount = 32;
    private const float SmallRoiUpscaleFactor = 2.0f;
    private const float InvRecNormalizeScale = 1.0f / RecNormalizeScale; // [Math Why]: 1 / 0.5 == 2.0f, exact power-of-two IEEE-754 exponent shift
    private const byte MaxPixelValue = byte.MaxValue;

    /// <summary>
    /// Computes the aspect-preserved resized width (at height 48) and 32-aligned target tensor width for recognition.
    /// </summary>
    public static void ComputeRecWidths(
        int sourceWidth,
        int sourceHeight,
        out int targetWidth,
        out int resizedContentWidth)
    {
        int targetH = PpOcrRunOptions.OfficialRecImageHeight;
        float aspectRatio = sourceWidth / (float)Math.Max(1, sourceHeight);
        resizedContentWidth = Math.Clamp(
            (int)MathF.Ceiling(targetH * aspectRatio),
            1,
            PpOcrRunOptions.DefaultRecMaxWidth);
        int bucketed = AlignUpToStep(resizedContentWidth, PpOcrRunOptions.RecWidthBucketStep);
        targetWidth = Math.Clamp(
            Math.Max(PpOcrRunOptions.DefaultRecMinWidth, bucketed),
            PpOcrRunOptions.DefaultRecMinWidth,
            PpOcrRunOptions.DefaultRecMaxWidth);
    }

    /// <summary>
    /// Builds an official <c>[1, 3, 48, targetWidth]</c> NCHW recognition tensor:
    /// aspect-preserving bilinear resize to height 48, <c>(x / 255 - 0.5) / 0.5</c>,
    /// 3-channel grayscale replication, and zero right-padding.
    /// </summary>
    public static float[] BuildRecInputTensor(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        out int targetWidth,
        out int resizedContentWidth)
    {
        if (!roiView.IsValid || roiView.Width <= 0 || roiView.Height <= 0)
        {
            throw new ArgumentException("입력 ROI 영상이 비어 있습니다.", nameof(roiView));
        }

        int srcW = roiView.Width;
        int srcH = roiView.Height;
        int targetH = PpOcrRunOptions.OfficialRecImageHeight;

        float aspectRatio = srcW / (float)srcH;
        resizedContentWidth = Math.Clamp(
            (int)MathF.Ceiling(targetH * aspectRatio),
            1,
            PpOcrRunOptions.DefaultRecMaxWidth);
        targetWidth = Math.Max(PpOcrRunOptions.DefaultRecMinWidth, resizedContentWidth);

        int planeLen = targetH * targetWidth;
        float[] tensor = new float[ChannelCount * planeLen];
        FillRecBatchSliceInto(roiView, invertPolarity, targetWidth, resizedContentWidth, tensor.AsSpan(0, ChannelCount * planeLen));
        return tensor;
    }

    /// <summary>
    /// Writes a single crop's normalized <c>[3, 48, batchTargetWidth]</c> NCHW planes directly into a pre-rented batch buffer slice,
    /// zero-filling right-padding columns <c>[resizedContentWidth .. batchTargetWidth)</c>.
    /// </summary>
    internal static void FillRecBatchSliceInto(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        int batchTargetWidth,
        int resizedContentWidth,
        Span<float> destinationSlice)
    {
        int targetH = PpOcrRunOptions.OfficialRecImageHeight;
        int planeLen = targetH * batchTargetWidth;

        using Mat srcMat = CreateContiguousGrayMat(roiView, invertPolarity);
        using var resizedMat = new Mat();
        Cv2.Resize(srcMat, resizedMat, new Size(resizedContentWidth, targetH), 0, 0, InterpolationFlags.Linear);

        Span<float> plane0 = destinationSlice.Slice(0, planeLen);
        Span<float> plane1 = destinationSlice.Slice(planeLen, planeLen);
        Span<float> plane2 = destinationSlice.Slice(SecondChannelPlaneMultiplier * planeLen, planeLen);
        int padWidth = batchTargetWidth - resizedContentWidth;

        unsafe
        {
            byte* basePtr = resizedMat.DataPointer;
            long step = resizedMat.Step();
            for (int y = 0; y < targetH; y++)
            {
                ReadOnlySpan<byte> srcRow = new(basePtr + (y * step), resizedContentWidth);
                int dstRowOffset = y * batchTargetWidth;
                Span<float> row0 = plane0.Slice(dstRowOffset, resizedContentWidth);
                Span<float> row1 = plane1.Slice(dstRowOffset, resizedContentWidth);
                Span<float> row2 = plane2.Slice(dstRowOffset, resizedContentWidth);

                if (Vector256.IsHardwareAccelerated && resizedContentWidth >= Vector256FloatCount)
                {
                    NormalizeRecRowKernelSimd(srcRow, row0, row1, row2);
                }
                else
                {
                    NormalizeRecRowKernelScalar(srcRow, row0, row1, row2);
                }

                if (padWidth > 0)
                {
                    int padOffset = dstRowOffset + resizedContentWidth;
                    plane0.Slice(padOffset, padWidth).Clear();
                    plane1.Slice(padOffset, padWidth).Clear();
                    plane2.Slice(padOffset, padWidth).Clear();
                }
            }
        }
    }

    /// <summary>
    /// Builds an official <c>[1, 3, H32, W32]</c> NCHW DBNet detection tensor with border-median padding
    /// and 32-multiple spatial alignment.
    /// </summary>
    public static float[] BuildDetInputTensor(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        DbDetOptions options,
        out int tensorWidth,
        out int tensorHeight,
        out float scaleX,
        out float scaleY,
        out int borderPadPx)
    {
        float[] rented = RentDetInputTensor(
            roiView,
            invertPolarity,
            options,
            out int totalFloats,
            out tensorWidth,
            out tensorHeight,
            out scaleX,
            out scaleY,
            out borderPadPx);
        try
        {
            float[] exact = new float[totalFloats];
            rented.AsSpan(0, totalFloats).CopyTo(exact);
            return exact;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Rents an <see cref="ArrayPool{T}"/> buffer and populates the <c>[1, 3, H32, W32]</c> DBNet detection tensor with zero heap allocation on steady state.
    /// </summary>
    internal static float[] RentDetInputTensor(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        DbDetOptions options,
        out int totalLength,
        out int tensorWidth,
        out int tensorHeight,
        out float scaleX,
        out float scaleY,
        out int borderPadPx)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!roiView.IsValid || roiView.Width <= 0 || roiView.Height <= 0)
        {
            throw new ArgumentException("입력 ROI 영상이 비어 있습니다.", nameof(roiView));
        }

        borderPadPx = Math.Clamp(options.BorderPadPx, 0, MaxBorderPadPixels);
        using Mat rawMat = CreateContiguousGrayMat(roiView, invertPolarity);
        byte borderGray = EstimateBorderMedian(rawMat);

        using var paddedMat = new Mat();
        if (borderPadPx > 0)
        {
            Cv2.CopyMakeBorder(
                rawMat,
                paddedMat,
                borderPadPx,
                borderPadPx,
                borderPadPx,
                borderPadPx,
                BorderTypes.Constant,
                new Scalar(borderGray));
        }
        else
        {
            rawMat.CopyTo(paddedMat);
        }

        int paddedW = paddedMat.Width;
        int paddedH = paddedMat.Height;
        int limitSide = Math.Max(DbDetOptions.StrideAlignment, options.LimitSideLen);

        float ratio = 1.0f;
        int minSide = Math.Min(paddedW, paddedH);
        int maxSide = Math.Max(paddedW, paddedH);
        if (minSide < MinUpscaleTriggerSidePx && maxSide * SmallRoiUpscaleFactor <= limitSide)
        {
            ratio = SmallRoiUpscaleFactor;
        }
        else if (maxSide > limitSide)
        {
            ratio = limitSide / (float)maxSide;
        }

        tensorWidth = AlignToStride(Math.Max(DbDetOptions.StrideAlignment, (int)MathF.Round(paddedW * ratio)));
        tensorHeight = AlignToStride(Math.Max(DbDetOptions.StrideAlignment, (int)MathF.Round(paddedH * ratio)));
        scaleX = tensorWidth / (float)paddedW;
        scaleY = tensorHeight / (float)paddedH;

        using var resizedMat = new Mat();
        Cv2.Resize(paddedMat, resizedMat, new Size(tensorWidth, tensorHeight), 0, 0, InterpolationFlags.Linear);

        int planeLen = tensorHeight * tensorWidth;
        totalLength = ChannelCount * planeLen;
        float[] tensor = ArrayPool<float>.Shared.Rent(totalLength);

        Span<float> plane0 = tensor.AsSpan(0, planeLen);
        Span<float> plane1 = tensor.AsSpan(planeLen, planeLen);
        Span<float> plane2 = tensor.AsSpan(SecondChannelPlaneMultiplier * planeLen, planeLen);

        unsafe
        {
            byte* basePtr = resizedMat.DataPointer;
            long step = resizedMat.Step();
            for (int y = 0; y < tensorHeight; y++)
            {
                ReadOnlySpan<byte> srcRow = new(basePtr + (y * step), tensorWidth);
                int dstRowOffset = y * tensorWidth;
                Span<float> row0 = plane0.Slice(dstRowOffset, tensorWidth);
                Span<float> row1 = plane1.Slice(dstRowOffset, tensorWidth);
                Span<float> row2 = plane2.Slice(dstRowOffset, tensorWidth);

                if (Vector256.IsHardwareAccelerated && tensorWidth >= Vector256FloatCount)
                {
                    NormalizeDetRowKernelSimd(srcRow, row0, row1, row2);
                }
                else
                {
                    NormalizeDetRowKernelScalar(srcRow, row0, row1, row2);
                }
            }
        }

        return tensor;
    }

    internal static Mat CreateContiguousGrayMat(ReadOnlyImageView2D<byte> view, bool invertPolarity)
    {
        int width = view.Width;
        int height = view.Height;
        var mat = new Mat(height, width, MatType.CV_8UC1);
        ReadOnlySpan<byte> src = view.Pixels;
        int srcStride = view.Stride;

        unsafe
        {
            byte* dstBase = mat.DataPointer;
            long dstStep = mat.Step();
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> srcRow = src.Slice(y * srcStride, width);
                Span<byte> dstRow = new(dstBase + (y * dstStep), width);
                if (!invertPolarity)
                {
                    srcRow.CopyTo(dstRow);
                }
                else if (Vector256.IsHardwareAccelerated && width >= Vector256ByteCount)
                {
                    InvertRowKernelSimd(srcRow, dstRow);
                }
                else
                {
                    InvertRowKernelScalar(srcRow, dstRow);
                }
            }
        }

        return mat;
    }

    private static void NormalizeRecRowKernelSimd(
        ReadOnlySpan<byte> srcRow,
        Span<float> dstPlane0,
        Span<float> dstPlane1,
        Span<float> dstPlane2)
    {
        // [Perf Why]: Vector256 widens 8 grayscale bytes at once and writes identical normalized floats to all 3 planar channels.
        Vector256<float> vRecip = Vector256.Create(ByteMaxReciprocal);
        Vector256<float> vOffset = Vector256.Create(RecNormalizeOffset);
        Vector256<float> vInvScale = Vector256.Create(InvRecNormalizeScale);

        int x = 0;
        int simdLimit = srcRow.Length - (srcRow.Length % Vector256FloatCount);
        for (; x < simdLimit; x += Vector256FloatCount)
        {
            Vector256<float> vPixels = Vector256.Create(
                (float)srcRow[x],
                srcRow[x + 1],
                srcRow[x + 2],
                srcRow[x + 3],
                srcRow[x + 4],
                srcRow[x + 5],
                srcRow[x + 6],
                srcRow[x + 7]);

            Vector256<float> vNorm = ((vPixels * vRecip) - vOffset) * vInvScale;
            vNorm.CopyTo(dstPlane0.Slice(x, Vector256FloatCount));
            vNorm.CopyTo(dstPlane1.Slice(x, Vector256FloatCount));
            vNorm.CopyTo(dstPlane2.Slice(x, Vector256FloatCount));
        }

        if (x < srcRow.Length)
        {
            NormalizeRecRowKernelScalar(
                srcRow.Slice(x),
                dstPlane0.Slice(x),
                dstPlane1.Slice(x),
                dstPlane2.Slice(x));
        }
    }

    private static void NormalizeRecRowKernelScalar(
        ReadOnlySpan<byte> srcRow,
        Span<float> dstPlane0,
        Span<float> dstPlane1,
        Span<float> dstPlane2)
    {
        for (int x = 0; x < srcRow.Length; x++)
        {
            float norm = ((srcRow[x] * ByteMaxReciprocal) - RecNormalizeOffset) / RecNormalizeScale;
            dstPlane0[x] = norm;
            dstPlane1[x] = norm;
            dstPlane2[x] = norm;
        }
    }

    private static void NormalizeDetRowKernelSimd(
        ReadOnlySpan<byte> srcRow,
        Span<float> dstPlane0,
        Span<float> dstPlane1,
        Span<float> dstPlane2)
    {
        Vector256<float> vRecip = Vector256.Create(ByteMaxReciprocal);
        Vector256<float> vMean0 = Vector256.Create(DetMeanChannel0);
        Vector256<float> vMean1 = Vector256.Create(DetMeanChannel1);
        Vector256<float> vMean2 = Vector256.Create(DetMeanChannel2);
        Vector256<float> vInvStd0 = Vector256.Create(1.0f / DetStdChannel0);
        Vector256<float> vInvStd1 = Vector256.Create(1.0f / DetStdChannel1);
        Vector256<float> vInvStd2 = Vector256.Create(1.0f / DetStdChannel2);

        int x = 0;
        int simdLimit = srcRow.Length - (srcRow.Length % Vector256FloatCount);
        for (; x < simdLimit; x += Vector256FloatCount)
        {
            Vector256<float> vPixels = Vector256.Create(
                (float)srcRow[x],
                srcRow[x + 1],
                srcRow[x + 2],
                srcRow[x + 3],
                srcRow[x + 4],
                srcRow[x + 5],
                srcRow[x + 6],
                srcRow[x + 7]);

            Vector256<float> vUnit = vPixels * vRecip;
            ((vUnit - vMean0) * vInvStd0).CopyTo(dstPlane0.Slice(x, Vector256FloatCount));
            ((vUnit - vMean1) * vInvStd1).CopyTo(dstPlane1.Slice(x, Vector256FloatCount));
            ((vUnit - vMean2) * vInvStd2).CopyTo(dstPlane2.Slice(x, Vector256FloatCount));
        }

        if (x < srcRow.Length)
        {
            NormalizeDetRowKernelScalar(
                srcRow.Slice(x),
                dstPlane0.Slice(x),
                dstPlane1.Slice(x),
                dstPlane2.Slice(x));
        }
    }

    private static void NormalizeDetRowKernelScalar(
        ReadOnlySpan<byte> srcRow,
        Span<float> dstPlane0,
        Span<float> dstPlane1,
        Span<float> dstPlane2)
    {
        float invStd0 = 1.0f / DetStdChannel0;
        float invStd1 = 1.0f / DetStdChannel1;
        float invStd2 = 1.0f / DetStdChannel2;

        for (int x = 0; x < srcRow.Length; x++)
        {
            float unit = srcRow[x] * ByteMaxReciprocal;
            dstPlane0[x] = (unit - DetMeanChannel0) * invStd0;
            dstPlane1[x] = (unit - DetMeanChannel1) * invStd1;
            dstPlane2[x] = (unit - DetMeanChannel2) * invStd2;
        }
    }

    private static void InvertRowKernelSimd(ReadOnlySpan<byte> srcRow, Span<byte> dstRow)
    {
        // [Math Why]: For unsigned 8-bit integers, 255 - x is bit-identical to bitwise complement (~x).
        int x = 0;
        int simdLimit = srcRow.Length - (srcRow.Length % Vector256ByteCount);
        for (; x < simdLimit; x += Vector256ByteCount)
        {
            Vector256<byte> v = Vector256.Create<byte>(srcRow.Slice(x, Vector256ByteCount));
            Vector256.OnesComplement(v).CopyTo(dstRow.Slice(x, Vector256ByteCount));
        }

        if (x < srcRow.Length)
        {
            InvertRowKernelScalar(srcRow.Slice(x), dstRow.Slice(x));
        }
    }

    private static void InvertRowKernelScalar(ReadOnlySpan<byte> srcRow, Span<byte> dstRow)
    {
        for (int x = 0; x < srcRow.Length; x++)
        {
            dstRow[x] = (byte)(MaxPixelValue - srcRow[x]);
        }
    }

    private static int AlignUpToStep(int value, int step)
    {
        int remainder = value % step;
        return remainder == 0 ? value : value + (step - remainder);
    }

    private static int AlignToStride(int length)
    {
        int remainder = length % DbDetOptions.StrideAlignment;
        if (remainder == 0)
        {
            return Math.Max(DbDetOptions.StrideAlignment, length);
        }

        int rounded = remainder >= (DbDetOptions.StrideAlignment / 2)
            ? length + (DbDetOptions.StrideAlignment - remainder)
            : length - remainder;
        return Math.Max(DbDetOptions.StrideAlignment, rounded);
    }

    private static byte EstimateBorderMedian(Mat mat)
    {
        int width = mat.Width;
        int height = mat.Height;
        Span<int> hist = stackalloc int[GrayHistogramBinCount];
        int total = 0;

        unsafe
        {
            byte* ptr = mat.DataPointer;
            long step = mat.Step();
            for (int x = 0; x < width; x++)
            {
                hist[ptr[x]]++;
                hist[(ptr + ((height - 1) * step))[x]]++;
                total += 2;
            }

            for (int y = 1; y < height - 1; y++)
            {
                byte* row = ptr + (y * step);
                hist[row[0]]++;
                hist[row[width - 1]]++;
                total += 2;
            }
        }

        int half = total / 2;
        int cumulative = 0;
        for (int i = 0; i < GrayHistogramBinCount; i++)
        {
            cumulative += hist[i];
            if (cumulative >= half)
            {
                return (byte)i;
            }
        }

        return (byte)(GrayHistogramBinCount / 2);
    }
}
