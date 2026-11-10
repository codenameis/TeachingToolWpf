// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJWorkbenchEngine.cs
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Core ImageJ image-processing, spatial/morphological filtering, LUT/contrast mapping,
/// ROI measurement (<c>Ctrl+M</c>), and particle analysis engine.
/// </summary>
public sealed class ImageJWorkbenchEngine : IDisposable
{
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const int GrayBinCount = byte.MaxValue + 1;
    private const int Window3x3Length = 9;
    private const int Median3x3Rank = 4;
    private const int RollingBallRadius = 5;
    private const float QuarterScale = 0.25f;
    private const float LowerAutoContrastQuantile = 0.01f;
    private const float UpperAutoContrastQuantile = 0.99f;

    private readonly InspectionWorkspace _workspace = new();
    private bool _disposed;

    public ImageJPixelProbeResult ProbePixel(ReadOnlyImageView2D<byte> image, int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!image.IsValid || x < 0 || y < 0 || x >= image.Width || y >= image.Height)
        {
            return ImageJPixelProbeResult.OutOfBounds(x, y);
        }

        ReadOnlySpan<byte> px = image.Pixels;
        int stride = image.Stride;
        int w = image.Width;
        int h = image.Height;

        byte centerGv = px[(y * stride) + x];
        byte min3x3 = byte.MaxValue;
        byte max3x3 = byte.MinValue;
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
        float mean3x3 = (float)sum / Math.Max(1, count);

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
        float sobel = MathF.Sqrt((gx * gx) + (gy * gy)) * QuarterScale;

