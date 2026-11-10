// File: src/VisionAlgorithm/OpenSrc/Common/RoiImageProcessor.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Stateless ImageJ-style ROI spatial filter processor, auto-threshold resolver,
/// and 3x3 neighborhood pixel probe kernel.
/// </summary>
public static class RoiImageProcessor
{
    private const int NeighborhoodWindowSize = 9;
    private const int MedianCenterRank = 4;
    private const int RollingBallKernelRadius = 4;
    private const float LowerStretchQuantile = 0.01f;
    private const float UpperStretchQuantile = 0.99f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ResolveThreshold(
        in RoiHistogramAnalysis analysis,
        ImageJAutoThresholdMethod method,
        byte manualThreshold) => method switch
        {
            ImageJAutoThresholdMethod.Otsu => analysis.OtsuThreshold,
            ImageJAutoThresholdMethod.Triangle => analysis.TriangleThreshold,
            ImageJAutoThresholdMethod.IsoData => analysis.IsoDataThreshold,
            ImageJAutoThresholdMethod.MaxEntropy => analysis.MaxEntropyThreshold,
            ImageJAutoThresholdMethod.Mean => analysis.MeanThreshold,
            ImageJAutoThresholdMethod.Percentile => analysis.MedianGray,
            _ => manualThreshold
        };

