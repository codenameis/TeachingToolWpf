// File: src/VisionAlgorithm/OpenSrc/Common/RoiHistogramAnalyzer.cs
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Stateless ImageJ-style grayscale ROI histogram, multi-algorithm auto-thresholds
/// (Otsu, Triangle, IsoData, MaxEntropy, Mean, Percentile), higher-order moments (Skewness, Kurtosis),
/// Shannon entropy, and 1D horizontal/vertical intensity + edge gradient profile analyzer.
/// </summary>
public static class RoiHistogramAnalyzer
{
    private const int GrayBinCount = byte.MaxValue + 1;
    private const float PercentMultiplier = 100.0f;
    private const float FifthPercentileRatio = 0.05f;
    private const float MedianPercentileRatio = 0.50f;
    private const float NinetyFifthPercentileRatio = 0.95f;
    private const float ZeroProbabilityEpsilon = 1e-12f;
    private const float HalfCentralDifferenceScale = 0.5f;
    private const float ExcessKurtosisBaseline = 3.0f;
    private const int MaxIsoDataIterations = 64;
    private const byte DefaultFallbackThreshold = 128;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static RoiHistogramAnalysis AnalyzeRect(
        ReadOnlyImageView2D<byte> image,
        int roiX,
        int roiY,
        int roiWidth,
        int roiHeight,
        byte minThreshold = DefaultFallbackThreshold,
        byte maxThreshold = byte.MaxValue)
    {
        image.EnsureValid();
        if (image.Width <= 0 || image.Height <= 0)
        {
            return RoiHistogramAnalysis.Empty;
        }

        int clampedX = Math.Clamp(roiX, 0, Math.Max(0, image.Width - 1));
        int clampedY = Math.Clamp(roiY, 0, Math.Max(0, image.Height - 1));
        int clampedW = Math.Clamp(roiWidth, 1, image.Width - clampedX);
        int clampedH = Math.Clamp(roiHeight, 1, image.Height - clampedY);
        int totalPixels = checked(clampedW * clampedH);

        var bins = new int[GrayBinCount];
        var horizontalProfile = new float[clampedW];
        var verticalProfile = new float[clampedH];

        byte minGray = byte.MaxValue;
        byte maxGray = byte.MinValue;
        long sumGray = 0;
        long sumGraySq = 0;
        int thresholdedCount = 0;

        byte lowT = Math.Min(minThreshold, maxThreshold);
        byte highT = Math.Max(minThreshold, maxThreshold);

        ReadOnlySpan<byte> pixels = image.Pixels;
        int stride = image.Stride;

        for (int r = 0; r < clampedH; r++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(((clampedY + r) * stride) + clampedX, clampedW);
            long rowSum = 0;

            for (int c = 0; c < clampedW; c++)
            {
                byte g = row[c];
                bins[g]++;
                rowSum += g;
                horizontalProfile[c] += g;
                sumGray += g;
                sumGraySq += g * g;

                if (g < minGray)
                {
                    minGray = g;
                }

                if (g > maxGray)
                {
                    maxGray = g;
                }

                if (g >= lowT && g <= highT)
                {
                    thresholdedCount++;
                }
            }

            verticalProfile[r] = (float)rowSum / clampedW;
        }

        float invHeight = 1.0f / clampedH;
        for (int c = 0; c < clampedW; c++)
        {
            horizontalProfile[c] *= invHeight;
        }

        var horizontalGradient = ComputeGradientProfile(horizontalProfile, out float maxGradX, out int peakGradX);
        var verticalGradient = ComputeGradientProfile(verticalProfile, out float maxGradY, out int peakGradY);

        float invTotal = 1.0f / totalPixels;
        float meanGray = sumGray * invTotal;
        float variance = MathF.Max(0.0f, (sumGraySq * invTotal) - (meanGray * meanGray));
        float stdDevGray = MathF.Sqrt(variance);

        int peakBinCount = 0;
        byte modeGray = 0;
        float entropyBits = 0.0f;
        double thirdMomentSum = 0.0;
        double fourthMomentSum = 0.0;

        for (int i = 0; i < GrayBinCount; i++)
        {
            int count = bins[i];
            if (count > peakBinCount)
            {
                peakBinCount = count;
                modeGray = (byte)i;
            }

            if (count > 0)
            {
                float p = count * invTotal;
                if (p > ZeroProbabilityEpsilon)
                {
                    entropyBits -= p * MathF.Log2(p);
                }

                double diff = i - meanGray;
                double diffSq = diff * diff;
                thirdMomentSum += count * diffSq * diff;
                fourthMomentSum += count * diffSq * diffSq;
            }
        }

        float skewness = 0.0f;
        float kurtosis = 0.0f;
        if (stdDevGray > 1e-4f)
        {
            double sigma3 = (double)stdDevGray * stdDevGray * stdDevGray;
            double sigma4 = sigma3 * stdDevGray;
            skewness = (float)((thirdMomentSum * invTotal) / sigma3);
            kurtosis = (float)(((fourthMomentSum * invTotal) / sigma4) - ExcessKurtosisBaseline);
        }

        byte p5 = FindQuantile(bins, totalPixels, FifthPercentileRatio);
        byte median = FindQuantile(bins, totalPixels, MedianPercentileRatio);
        byte p95 = FindQuantile(bins, totalPixels, NinetyFifthPercentileRatio);

        byte otsu = ComputeOtsuThreshold(bins, totalPixels, sumGray);
        byte triangle = ComputeTriangleThreshold(bins);
        byte isoData = ComputeIsoDataThreshold(bins);
        byte maxEntropy = ComputeMaxEntropyThreshold(bins, totalPixels);
        byte meanThresh = (byte)Math.Clamp((int)MathF.Round(meanGray), byte.MinValue, byte.MaxValue);
        float thresholdedPercent = (thresholdedCount * PercentMultiplier) * invTotal;

        return new RoiHistogramAnalysis(
            RoiX: clampedX,
            RoiY: clampedY,
            RoiWidth: clampedW,
            RoiHeight: clampedH,
            TotalPixelCount: totalPixels,
            MinGray: minGray,
            MaxGray: maxGray,
            ModeGray: modeGray,
            MedianGray: median,
            Percentile5Gray: p5,
            Percentile95Gray: p95,
            OtsuThreshold: otsu,
            TriangleThreshold: triangle,
            IsoDataThreshold: isoData,
            MaxEntropyThreshold: maxEntropy,
            MeanThreshold: meanThresh,
            MeanGray: meanGray,
            StdDevGray: stdDevGray,
            Skewness: skewness,
            Kurtosis: kurtosis,
            EntropyBits: entropyBits,
            ThresholdedPixelCount: thresholdedCount,
            ThresholdedRatioPercent: thresholdedPercent,
            PeakBinCount: peakBinCount,
            Bins256: bins,
            HorizontalProfile: horizontalProfile,
            VerticalProfile: verticalProfile,
            HorizontalGradientProfile: horizontalGradient,
            VerticalGradientProfile: verticalGradient,
            MaxHorizontalGradient: maxGradX,
            PeakHorizontalGradientX: peakGradX,
            MaxVerticalGradient: maxGradY,
            PeakVerticalGradientY: peakGradY);
    }