        return new ImageJPixelProbeResult(
            IsInsideImage: true,
            X: x,
            Y: y,
            GrayValue: centerGv,
            Mean3x3: mean3x3,
            Min3x3: min3x3,
            Max3x3: max3x3,
            SobelGradient: sobel);
    }

    public (byte MinThreshold, byte MaxThreshold) ResolveEffectiveThresholds(
        ReadOnlyImageView2D<byte> image,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        ImageJThresholdSpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!spec.IsInitialized)
        {
            throw new ArgumentException("Threshold spec must be initialized.", nameof(spec));
        }

        byte baseMin = spec.MinThreshold;
        byte baseMax = spec.MaxThreshold;

        if (spec.Method != ImageJAutoThresholdMethod.Manual)
        {
            RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(image, roiX, roiY, roiWidth, roiHeight);
            byte autoVal = RoiImageProcessor.ResolveThreshold(in hist, spec.Method, baseMin);
            if (spec.IsDarkBackground)
            {
                return (autoVal, byte.MaxValue);
            }

            return (byte.MinValue, autoVal);
        }

        if (!spec.IsDarkBackground && baseMax == byte.MaxValue)
        {
            return (byte.MinValue, baseMin);
        }

        return (baseMin, baseMax);
    }

    public ImageJMeasurementRecord MeasureRoi(
        ReadOnlyImageView2D<byte> image,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        int measurementNumber,
        string label,
        ImageJThresholdSpec thresholdSpec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        image.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (!thresholdSpec.IsInitialized)
        {
            throw new ArgumentException("Threshold spec must be initialized.", nameof(thresholdSpec));
        }

        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, image.Width, image.Height);
        (byte minT, byte maxT) = ResolveEffectiveThresholds(image, cx, cy, cw, ch, thresholdSpec);
        RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(image, cx, cy, cw, ch, minT, maxT);

        // Compute intensity-weighted Center of Mass (XM, YM) in world coordinates
        ReadOnlySpan<byte> px = image.Pixels;
        int stride = image.Stride;
        double weightedXSum = 0.0;
        double weightedYSum = 0.0;
        double intensitySum = 0.0;

        for (int r = 0; r < ch; r++)
        {
            int wy = cy + r;
            ReadOnlySpan<byte> row = px.Slice((wy * stride) + cx, cw);
            for (int c = 0; c < cw; c++)
            {
                byte gv = row[c];
                weightedXSum += (cx + c + 0.5) * gv;
                weightedYSum += (wy + 0.5) * gv;
                intensitySum += gv;
            }
        }

        float geomCentroidX = cx + (cw * 0.5f);
        float geomCentroidY = cy + (ch * 0.5f);
        float comX = intensitySum > 1e-6 ? (float)(weightedXSum / intensitySum) : geomCentroidX;
        float comY = intensitySum > 1e-6 ? (float)(weightedYSum / intensitySum) : geomCentroidY;
        double integratedDensity = hist.TotalPixelCount * (double)hist.MeanGray;

        return new ImageJMeasurementRecord(
            MeasurementNumber: measurementNumber,
            Label: label,
            RoiX: cx,
            RoiY: cy,
            RoiWidth: cw,
            RoiHeight: ch,
            AreaPixels: hist.TotalPixelCount,
            MeanGray: hist.MeanGray,
            StdDevGray: hist.StdDevGray,
            MinGray: hist.MinGray,
            MaxGray: hist.MaxGray,
            ModeGray: hist.ModeGray,
            MedianGray: hist.MedianGray,
            IntegratedDensity: integratedDensity,
            CentroidX: geomCentroidX,
            CentroidY: geomCentroidY,
            CenterOfMassX: comX,
            CenterOfMassY: comY,
            Skewness: hist.Skewness,
            Kurtosis: hist.Kurtosis,
            EntropyBits: hist.EntropyBits,
            OtsuThreshold: hist.OtsuThreshold,
            ThresholdedAreaPercent: hist.ThresholdedRatioPercent);
    }

    public IReadOnlyList<ImageJParticleRecord> AnalyzeParticles(
        ReadOnlyImageView2D<byte> image,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        ImageJThresholdSpec thresholdSpec,
        ImageJParticleFilterSpec filterSpec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        image.EnsureValid();

        if (!thresholdSpec.IsInitialized)
        {
            throw new ArgumentException("Threshold spec must be initialized.", nameof(thresholdSpec));
        }

        if (!filterSpec.IsInitialized)
        {
            throw new ArgumentException("Particle filter spec must be initialized.", nameof(filterSpec));
        }

        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, image.Width, image.Height);
        (byte minT, byte maxT) = ResolveEffectiveThresholds(image, cx, cy, cw, ch, thresholdSpec);

        var crop = new byte[cw * ch];
        ReadOnlySpan<byte> src = image.Pixels;
        for (int r = 0; r < ch; r++)
        {
            src.Slice(((cy + r) * image.Stride) + cx, cw).CopyTo(crop.AsSpan(r * cw, cw));
        }

        RleRegion region = RleRegion.Threshold(crop, cw, ch, cw, minT, maxT);
        if (region.IsEmpty)
        {
            return [];
        }

        List<BlobComponentFeature> components = RleBlobAnalyzer.AnalyzeComponents(region, filterSpec.MinAreaPixels, _workspace);
        if (components.Count == 0)
        {
            return [];
        }

        components.Sort(static (a, b) => b.AreaPixels.CompareTo(a.AreaPixels));

        var results = new List<ImageJParticleRecord>(components.Count);
        int particleIndex = 0;

        foreach (BlobComponentFeature comp in components)
        {
            if (comp.AreaPixels < filterSpec.MinAreaPixels || comp.AreaPixels > filterSpec.MaxAreaPixels)
            {
                continue;
            }

            if (filterSpec.ShouldExcludeEdgeParticles &&
                (comp.MinCol <= 0 || comp.MinRow <= 0 || comp.MaxCol >= cw - 1 || comp.MaxRow >= ch - 1))
            {
                continue;
            }

            int bw = Math.Max(1, comp.BoundingWidth);
            int bh = Math.Max(1, comp.BoundingHeight);
            float aspect = (float)Math.Max(bw, bh) / Math.Max(1, Math.Min(bw, bh));
            float boxSolidity = Math.Clamp((float)comp.AreaPixels / (bw * bh), 0.05f, 1.0f);
            float circularity = Math.Clamp(boxSolidity * (float)(4.0 / Math.PI) * (1.0f - (comp.Eccentricity * 0.35f)), 0.02f, 1.0f);

            if (circularity < filterSpec.MinCircularity || circularity > filterSpec.MaxCircularity)
            {
                continue;
            }

            (float meanGv, byte minGv, byte maxGv) = MeasureBoundingBoxIntensity(crop, cw, comp, minT, maxT);
            float worldX = cx + comp.Centroid.X;
            float worldY = cy + comp.Centroid.Y;
            float angleDeg = (float)(comp.PrincipalAxisAngleRad * RadiansToDegrees);

            results.Add(new ImageJParticleRecord(
                ParticleNumber: ++particleIndex,
                AreaPixels: (int)comp.AreaPixels,
                MeanGray: meanGv,
                MinGray: minGv,
                MaxGray: maxGv,
                CentroidX: worldX,
                CentroidY: worldY,
                BoundX: cx + comp.MinCol,
                BoundY: cy + comp.MinRow,
                BoundWidth: bw,
                BoundHeight: bh,
                Circularity: circularity,
                AspectRatio: aspect,
                Eccentricity: comp.Eccentricity,
                Solidity: boxSolidity,
                OrientationDeg: angleDeg));
        }

        return results;
    }

    public static ImageJContrastSpec ComputeAutoContrastSpec(
        ReadOnlyImageView2D<byte> image,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight)
    {
        RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(image, roiX, roiY, roiWidth, roiHeight);
        if (hist.TotalPixelCount <= 0)
        {
            return ImageJContrastSpec.Identity;
        }

        int lowTarget = Math.Max(1, (int)(hist.TotalPixelCount * LowerAutoContrastQuantile));
        int highTarget = Math.Max(lowTarget, (int)(hist.TotalPixelCount * UpperAutoContrastQuantile));
        int cum = 0;
        byte minGv = hist.MinGray;
        byte maxGv = hist.MaxGray;

        for (int i = 0; i < GrayBinCount; i++)
        {
            cum += hist.Bins256[i];
            if (cum >= lowTarget)
            {
                minGv = (byte)i;
                break;
            }
        }

        cum = 0;
        for (int i = 0; i < GrayBinCount; i++)
        {
            cum += hist.Bins256[i];
            if (cum >= highTarget)
            {
                maxGv = (byte)i;
                break;
            }
        }

        if (maxGv <= minGv)
        {
            maxGv = (byte)Math.Min(byte.MaxValue, minGv + 1);
        }

        return ImageJContrastSpec.Create(minGv, maxGv, ImageJContrastSpec.NeutralGamma);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyBrightnessContrastToRoi(
        ReadOnlyImageView2D<byte> source,
        Span<byte> destinationFullBuffer,
        int dstStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        ImageJContrastSpec contrastSpec)
    {
        source.EnsureValid();
        if (!contrastSpec.IsInitialized)
        {
            throw new ArgumentException("Contrast spec must be initialized.", nameof(contrastSpec));
        }

        if (contrastSpec.IsIdentity)
        {
            return;
        }

        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, source.Width, source.Height);
        Span<byte> lut = stackalloc byte[GrayBinCount];
        float span = Math.Max(1, contrastSpec.DisplayMax - contrastSpec.DisplayMin);
        float gamma = contrastSpec.Gamma;

        for (int i = 0; i < GrayBinCount; i++)
        {
            if (i <= contrastSpec.DisplayMin)
            {
                lut[i] = byte.MinValue;
            }
            else if (i >= contrastSpec.DisplayMax)
            {
                lut[i] = byte.MaxValue;
            }
            else
            {
                float norm = (i - contrastSpec.DisplayMin) / span;
                float mapped = MathF.Abs(gamma - ImageJContrastSpec.NeutralGamma) > 1e-3f
                    ? MathF.Pow(norm, gamma)
                    : norm;
                lut[i] = (byte)Math.Clamp((int)MathF.Round(mapped * byte.MaxValue), byte.MinValue, byte.MaxValue);
            }
        }

        ReadOnlySpan<byte> px = source.Pixels;
        int srcStride = source.Stride;
        for (int r = 0; r < ch; r++)
        {
            int y = cy + r;
            ReadOnlySpan<byte> srcRow = px.Slice((y * srcStride) + cx, cw);
            Span<byte> dstRow = destinationFullBuffer.Slice((y * dstStride) + cx, cw);
            for (int c = 0; c < cw; c++)
            {
                dstRow[c] = lut[srcRow[c]];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyThresholdMaskToRoi(
        ReadOnlyImageView2D<byte> source,
        Span<byte> destinationFullBuffer,
        int dstStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        byte minThreshold,
        byte maxThreshold)
    {
        source.EnsureValid();
        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, source.Width, source.Height);
        byte lowT = Math.Min(minThreshold, maxThreshold);
        byte highT = Math.Max(minThreshold, maxThreshold);

        ReadOnlySpan<byte> px = source.Pixels;
        int srcStride = source.Stride;
        for (int r = 0; r < ch; r++)
        {
            int y = cy + r;
            ReadOnlySpan<byte> srcRow = px.Slice((y * srcStride) + cx, cw);
            Span<byte> dstRow = destinationFullBuffer.Slice((y * dstStride) + cx, cw);
            for (int c = 0; c < cw; c++)
            {
                byte gv = srcRow[c];
                dstRow[c] = (gv >= lowT && gv <= highT) ? byte.MaxValue : byte.MinValue;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ApplyFilterToRoi(
        ReadOnlyImageView2D<byte> source,
        Span<byte> destinationFullBuffer,
        int dstStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        ImageJFilterKind filter)
    {
        source.EnsureValid();
        if (filter == ImageJFilterKind.None)
        {
            return;
        }

        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, source.Width, source.Height);

        switch (filter)
        {
            case ImageJFilterKind.GaussianBlur3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.Gaussian3x3);
                break;

            case ImageJFilterKind.GaussianBlur5x5:
                ApplyGaussian5x5(source, destinationFullBuffer, dstStride, cx, cy, cw, ch);
                break;

            case ImageJFilterKind.Median3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.Median3x3);
                break;

            case ImageJFilterKind.MeanSmooth3x3:
                ApplyMeanSmooth3x3(source, destinationFullBuffer, dstStride, cx, cy, cw, ch);
                break;

            case ImageJFilterKind.UnsharpSharpen3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.UnsharpSharpen3x3);
                break;

            case ImageJFilterKind.SobelEdgeMagnitude:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.SobelGradient);
                break;

            case ImageJFilterKind.VarianceTexture3x3:
                ApplyVariance3x3(source, destinationFullBuffer, dstStride, cx, cy, cw, ch);
                break;

            case ImageJFilterKind.MorphErode3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.MorphErode3x3);
                break;

            case ImageJFilterKind.MorphDilate3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.MorphDilate3x3);
                break;

            case ImageJFilterKind.MorphOpen3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.MorphOpen3x3);
                break;

            case ImageJFilterKind.MorphClose3x3:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.MorphClose3x3);
                break;

            case ImageJFilterKind.MorphGradient3x3:
                ApplyMorphGradient3x3(source, destinationFullBuffer, dstStride, cx, cy, cw, ch);
                break;

            case ImageJFilterKind.RollingBallTopHat:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.RollingBallTopHat);
                break;

            case ImageJFilterKind.BlackBottomHat:
                ApplyBlackBottomHat(source, destinationFullBuffer, dstStride, cx, cy, cw, ch);
                break;

            case ImageJFilterKind.Invert:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.Invert);
                break;

            case ImageJFilterKind.EnhanceContrastStretch:
                RoiImageProcessor.ApplyFilterInPlaceOnRoi(source, destinationFullBuffer, dstStride, cx, cy, cw, ch, RoiPreFilterKind.EnhanceContrastStretch);
                break;

            case ImageJFilterKind.HistogramEqualize:
                ApplyHistogramEqualization(source, destinationFullBuffer, dstStride, cx, cy, cw, ch);
                break;
        }
    }

    public static void FillRoi(
        Span<byte> destinationFullBuffer,
        int imageWidth,
        int imageHeight,
        int dstStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        byte fillValue)
    {
        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, imageWidth, imageHeight);
        for (int r = 0; r < ch; r++)
        {
            destinationFullBuffer.Slice(((cy + r) * dstStride) + cx, cw).Fill(fillValue);
        }
    }

    public static void ClearOutsideRoi(
        Span<byte> destinationFullBuffer,
        int imageWidth,
        int imageHeight,
        int dstStride,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        byte backgroundValue)
    {
        (int cx, int cy, int cw, int ch) = ClampRoi(roiX, roiY, roiWidth, roiHeight, imageWidth, imageHeight);
        for (int y = 0; y < imageHeight; y++)
        {
            Span<byte> row = destinationFullBuffer.Slice(y * dstStride, imageWidth);
            if (y < cy || y >= cy + ch)
            {
                row.Fill(backgroundValue);
            }
            else
            {
                if (cx > 0)
                {
                    row.Slice(0, cx).Fill(backgroundValue);
                }

                int rightStart = cx + cw;
                if (rightStart < imageWidth)
                {
                    row.Slice(rightStart, imageWidth - rightStart).Fill(backgroundValue);
                }
            }
        }
    }

    public static void RenderBuiltInSample(
        Span<byte> destinationBuffer,
        int width,
        int height,
        int stride,
        ImageJSampleKind sampleKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        switch (sampleKind)
        {
            case ImageJSampleKind.BlobsAndParticles:
                RenderBlobsSample(destinationBuffer, width, height, stride);
                break;
            case ImageJSampleKind.CellColonyCounter:
                RenderColonySample(destinationBuffer, width, height, stride);
                break;
            case ImageJSampleKind.MetrologyEdgesAndRings:
                RenderMetrologySample(destinationBuffer, width, height, stride);
                break;
            case ImageJSampleKind.UnevenShadingAndSaltPepper:
                RenderUnevenShadingSample(destinationBuffer, width, height, stride);
                break;
            default:
                RenderBlobsSample(destinationBuffer, width, height, stride);
                break;
        }
    }

    private static (float MeanGray, byte MinGray, byte MaxGray) MeasureBoundingBoxIntensity(
        ReadOnlySpan<byte> crop,
        int cropWidth,
        in BlobComponentFeature comp,
        byte minT,
        byte maxT)
    {
        long sum = 0;
        int count = 0;
        byte minGv = byte.MaxValue;
        byte maxGv = byte.MinValue;

        for (int r = comp.MinRow; r <= comp.MaxRow; r++)
        {
            ReadOnlySpan<byte> row = crop.Slice(r * cropWidth, cropWidth);
            for (int c = comp.MinCol; c <= comp.MaxCol; c++)
            {
                byte v = row[c];
                if (v >= minT && v <= maxT)
                {
                    sum += v;
                    count++;
                    if (v < minGv)
                    {
                        minGv = v;
                    }

                    if (v > maxGv)
                    {
                        maxGv = v;
                    }
                }
            }
        }

        if (count == 0)
        {
            return (0.0f, 0, 0);
        }

        return ((float)sum / count, minGv, maxGv);
    }

    private static void ApplyGaussian5x5(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        var temp = src.Pixels.ToArray();
        RoiImageProcessor.ApplyFilterInPlaceOnRoi(src, temp, src.Stride, rx, ry, rw, rh, RoiPreFilterKind.Gaussian3x3);
        var pass1 = new ReadOnlyImageView2D<byte>(temp, src.Width, src.Height, src.Stride);
        RoiImageProcessor.ApplyFilterInPlaceOnRoi(pass1, dst, dstStride, rx, ry, rw, rh, RoiPreFilterKind.Gaussian3x3);
    }

    private static void ApplyMeanSmooth3x3(
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
                    px[(y0 * stride) + x0] + px[(y0 * stride) + x] + px[(y0 * stride) + x1] +
                    px[(y * stride) + x0] + px[(y * stride) + x] + px[(y * stride) + x1] +
                    px[(y1 * stride) + x0] + px[(y1 * stride) + x] + px[(y1 * stride) + x1];

                dstRow[c] = (byte)((sum + Median3x3Rank) / Window3x3Length);
            }
        }
    }

    private static void ApplyVariance3x3(
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

                int sum = 0;
                int sumSq = 0;
                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int v = px[(ny * stride) + nx];
                        sum += v;
                        sumSq += v * v;
                    }
                }

                float mean = sum / (float)Window3x3Length;
                float variance = MathF.Max(0.0f, (sumSq / (float)Window3x3Length) - (mean * mean));
                int scaledStd = (int)MathF.Round(MathF.Sqrt(variance) * 3.0f);
                dstRow[c] = (byte)Math.Clamp(scaledStd, byte.MinValue, byte.MaxValue);
            }
        }
    }

    private static void ApplyMorphGradient3x3(
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

                byte minV = byte.MaxValue;
                byte maxV = byte.MinValue;
                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        byte v = px[(ny * stride) + nx];
                        if (v < minV)
                        {
                            minV = v;
                        }

                        if (v > maxV)
                        {
                            maxV = v;
                        }
                    }
                }

                dstRow[c] = (byte)(maxV - minV);
            }
        }
    }

    private static void ApplyBlackBottomHat(
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

        var dilated = new byte[rw * rh];
        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int y0 = Math.Max(0, y - RollingBallRadius);
            int y1 = Math.Min(h - 1, y + RollingBallRadius);
            for (int c = 0; c < rw; c++)
            {
                int x = rx + c;
                int x0 = Math.Max(0, x - RollingBallRadius);
                int x1 = Math.Min(w - 1, x + RollingBallRadius);
                byte maxV = byte.MinValue;
                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        byte v = px[(ny * stride) + nx];
                        if (v > maxV)
                        {
                            maxV = v;
                        }
                    }
                }

                dilated[(r * rw) + c] = maxV;
            }
        }

        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            int r0 = Math.Max(0, r - RollingBallRadius);
            int r1 = Math.Min(rh - 1, r + RollingBallRadius);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);

            for (int c = 0; c < rw; c++)
            {
                int c0 = Math.Max(0, c - RollingBallRadius);
                int c1 = Math.Min(rw - 1, c + RollingBallRadius);
                byte closedVal = byte.MaxValue;
                for (int nr = r0; nr <= r1; nr++)
                {
                    for (int nc = c0; nc <= c1; nc++)
                    {
                        byte v = dilated[(nr * rw) + nc];
                        if (v < closedVal)
                        {
                            closedVal = v;
                        }
                    }
                }

                int orig = px[(y * stride) + rx + c];
                dstRow[c] = (byte)Math.Clamp(closedVal - orig, byte.MinValue, byte.MaxValue);
            }
        }
    }

    private static void ApplyHistogramEqualization(
        ReadOnlyImageView2D<byte> src,
        Span<byte> dst,
        int dstStride,
        int rx,
        int ry,
        int rw,
        int rh)
    {
        RoiHistogramAnalysis hist = RoiHistogramAnalyzer.AnalyzeRect(src, rx, ry, rw, rh);
        if (hist.TotalPixelCount <= 1)
        {
            return;
        }

        Span<byte> lut = stackalloc byte[GrayBinCount];
        int cdfMin = 0;
        for (int i = 0; i < GrayBinCount; i++)
        {
            if (hist.Bins256[i] > 0)
            {
                cdfMin = hist.Bins256[i];
                break;
            }
        }

        int runningCdf = 0;
        float denom = Math.Max(1, hist.TotalPixelCount - cdfMin);
        for (int i = 0; i < GrayBinCount; i++)
        {
            runningCdf += hist.Bins256[i];
            int eq = (int)MathF.Round(((runningCdf - cdfMin) / denom) * byte.MaxValue);
            lut[i] = (byte)Math.Clamp(eq, byte.MinValue, byte.MaxValue);
        }

        ReadOnlySpan<byte> px = src.Pixels;
        int srcStride = src.Stride;
        for (int r = 0; r < rh; r++)
        {
            int y = ry + r;
            ReadOnlySpan<byte> srcRow = px.Slice((y * srcStride) + rx, rw);
            Span<byte> dstRow = dst.Slice((y * dstStride) + rx, rw);
            for (int c = 0; c < rw; c++)
            {
                dstRow[c] = lut[srcRow[c]];
            }
        }
    }

    private static void RenderBlobsSample(Span<byte> dst, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = dst.Slice(y * stride, width);
            for (int x = 0; x < width; x++)
            {
                int bg = 32 + (((x * 7) + (y * 5)) % 14);
                row[x] = (byte)bg;
            }
        }

        ReadOnlySpan<(float Cx, float Cy, float Rx, float Ry, float AngleRad, byte PeakGv)> blobs =
        [
            (95f, 80f, 22f, 18f, 0.25f, 225),
            (210f, 75f, 28f, 14f, -0.45f, 210),
            (340f, 90f, 19f, 19f, 0.0f, 238),
            (465f, 85f, 14f, 26f, 0.60f, 195),
            (115f, 195f, 32f, 24f, -0.20f, 230),
            (255f, 185f, 16f, 16f, 0.0f, 215),
            (385f, 200f, 34f, 15f, 0.35f, 205),
            (490f, 210f, 21f, 21f, 0.0f, 242),
            (155f, 305f, 20f, 20f, 0.0f, 220),
            (305f, 300f, 26f, 18f, -0.50f, 232),
            (440f, 310f, 18f, 28f, 0.15f, 198)
        ];

        for (int i = 0; i < blobs.Length; i++)
        {
            DrawRotatedEllipse(dst, width, height, stride, blobs[i].Cx, blobs[i].Cy, blobs[i].Rx, blobs[i].Ry, blobs[i].AngleRad, blobs[i].PeakGv);
        }
    }

    private static void RenderColonySample(Span<byte> dst, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            dst.Slice(y * stride, width).Fill(28);
        }

        for (int row = 0; row < 5; row++)
        {
            for (int col = 0; col < 7; col++)
            {
                float cx = 65f + (col * 72f) + (((row * 17) + (col * 11)) % 19) - 9f;
                float cy = 52f + (row * 66f) + (((row * 13) + (col * 23)) % 17) - 8f;
                float r = 8f + (((row * 7) + (col * 5)) % 9);
                byte gv = (byte)(165 + (((row * 19) + (col * 29)) % 80));
                DrawRotatedEllipse(dst, width, height, stride, cx, cy, r, r * 0.9f, 0.1f * row, gv);
            }
        }
    }

    private static void RenderMetrologySample(Span<byte> dst, int width, int height, int stride)
    {
        float cx = width * 0.5f;
        float cy = height * 0.5f;
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = dst.Slice(y * stride, width);
            for (int x = 0; x < width; x++)
            {
                byte gv = 35;
                if (x >= 70 && x <= 210 && y >= 60 && y <= 320)
                {
                    gv = 205;
                }

                if (x >= 110 && x <= 170 && y >= 100 && y <= 280)
                {
                    gv = 50;
                }

                float dx = x - (cx + 95f);
                float dy = y - cy;
                float dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist >= 28f && dist <= 62f)
                {
                    gv = 225;
                }
                else if (dist < 28f)
                {
                    gv = 20;
                }

                row[x] = gv;
            }
        }
    }

    private static void RenderUnevenShadingSample(Span<byte> dst, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = dst.Slice(y * stride, width);
            for (int x = 0; x < width; x++)
            {
                float shading = 20.0f + (115.0f * (x / (float)Math.Max(1, width))) + (45.0f * (y / (float)Math.Max(1, height)));
                row[x] = (byte)Math.Clamp((int)shading, byte.MinValue, byte.MaxValue);
            }
        }

        DrawRotatedEllipse(dst, width, height, stride, width * 0.25f, height * 0.35f, 22f, 22f, 0f, 215);
        DrawRotatedEllipse(dst, width, height, stride, width * 0.50f, height * 0.50f, 26f, 18f, 0.3f, 235);
        DrawRotatedEllipse(dst, width, height, stride, width * 0.72f, height * 0.65f, 20f, 20f, 0f, 248);

        // Deterministic salt & pepper impulses for Median3x3 testing
        for (int i = 0; i < 180; i++)
        {
            int px = ((i * 97) + 31) % width;
            int py = ((i * 53) + 19) % height;
            dst[(py * stride) + px] = (i & 1) == 0 ? byte.MaxValue : byte.MinValue;
        }
    }

    private static void DrawRotatedEllipse(
        Span<byte> dst,
        int width,
        int height,
        int stride,
        float cx,
        float cy,
        float rx,
        float ry,
        float angleRad,
        byte peakGv)
    {
        float cos = MathF.Cos(angleRad);
        float sin = MathF.Sin(angleRad);
        int maxR = (int)MathF.Ceiling(MathF.Max(rx, ry)) + 3;
        int y0 = Math.Max(0, (int)(cy - maxR));
        int y1 = Math.Min(height - 1, (int)(cy + maxR));
        int x0 = Math.Max(0, (int)(cx - maxR));
        int x1 = Math.Min(width - 1, (int)(cx + maxR));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                float lx = (dx * cos) + (dy * sin);
                float ly = (-dx * sin) + (dy * cos);
                float normDist = MathF.Sqrt(((lx * lx) / (rx * rx)) + ((ly * ly) / (ry * ry)));
                if (normDist <= 1.0f)
                {
                    float profile = 1.0f - (0.20f * normDist * normDist);
                    byte val = (byte)Math.Clamp((int)(peakGv * profile), byte.MinValue, byte.MaxValue);
                    int idx = (y * stride) + x;
                    if (val > dst[idx])
                    {
                        dst[idx] = val;
                    }
                }
            }
        }
    }

    private static (int X, int Y, int Width, int Height) ClampRoi(
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        int imageWidth,
        int imageHeight)
    {
        int cx = Math.Clamp(roiX, 0, Math.Max(0, imageWidth - 1));
        int cy = Math.Clamp(roiY, 0, Math.Max(0, imageHeight - 1));
        int cw = Math.Clamp(roiWidth, 1, Math.Max(1, imageWidth - cx));
        int ch = Math.Clamp(roiHeight, 1, Math.Max(1, imageHeight - cy));
        return (cx, cy, cw, ch);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.Dispose();
    }
}