    /// <summary>
    /// Probes the pixel at <paramref name="x"/>, <paramref name="y"/> and its 3x3 neighborhood
    /// for live ImageJ status-bar readout (Gray, 3x3 Mean, Min, Max, Sobel Gradient).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryProbePixel(
        ReadOnlyImageView2D<byte> image,
        int x,
        int y,
        out byte centerGray,
        out float mean3x3,
        out byte min3x3,
        out byte max3x3,
        out float gradientMagnitude)
    {
        centerGray = 0;
        mean3x3 = 0.0f;
        min3x3 = 0;
        max3x3 = 0;
        gradientMagnitude = 0.0f;

        if (!image.IsValid || x < 0 || y < 0 || x >= image.Width || y >= image.Height)
        {
            return false;
        }

        ReadOnlySpan<byte> px = image.Pixels;
        int stride = image.Stride;
        int w = image.Width;
        int h = image.Height;

        centerGray = px[(y * stride) + x];
        min3x3 = byte.MaxValue;
        max3x3 = byte.MinValue;
        int sum = 0;

        int y0 = Math.Max(0, y - 1);
        int y1 = Math.Min(h - 1, y + 1);
        int x0 = Math.Max(0, x - 1);
        int x1 = Math.Min(w - 1, x + 1);

        for (int ny = y0; ny <= y1; ny++)
        {
            for (int nx = x0; nx <= x1; nx++)
            {
                byte v = px[(ny * stride) + nx];
                sum += v;
                if (v < min3x3)
                {
                    min3x3 = v;
                }

                if (v > max3x3)
                {
                    max3x3 = v;
                }
            }
        }

        int count = (y1 - y0 + 1) * (x1 - x0 + 1);
        mean3x3 = (float)sum / Math.Max(1, count);

        byte p00 = px[(y0 * stride) + x0];
        byte p01 = px[(y0 * stride) + x];
        byte p02 = px[(y0 * stride) + x1];
        byte p10 = px[(y * stride) + x0];
        byte p12 = px[(y * stride) + x1];
        byte p20 = px[(y1 * stride) + x0];
        byte p21 = px[(y1 * stride) + x];
        byte p22 = px[(y1 * stride) + x1];

        int gx = (p02 + (2 * p12) + p22) - (p00 + (2 * p10) + p20);
        int gy = (p20 + (2 * p21) + p22) - (p00 + (2 * p01) + p02);
        gradientMagnitude = MathF.Sqrt((gx * gx) + (gy * gy)) * 0.25f;
        return true;
    }

    /// <summary>
    /// Creates a full-frame image copy with the specified <see cref="RoiPreFilterKind"/> applied inside the target ROI rectangle.
    /// If <paramref name="filter"/> is <see cref="RoiPreFilterKind.None"/>, returns the original view without copying.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ReadOnlyImageView2D<byte> ApplyRoiFilterIfNeeded(
        ReadOnlyImageView2D<byte> source,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        RoiPreFilterKind filter)
    {
        source.EnsureValid();
        if (filter == RoiPreFilterKind.None)
        {
            return source;
        }

        byte[] copy = source.Pixels.ToArray();
        ApplyFilterInPlaceOnRoi(source, copy, source.Stride, roiX, roiY, roiWidth, roiHeight, filter);
        return new ReadOnlyImageView2D<byte>(copy, source.Width, source.Height, source.Stride);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyFilterInPlaceOnRoi(
        ReadOnlyImageView2D<byte> source,
        Span<byte> destinationFullBuffer,
        int dstStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        RoiPreFilterKind filter)
    {
        if (filter == RoiPreFilterKind.None)
        {
            return;
        }

        int clampedX = Math.Clamp(roiX, 0, Math.Max(0, source.Width - 1));
        int clampedY = Math.Clamp(roiY, 0, Math.Max(0, source.Height - 1));
        int clampedW = Math.Clamp(roiWidth, 1, source.Width - clampedX);
        int clampedH = Math.Clamp(roiHeight, 1, source.Height - clampedY);

        switch (filter)
        {
            case RoiPreFilterKind.Gaussian3x3:
                ApplyGaussian3x3(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;

            case RoiPreFilterKind.Median3x3:
                ApplyMedian3x3(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;

            case RoiPreFilterKind.SobelGradient:
                ApplySobelGradient(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;

            case RoiPreFilterKind.UnsharpSharpen3x3:
                ApplyUnsharpSharpen3x3(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;

            case RoiPreFilterKind.MorphErode3x3:
                ApplyGrayMorphology3x3(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH, dilate: false);
                break;

            case RoiPreFilterKind.MorphDilate3x3:
                ApplyGrayMorphology3x3(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH, dilate: true);
                break;

            case RoiPreFilterKind.MorphOpen3x3:
            {
                byte[] temp = source.Pixels.ToArray();
                ApplyGrayMorphology3x3(source, temp, source.Stride, clampedX, clampedY, clampedW, clampedH, dilate: false);
                var erodedView = new ReadOnlyImageView2D<byte>(temp, source.Width, source.Height, source.Stride);
                ApplyGrayMorphology3x3(erodedView, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH, dilate: true);
                break;
            }

            case RoiPreFilterKind.MorphClose3x3:
            {
                byte[] temp = source.Pixels.ToArray();
                ApplyGrayMorphology3x3(source, temp, source.Stride, clampedX, clampedY, clampedW, clampedH, dilate: true);
                var dilatedView = new ReadOnlyImageView2D<byte>(temp, source.Width, source.Height, source.Stride);
                ApplyGrayMorphology3x3(dilatedView, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH, dilate: false);
                break;
            }

            case RoiPreFilterKind.RollingBallTopHat:
                ApplyRollingBallTopHat(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;

            case RoiPreFilterKind.Invert:
                ApplyInvert(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;

            case RoiPreFilterKind.EnhanceContrastStretch:
                ApplyContrastStretch(source, destinationFullBuffer, dstStride, clampedX, clampedY, clampedW, clampedH);
                break;
        }
    }

    private static void ApplyGaussian3x3(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        int w = src.Width;
        int h = src.Height;

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - 1);
            int y1 = Math.Min(h - 1, y + 1);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(w - 1, x + 1);

                int sum =
                    px[(y0 * stride) + x0] + (2 * px[(y0 * stride) + x]) + px[(y0 * stride) + x1] +
                    (2 * px[(y * stride) + x0]) + (4 * px[(y * stride) + x]) + (2 * px[(y * stride) + x1]) +
                    px[(y1 * stride) + x0] + (2 * px[(y1 * stride) + x]) + px[(y1 * stride) + x1];

                dstRow[c] = (byte)((sum + 8) >> 4);
            }
        }
    }

    private static void ApplyMedian3x3(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        int w = src.Width;
        int h = src.Height;
        Span<byte> win = stackalloc byte[NeighborhoodWindowSize];

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - 1);
            int y1 = Math.Min(h - 1, y + 1);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(w - 1, x + 1);

                win[0] = px[(y0 * stride) + x0];
                win[1] = px[(y0 * stride) + x];
                win[2] = px[(y0 * stride) + x1];
                win[3] = px[(y * stride) + x0];
                win[4] = px[(y * stride) + x];
                win[5] = px[(y * stride) + x1];
                win[6] = px[(y1 * stride) + x0];
                win[7] = px[(y1 * stride) + x];
                win[8] = px[(y1 * stride) + x1];

                // Insertion sort on 9 elements
                for (int i = 1; i < NeighborhoodWindowSize; i++)
                {
                    byte key = win[i];
                    int j = i - 1;
                    while (j >= 0 && win[j] > key)
                    {
                        win[j + 1] = win[j];
                        j--;
                    }

                    win[j + 1] = key;
                }

                dstRow[c] = win[MedianCenterRank];
            }
        }
    }

    private static void ApplySobelGradient(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        int w = src.Width;
        int h = src.Height;

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - 1);
            int y1 = Math.Min(h - 1, y + 1);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(w - 1, x + 1);

                byte p00 = px[(y0 * stride) + x0];
                byte p01 = px[(y0 * stride) + x];
                byte p02 = px[(y0 * stride) + x1];
                byte p10 = px[(y * stride) + x0];
                byte p12 = px[(y * stride) + x1];
                byte p20 = px[(y1 * stride) + x0];
                byte p21 = px[(y1 * stride) + x];
                byte p22 = px[(y1 * stride) + x1];

                int gx = (p02 + (2 * p12) + p22) - (p00 + (2 * p10) + p20);
                int gy = (p20 + (2 * p21) + p22) - (p00 + (2 * p01) + p02);
                int mag = (int)MathF.Round(MathF.Sqrt((gx * gx) + (gy * gy)));
                dstRow[c] = (byte)Math.Clamp(mag, byte.MinValue, byte.MaxValue);
            }
        }
    }

    private static void ApplyUnsharpSharpen3x3(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        int w = src.Width;
        int h = src.Height;

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - 1);
            int y1 = Math.Min(h - 1, y + 1);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(w - 1, x + 1);

                int center = px[(y * stride) + x];
                int laplacian = (5 * center)
                    - px[(y0 * stride) + x]
                    - px[(y1 * stride) + x]
                    - px[(y * stride) + x0]
                    - px[(y * stride) + x1];

                dstRow[c] = (byte)Math.Clamp(laplacian, byte.MinValue, byte.MaxValue);
            }
        }
    }

    private static void ApplyGrayMorphology3x3(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh,
        bool dilate)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        int w = src.Width;
        int h = src.Height;

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - 1);
            int y1 = Math.Min(h - 1, y + 1);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(w - 1, x + 1);

                byte extremum = dilate ? byte.MinValue : byte.MaxValue;
                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        byte v = px[(ny * stride) + nx];
                        if (dilate)
                        {
                            if (v > extremum)
                            {
                                extremum = v;
                            }
                        }
                        else
                        {
                            if (v < extremum)
                            {
                                extremum = v;
                            }
                        }
                    }
                }

                dstRow[c] = extremum;
            }
        }
    }

    private static void ApplyRollingBallTopHat(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        int w = src.Width;
        int h = src.Height;

        // White Top-Hat background subtraction: I - Open_R(I)
        var eroded = new byte[rw * rh];
        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - RollingBallKernelRadius);
            int y1 = Math.Min(h - 1, y + RollingBallKernelRadius);
            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - RollingBallKernelRadius);
                int x1 = Math.Min(w - 1, x + RollingBallKernelRadius);
                byte minVal = byte.MaxValue;
                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        byte v = px[(ny * stride) + nx];
                        if (v < minVal)
                        {
                            minVal = v;
                        }
                    }
                }

                eroded[(r * rw) + c] = minVal;
            }
        }

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int r0 = Math.Max(0, r - RollingBallKernelRadius);
            int r1 = Math.Min(rh - 1, r + RollingBallKernelRadius);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int c0 = Math.Max(0, c - RollingBallKernelRadius);
                int c1 = Math.Min(rw - 1, c + RollingBallKernelRadius);
                byte bgOpen = byte.MinValue;
                for (int nr = r0; nr <= r1; nr++)
                {
                    for (int nc = c0; nc <= c1; nc++)
                    {
                        byte v = eroded[(nr * rw) + nc];
                        if (v > bgOpen)
                        {
                            bgOpen = v;
                        }
                    }
                }

                int orig = px[(y * stride) + rx + c];
                dstRow[c] = (byte)Math.Clamp(orig - bgOpen, byte.MinValue, byte.MaxValue);
            }
        }
    }

    private static void ApplyInvert(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            ReadOnlySpan<byte> srcRow = px.Slice((y * stride) + rx, rw);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);
            for (int c = 0; c < rw; c++)
            {
                dstRow[c] = (byte)(byte.MaxValue - srcRow[c]);
            }
        }
    }

    private static void ApplyContrastStretch(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        ReadOnlySpan<byte> px = src.Pixels;
        int stride = src.Stride;
        Span<int> hist = stackalloc int[byte.MaxValue + 1];
        int total = rw * rh;

        for (int r = 0; r < rh; r++)
        {
            ReadOnlySpan<byte> srcRow = px.Slice(((ry + r) * stride) + rx, rw);
            for (int c = 0; c < rw; c++)
            {
                hist[srcRow[c]]++;
            }
        }

        int lowTarget = Math.Max(1, (int)(total * LowerStretchQuantile));
        int highTarget = Math.Max(lowTarget, (int)(total * UpperStretchQuantile));
        int cum = 0;
        int lowBin = 0;
        int highBin = byte.MaxValue;

        for (int i = 0; i <= byte.MaxValue; i++)
        {
            cum += hist[i];
            if (cum >= lowTarget)
            {
                lowBin = i;
                break;
            }
        }

        cum = 0;
        for (int i = 0; i <= byte.MaxValue; i++)
        {
            cum += hist[i];
            if (cum >= highTarget)
            {
                highBin = i;
                break;
            }
        }

        float scale = highBin > lowBin ? 255.0f / (highBin - lowBin) : 1.0f;
        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            ReadOnlySpan<byte> srcRow = px.Slice((y * stride) + rx, rw);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);
            for (int c = 0; c < rw; c++)
            {
                float mapped = (srcRow[c] - lowBin) * scale;
                dstRow[c] = (byte)Math.Clamp((int)MathF.Round(mapped), byte.MinValue, byte.MaxValue);
            }
        }
    }
}