    private static float[] ComputeGradientProfile(ReadOnlySpan<float> profile, out float maxGradient, out int peakIndex)
    {
        int len = profile.Length;
        var grad = new float[len];
        maxGradient = 0.0f;
        peakIndex = 0;

        if (len <= 1)
        {
            return grad;
        }

        for (int i = 0; i < len; i++)
        {
            float g;
            if (i == 0)
            {
                g = MathF.Abs(profile[1] - profile[0]);
            }
            else if (i == len - 1)
            {
                g = MathF.Abs(profile[len - 1] - profile[len - 2]);
            }
            else
            {
                g = MathF.Abs(profile[i + 1] - profile[i - 1]) * HalfCentralDifferenceScale;
            }

            grad[i] = g;
            if (g > maxGradient)
            {
                maxGradient = g;
                peakIndex = i;
            }
        }

        return grad;
    }

    private static byte FindQuantile(ReadOnlySpan<int> bins, int totalPixels, float quantileRatio)
    {
        int targetCount = Math.Clamp((int)MathF.Ceiling(totalPixels * quantileRatio), 1, totalPixels);
        int cumulative = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            cumulative += bins[i];
            if (cumulative >= targetCount)
            {
                return (byte)i;
            }
        }

        return byte.MaxValue;
    }

    // [Math Why]: Otsu's method maximizes inter-class variance sigma_B^2(t) = w0(t) * w1(t) * (mu0(t) - mu1(t))^2 across 256 bins in O(256).
    private static byte ComputeOtsuThreshold(ReadOnlySpan<int> bins, int totalPixels, long totalIntensitySum)
    {
        long weightBackground = 0;
        double sumBackground = 0.0;
        double maxBetweenVariance = -1.0;
        byte optimalThreshold = DefaultFallbackThreshold;

        for (int t = 0; t < bins.Length; t++)
        {
            weightBackground += bins[t];
            if (weightBackground == 0)
            {
                continue;
            }

            long weightForeground = totalPixels - weightBackground;
            if (weightForeground == 0)
            {
                break;
            }

            sumBackground += (double)t * bins[t];
            double meanBackground = sumBackground / weightBackground;
            double meanForeground = (totalIntensitySum - sumBackground) / weightForeground;
            double diff = meanBackground - meanForeground;
            double betweenVariance = (double)weightBackground * weightForeground * diff * diff;

            if (betweenVariance > maxBetweenVariance)
            {
                maxBetweenVariance = betweenVariance;
                optimalThreshold = (byte)t;
            }
        }

        return optimalThreshold;
    }

    // [Math Why]: Zack's Triangle method constructs a line from the histogram peak (b_max, h_max) to the farthest non-zero tail
    // and selects the intensity bin maximizing perpendicular distance to that line. Ideal for skewed unimodal histograms.
    private static byte ComputeTriangleThreshold(ReadOnlySpan<int> bins)
    {
        int minBin = 0;
        while (minBin < GrayBinCount && bins[minBin] == 0)
        {
            minBin++;
        }

        int maxBin = GrayBinCount - 1;
        while (maxBin > minBin && bins[maxBin] == 0)
        {
            maxBin--;
        }

        if (maxBin - minBin <= 1)
        {
            return (byte)Math.Clamp(minBin, byte.MinValue, byte.MaxValue);
        }

        int peakBin = minBin;
        int peakVal = bins[minBin];
        for (int i = minBin + 1; i <= maxBin; i++)
        {
            if (bins[i] > peakVal)
            {
                peakVal = bins[i];
                peakBin = i;
            }
        }

        bool inverted = (peakBin - minBin) > (maxBin - peakBin);
        int startBin = inverted ? minBin : peakBin;
        int endBin = inverted ? peakBin : maxBin;
        if (endBin <= startBin || peakVal <= 0)
        {
            return (byte)peakBin;
        }

        double nx = peakVal;
        double ny = inverted ? (minBin - peakBin) : (maxBin - peakBin);
        double norm = Math.Sqrt((nx * nx) + (ny * ny));
        if (norm <= 1e-9)
        {
            return (byte)peakBin;
        }

        nx /= norm;
        ny /= norm;

        double maxDist = -1.0;
        int bestBin = peakBin;
        for (int b = startBin; b <= endBin; b++)
        {
            double dx = b - peakBin;
            double dy = bins[b] - peakVal;
            double dist = Math.Abs((nx * dx) + (ny * dy));
            if (dist > maxDist)
            {
                maxDist = dist;
                bestBin = b;
            }
        }

        return (byte)Math.Clamp(bestBin, byte.MinValue, byte.MaxValue);
    }

    // [Math Why]: Ridler-Calvard IsoData iteratively updates T_{k+1} = (mu_bg(T_k) + mu_fg(T_k)) / 2 until fixed-point convergence.
    private static byte ComputeIsoDataThreshold(ReadOnlySpan<int> bins)
    {
        int minBin = 0;
        while (minBin < GrayBinCount && bins[minBin] == 0)
        {
            minBin++;
        }

        int maxBin = GrayBinCount - 1;
        while (maxBin > minBin && bins[maxBin] == 0)
        {
            maxBin--;
        }

        if (minBin >= maxBin)
        {
            return (byte)Math.Clamp(minBin, byte.MinValue, byte.MaxValue);
        }

        int threshold = (minBin + maxBin) / 2;
        for (int iter = 0; iter < MaxIsoDataIterations; iter++)
        {
            long sumLow = 0;
            long countLow = 0;
            for (int i = minBin; i <= threshold; i++)
            {
                sumLow += (long)i * bins[i];
                countLow += bins[i];
            }

            long sumHigh = 0;
            long countHigh = 0;
            for (int i = threshold + 1; i <= maxBin; i++)
            {
                sumHigh += (long)i * bins[i];
                countHigh += bins[i];
            }

            if (countLow == 0 || countHigh == 0)
            {
                break;
            }

            double meanLow = (double)sumLow / countLow;
            double meanHigh = (double)sumHigh / countHigh;
            int nextThreshold = (int)Math.Round((meanLow + meanHigh) * HalfCentralDifferenceScale);
            if (nextThreshold == threshold)
            {
                break;
            }

            threshold = nextThreshold;
        }

        return (byte)Math.Clamp(threshold, byte.MinValue, byte.MaxValue);
    }

    // [Math Why]: Kapur-Sahoo-Wong Maximum Entropy selects T maximizing H_bg(T) + H_fg(T) where H = -sum(p_i / P * ln(p_i / P)).
    private static byte ComputeMaxEntropyThreshold(ReadOnlySpan<int> bins, int totalPixels)
    {
        if (totalPixels <= 0)
        {
            return DefaultFallbackThreshold;
        }

        Span<double> p = stackalloc double[GrayBinCount];
        Span<double> cumP = stackalloc double[GrayBinCount];
        double invTotal = 1.0 / totalPixels;
        double running = 0.0;

        for (int i = 0; i < GrayBinCount; i++)
        {
            p[i] = bins[i] * invTotal;
            running += p[i];
            cumP[i] = running;
        }

        double maxTotalEntropy = -1.0;
        byte bestT = DefaultFallbackThreshold;

        for (int t = 0; t < GrayBinCount - 1; t++)
        {
            double pBg = cumP[t];
            double pFg = 1.0 - pBg;
            if (pBg <= 1e-9 || pFg <= 1e-9)
            {
                continue;
            }

            double hBg = 0.0;
            for (int i = 0; i <= t; i++)
            {
                if (p[i] > 1e-12)
                {
                    double q = p[i] / pBg;
                    hBg -= q * Math.Log(q);
                }
            }

            double hFg = 0.0;
            for (int i = t + 1; i < GrayBinCount; i++)
            {
                if (p[i] > 1e-12)
                {
                    double q = p[i] / pFg;
                    hFg -= q * Math.Log(q);
                }
            }

            double totalH = hBg + hFg;
            if (totalH > maxTotalEntropy)
            {
                maxTotalEntropy = totalH;
                bestT = (byte)t;
            }
        }

        return bestT;
    }
}
