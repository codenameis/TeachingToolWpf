using System.Buffers;
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Industrial frequency-domain and texture preprocessing engine:
/// 1. 2D Radix-2 FFT Periodic Moiré / Mesh Notch Filter (Cooley-Tukey FFT + off-DC spectral spike detection + Gaussian notch attenuation + IFFT).
/// 2. Homomorphic Illumination-Reflectance Separation Filter ($\ln(I+1)$ -> spatial Gaussian low-pass separation -> differential gain $\gamma_L, \gamma_H$ -> $\exp(\cdot)$).
/// 3. 2D Oriented Gabor Texture Filter ($g_{\lambda,\theta,\sigma,\gamma}(x,y)$ quadrature carrier bank).
/// 4. Live ROI 256-bin Grayscale Histogram, Otsu Threshold, and 1D Center-Line Intensity/Gradient Profile analyzer.
/// </summary>
public static class FrequencyAndTextureFilterEngine
{
    private const int HistogramBinCount = 256;
    private const byte MaxByteValue = 255;
    private const int MaxFftDimension = 256;
    private const int MinFftDimension = 16;
    private const int MaxDetectedPeaks = 16;
    private const int PeakExclusionNeighborhood = 2;
    private const double NumericalEpsilon = 1e-9;
    private const double TwoPi = 2.0 * Math.PI;
    private const double DegToRad = Math.PI / 180.0;
    private const int MinGaborRadius = 2;
    private const int MaxGaborRadius = 16;
    private const double GaborRadiusSigmaMultiplier = 2.5;

    /// <summary>
    /// Suppresses periodic Moiré, mesh, or woven grid interference using a 2D Radix-2 Cooley-Tukey FFT,
    /// automatic off-DC secondary spectral peak detection, symmetric Gaussian notch attenuation, and 2D IFFT.
    /// Returns the number of attenuated spectral peak pairs.
    /// </summary>
    public static int ApplyFftPeriodicNotchFilter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        Span<byte> destination,
        int minFrequencyRadius = FftHomomorphicGaborSpec.DefaultFftMinRadius,
        double notchSigma = FftHomomorphicGaborSpec.DefaultFftNotchSigma,
        double peakThresholdRatio = FftHomomorphicGaborSpec.DefaultFftPeakThresholdRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var pixelCount = width * height;
        if (destination.Length < pixelCount)
        {
            throw new ArgumentException("Destination buffer is smaller than width * height.", nameof(destination));
        }

        var fftWidth = ClampPowerOfTwo(width);
        var fftHeight = ClampPowerOfTwo(height);
        var totalBins = fftWidth * fftHeight;

        var realPool = ArrayPool<double>.Shared.Rent(totalBins);
        var imagPool = ArrayPool<double>.Shared.Rent(totalBins);
        var magPool = ArrayPool<double>.Shared.Rent(totalBins);

        try
        {
            var real = realPool.AsSpan(0, totalBins);
            var imag = imagPool.AsSpan(0, totalBins);
            var mag = magPool.AsSpan(0, totalBins);
            real.Clear();
            imag.Clear();

            var scaleX = (double)width / fftWidth;
            var scaleY = (double)height / fftHeight;

            for (var fy = 0; fy < fftHeight; fy++)
            {
                var sy = Math.Clamp((int)(fy * scaleY), 0, height - 1);
                var rowOffset = sy * stride;
                var fftRow = fy * fftWidth;
                var parityY = (fy & 1) != 0 ? -1.0 : 1.0;

                for (var fx = 0; fx < fftWidth; fx++)
                {
                    var sx = Math.Clamp((int)(fx * scaleX), 0, width - 1);
                    var sign = ((fx & 1) != 0 ? -parityY : parityY);
                    real[fftRow + fx] = source[rowOffset + sx] * sign;
                }
            }

            Transform2DRadix2(real, imag, fftWidth, fftHeight, inverse: false);

            var centerX = fftWidth / 2;
            var centerY = fftHeight / 2;
            var safeMinRadius = Math.Clamp(minFrequencyRadius, 3, Math.Min(centerX, centerY) / 2);
            var minRadiusSq = safeMinRadius * safeMinRadius;
            var sumMag = 0.0;
            var countMag = 0;

            for (var fy = 0; fy < fftHeight; fy++)
            {
                var dy = fy - centerY;
                var fftRow = fy * fftWidth;
                for (var fx = 0; fx < fftWidth; fx++)
                {
                    var dx = fx - centerX;
                    var idx = fftRow + fx;
                    var m = Math.Sqrt((real[idx] * real[idx]) + (imag[idx] * imag[idx]));
                    mag[idx] = m;

                    var rSq = (dx * dx) + (dy * dy);
                    if (rSq >= minRadiusSq)
                    {
                        sumMag += m;
                        countMag++;
                    }
                }
            }

            var meanHighFreqMag = countMag > 0 ? sumMag / countMag : 1.0;
            var safeRatio = Math.Clamp(peakThresholdRatio, 2.0, 20.0);
            var threshold = meanHighFreqMag * safeRatio;

            Span<(int X, int Y, double Mag)> peaks = stackalloc (int X, int Y, double Mag)[MaxDetectedPeaks];
            var peakCount = 0;

            for (var fy = PeakExclusionNeighborhood; fy < fftHeight - PeakExclusionNeighborhood; fy++)
            {
                var dy = fy - centerY;
                var fftRow = fy * fftWidth;
                for (var fx = PeakExclusionNeighborhood; fx < fftWidth - PeakExclusionNeighborhood; fx++)
                {
                    var dx = fx - centerX;
                    var rSq = (dx * dx) + (dy * dy);
                    if (rSq < minRadiusSq)
                    {
                        continue;
                    }

                    var m = mag[fftRow + fx];
                    if (m < threshold)
                    {
                        continue;
                    }

                    var isLocalMax = true;
                    for (var ny = -PeakExclusionNeighborhood; ny <= PeakExclusionNeighborhood && isLocalMax; ny++)
                    {
                        var nRow = (fy + ny) * fftWidth;
                        for (var nx = -PeakExclusionNeighborhood; nx <= PeakExclusionNeighborhood; nx++)
                        {
                            if (nx == 0 && ny == 0)
                            {
                                continue;
                            }

                            if (mag[nRow + fx + nx] > m)
                            {
                                isLocalMax = false;
                                break;
                            }
                        }
                    }

                    if (!isLocalMax)
                    {
                        continue;
                    }

                    if (peakCount < MaxDetectedPeaks)
                    {
                        peaks[peakCount++] = (fx, fy, m);
                    }
                    else
                    {
                        var minIdx = 0;
                        for (var i = 1; i < MaxDetectedPeaks; i++)
                        {
                            if (peaks[i].Mag < peaks[minIdx].Mag)
                            {
                                minIdx = i;
                            }
                        }

                        if (m > peaks[minIdx].Mag)
                        {
                            peaks[minIdx] = (fx, fy, m);
                        }
                    }
                }
            }

            var safeSigma = Math.Clamp(notchSigma, 1.0, 24.0);
            var invTwoSigmaSq = 1.0 / (2.0 * safeSigma * safeSigma);

            if (peakCount > 0)
            {
                for (var fy = 0; fy < fftHeight; fy++)
                {
                    var fftRow = fy * fftWidth;
                    for (var fx = 0; fx < fftWidth; fx++)
                    {
                        var gain = 1.0;
                        for (var p = 0; p < peakCount; p++)
                        {
                            var px = peaks[p].X;
                            var py = peaks[p].Y;
                            var symX = (2 * centerX) - px;
                            var symY = (2 * centerY) - py;

                            var d1Sq = ((fx - px) * (fx - px)) + ((fy - py) * (fy - py));
                            var d2Sq = ((fx - symX) * (fx - symX)) + ((fy - symY) * (fy - symY));

                            gain *= (1.0 - Math.Exp(-d1Sq * invTwoSigmaSq)) * (1.0 - Math.Exp(-d2Sq * invTwoSigmaSq));
                        }

                        var idx = fftRow + fx;
                        real[idx] *= gain;
                        imag[idx] *= gain;
                    }
                }
            }

            Transform2DRadix2(real, imag, fftWidth, fftHeight, inverse: true);

            var invScaleX = (double)(fftWidth - 1) / Math.Max(1, width - 1);
            var invScaleY = (double)(fftHeight - 1) / Math.Max(1, height - 1);

            for (var y = 0; y < height; y++)
            {
                var fy = Math.Clamp((int)Math.Round(y * invScaleY), 0, fftHeight - 1);
                var srcRow = y * stride;
                var dstRow = y * width;
                var fftRow = fy * fftWidth;
                var parityY = (fy & 1) != 0 ? -1.0 : 1.0;

                for (var x = 0; x < width; x++)
                {
                    var fx = Math.Clamp((int)Math.Round(x * invScaleX), 0, fftWidth - 1);
                    var sign = ((fx & 1) != 0 ? -parityY : parityY);
                    var reconstructed = real[fftRow + fx] * sign;

                    if (fftWidth < width || fftHeight < height)
                    {
                        var orig = source[srcRow + x];
                        var origSampled = source[(Math.Clamp((int)(fy * scaleY), 0, height - 1) * stride) + Math.Clamp((int)(fx * scaleX), 0, width - 1)];
                        var periodicComponent = origSampled - reconstructed;
                        var corrected = orig - periodicComponent;
                        destination[dstRow + x] = (byte)Math.Clamp((int)Math.Round(corrected), 0, MaxByteValue);
                    }
                    else
                    {
                        destination[dstRow + x] = (byte)Math.Clamp((int)Math.Round(reconstructed), 0, MaxByteValue);
                    }
                }
            }

            return peakCount;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(realPool);
            ArrayPool<double>.Shared.Return(imagPool);
            ArrayPool<double>.Shared.Return(magPool);
        }
    }

    /// <summary>
    /// Applies Homomorphic Illumination-Reflectance separation filtering:
    /// $\ln(I(x,y) + 1) = \ln(L(x,y)) + \ln(R(x,y))$, separates low-frequency illumination via spatial Gaussian smoothing,
    /// applies differential gain $\gamma_L L + \gamma_H R$, and restores via $\exp(\cdot) - 1$.
    /// </summary>
    public static void ApplyHomomorphicFilter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        Span<byte> destination,
        double gammaLow = FftHomomorphicGaborSpec.DefaultHomomorphicGammaLow,
        double gammaHigh = FftHomomorphicGaborSpec.DefaultHomomorphicGammaHigh,
        double sigma = FftHomomorphicGaborSpec.DefaultHomomorphicSigma)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var pixelCount = width * height;
        if (destination.Length < pixelCount)
        {
            throw new ArgumentException("Destination buffer is smaller than width * height.", nameof(destination));
        }

        var safeGammaLow = Math.Clamp(gammaLow, 0.05, 1.5);
        var safeGammaHigh = Math.Clamp(gammaHigh, 0.8, 4.0);
        var safeSigma = Math.Clamp(sigma, 2.0, 64.0);

        var logPool = ArrayPool<double>.Shared.Rent(pixelCount);
        var tempPool = ArrayPool<double>.Shared.Rent(pixelCount);
        var lowPassPool = ArrayPool<double>.Shared.Rent(pixelCount);

        try
        {
            var logImg = logPool.AsSpan(0, pixelCount);
            var temp = tempPool.AsSpan(0, pixelCount);
            var lowPass = lowPassPool.AsSpan(0, pixelCount);

            for (var y = 0; y < height; y++)
            {
                var srcRow = y * stride;
                var dstRow = y * width;
                for (var x = 0; x < width; x++)
                {
                    logImg[dstRow + x] = Math.Log(source[srcRow + x] + 1.0);
                }
            }

            var radius = Math.Clamp((int)Math.Ceiling(safeSigma * 2.0), 2, 24);
            Span<double> kernel = stackalloc double[(2 * radius) + 1];
            var invTwoSigmaSq = 1.0 / (2.0 * safeSigma * safeSigma);
            var kSum = 0.0;
            for (var k = -radius; k <= radius; k++)
            {
                var w = Math.Exp(-(k * k) * invTwoSigmaSq);
                kernel[k + radius] = w;
                kSum += w;
            }

            for (var i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= kSum;
            }

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sx = Math.Clamp(x + k, 0, width - 1);
                        acc += logImg[rowOffset + sx] * kernel[k + radius];
                    }

                    temp[rowOffset + x] = acc;
                }
            }

            var minOut = double.MaxValue;
            var maxOut = double.MinValue;
            var meanIlluminationLog = 0.0;

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var acc = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sy = Math.Clamp(y + k, 0, height - 1);
                        acc += temp[(sy * width) + x] * kernel[k + radius];
                    }

                    lowPass[rowOffset + x] = acc;
                    meanIlluminationLog += acc;
                }
            }

            meanIlluminationLog /= Math.Max(1, pixelCount);

            for (var i = 0; i < pixelCount; i++)
            {
                var illumination = lowPass[i];
                var reflectance = logImg[i] - illumination;
                var enhancedLog = (safeGammaLow * (illumination - meanIlluminationLog)) + meanIlluminationLog + (safeGammaHigh * reflectance);
                var restored = Math.Exp(enhancedLog) - 1.0;
                temp[i] = restored;

                if (restored < minOut)
                {
                    minOut = restored;
                }

                if (restored > maxOut)
                {
                    maxOut = restored;
                }
            }

            var dynamicRange = maxOut - minOut;
            var useRescale = minOut < 0.0 || maxOut > MaxByteValue;
            var scale = dynamicRange > NumericalEpsilon ? MaxByteValue / dynamicRange : 1.0;

            for (var i = 0; i < pixelCount; i++)
            {
                var v = useRescale
                    ? (temp[i] - minOut) * scale
                    : temp[i];
                destination[i] = (byte)Math.Clamp((int)Math.Round(v), 0, MaxByteValue);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(logPool);
            ArrayPool<double>.Shared.Return(tempPool);
            ArrayPool<double>.Shared.Return(lowPassPool);
        }
    }

    /// <summary>
    /// Applies a 2D Oriented Gabor Quadrature Filter ($g_{\lambda,\theta,\sigma,\gamma}(x,y)$) to highlight directional
    /// scratches, brushed textures, or woven fiber defects along orientation $\theta$.
    /// </summary>
    public static void ApplyGaborTextureFilter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride,
        Span<byte> destination,
        double wavelength = FftHomomorphicGaborSpec.DefaultGaborWavelength,
        double orientationDeg = FftHomomorphicGaborSpec.DefaultGaborOrientationDeg,
        double sigma = FftHomomorphicGaborSpec.DefaultGaborSigma,
        double aspectRatio = FftHomomorphicGaborSpec.DefaultGaborAspectRatio)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var pixelCount = width * height;
        if (destination.Length < pixelCount)
        {
            throw new ArgumentException("Destination buffer is smaller than width * height.", nameof(destination));
        }

        var safeWavelength = Math.Clamp(wavelength, 3.0, 32.0);
        var safeSigma = Math.Clamp(sigma, 1.2, 12.0);
        var safeGamma = Math.Clamp(aspectRatio, 0.2, 2.0);
        var thetaRad = orientationDeg * DegToRad;
        var cosTheta = Math.Cos(thetaRad);
        var sinTheta = Math.Sin(thetaRad);

        var radius = Math.Clamp((int)Math.Ceiling(safeSigma * GaborRadiusSigmaMultiplier), MinGaborRadius, MaxGaborRadius);
        var kernelSide = (2 * radius) + 1;
        var kernelLen = kernelSide * kernelSide;

        Span<double> kernelEven = stackalloc double[kernelLen];
        Span<double> kernelOdd = stackalloc double[kernelLen];

        var gammaSq = safeGamma * safeGamma;
        var invTwoSigmaSq = 1.0 / (2.0 * safeSigma * safeSigma);
        var spatialFreq = TwoPi / safeWavelength;
        var evenMean = 0.0;
        var l1Norm = 0.0;

        for (var ky = -radius; ky <= radius; ky++)
        {
            var rowOffset = (ky + radius) * kernelSide;
            for (var kx = -radius; kx <= radius; kx++)
            {
                var xPrime = (kx * cosTheta) + (ky * sinTheta);
                var yPrime = (-kx * sinTheta) + (ky * cosTheta);
                var envelope = Math.Exp(-((xPrime * xPrime) + (gammaSq * yPrime * yPrime)) * invTwoSigmaSq);
                var phase = spatialFreq * xPrime;

                var evenVal = envelope * Math.Cos(phase);
                var oddVal = envelope * Math.Sin(phase);

                kernelEven[rowOffset + kx + radius] = evenVal;
                kernelOdd[rowOffset + kx + radius] = oddVal;
                evenMean += evenVal;
                l1Norm += Math.Sqrt((evenVal * evenVal) + (oddVal * oddVal));
            }
        }

        evenMean /= kernelLen;
        for (var i = 0; i < kernelLen; i++)
        {
            kernelEven[i] -= evenMean;
        }

        var normScale = l1Norm > NumericalEpsilon ? 2.5 / l1Norm : 1.0;

        for (var y = 0; y < height; y++)
        {
            var dstRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var sumEven = 0.0;
                var sumOdd = 0.0;

                for (var ky = -radius; ky <= radius; ky++)
                {
                    var sy = Math.Clamp(y + ky, 0, height - 1);
                    var srcRow = sy * stride;
                    var kRow = (ky + radius) * kernelSide;

                    for (var kx = -radius; kx <= radius; kx++)
                    {
                        var sx = Math.Clamp(x + kx, 0, width - 1);
                        var px = source[srcRow + sx];
                        var kIdx = kRow + kx + radius;
                        sumEven += px * kernelEven[kIdx];
                        sumOdd += px * kernelOdd[kIdx];
                    }
                }

                var energy = Math.Sqrt((sumEven * sumEven) + (sumOdd * sumOdd)) * normScale;
                destination[dstRow + x] = (byte)Math.Clamp((int)Math.Round(energy), 0, MaxByteValue);
            }
        }
    }

    /// <summary>
    /// Computes the 256-bin grayscale histogram, Otsu optimal threshold, first/second-order moments,
    /// and 1D horizontal/vertical center-line intensity and gradient profiles for live ROI diagnostics.
    /// </summary>
    public static RoiHistogramProfileSummary ComputeRoiHistogramAndProfile(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);

        var bins = new int[HistogramBinCount];
        byte minVal = MaxByteValue;
        byte maxVal = 0;
        long sum = 0;
        long sumSq = 0;
        var totalPixels = width * height;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var px = source[rowOffset + x];
                bins[px]++;
                if (px < minVal)
                {
                    minVal = px;
                }

                if (px > maxVal)
                {
                    maxVal = px;
                }

                sum += px;
                sumSq += (long)px * px;
            }
        }

        var mean = totalPixels > 0 ? (double)sum / totalPixels : 0.0;
        var variance = totalPixels > 0 ? Math.Max(0.0, ((double)sumSq / totalPixels) - (mean * mean)) : 0.0;
        var stdDev = Math.Sqrt(variance);

        var totalWeight = (double)totalPixels;
        var sumTotal = (double)sum;
        var weightBg = 0.0;
        var sumBg = 0.0;
        var maxBetweenVariance = -1.0;
        byte otsuThreshold = 128;

        for (var t = 0; t < HistogramBinCount; t++)
        {
            weightBg += bins[t];
            if (weightBg <= 0.0)
            {
                continue;
            }

            var weightFg = totalWeight - weightBg;
            if (weightFg <= 0.0)
            {
                break;
            }

            sumBg += (double)t * bins[t];
            var meanBg = sumBg / weightBg;
            var meanFg = (sumTotal - sumBg) / weightFg;
            var diff = meanBg - meanFg;
            var betweenVar = (weightBg / totalWeight) * (weightFg / totalWeight) * diff * diff;

            if (betweenVar > maxBetweenVariance)
            {
                maxBetweenVariance = betweenVar;
                otsuThreshold = (byte)t;
            }
        }

        var centerY = height / 2;
        var centerRowOffset = centerY * stride;
        var hSum = 0.0;
        var hPeakGrad = 0.0;

        for (var x = 0; x < width; x++)
        {
            var px = source[centerRowOffset + x];
            hSum += px;
            if (x > 0)
            {
                var grad = Math.Abs(px - source[centerRowOffset + x - 1]);
                if (grad > hPeakGrad)
                {
                    hPeakGrad = grad;
                }
            }
        }

        var centerX = width / 2;
        var vSum = 0.0;
        var vPeakGrad = 0.0;

        for (var y = 0; y < height; y++)
        {
            var px = source[(y * stride) + centerX];
            vSum += px;
            if (y > 0)
            {
                var grad = Math.Abs(px - source[((y - 1) * stride) + centerX]);
                if (grad > vPeakGrad)
                {
                    vPeakGrad = grad;
                }
            }
        }

        return new RoiHistogramProfileSummary(
            width,
            height,
            minVal,
            maxVal,
            mean,
            stdDev,
            otsuThreshold,
            Math.Max(0.0, maxBetweenVariance),
            width > 0 ? hSum / width : 0.0,
            hPeakGrad,
            height > 0 ? vSum / height : 0.0,
            vPeakGrad,
            bins);
    }

    private static int ClampPowerOfTwo(int dimension)
    {
        var pow2 = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(MinFftDimension, dimension));
        if (pow2 > MaxFftDimension)
        {
            pow2 = MaxFftDimension;
        }

        return Math.Max(MinFftDimension, pow2);
    }

    private static void Transform2DRadix2(Span<double> real, Span<double> imag, int width, int height, bool inverse)
    {
        Span<double> colReal = stackalloc double[MaxFftDimension];
        Span<double> colImag = stackalloc double[MaxFftDimension];

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            Transform1DRadix2(real.Slice(rowOffset, width), imag.Slice(rowOffset, width), width, inverse);
        }

        var colR = colReal.Slice(0, height);
        var colI = colImag.Slice(0, height);

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var idx = (y * width) + x;
                colR[y] = real[idx];
                colI[y] = imag[idx];
            }

            Transform1DRadix2(colR, colI, height, inverse);

            for (var y = 0; y < height; y++)
            {
                var idx = (y * width) + x;
                real[idx] = colR[y];
                imag[idx] = colI[y];
            }
        }
    }

    private static void Transform1DRadix2(Span<double> real, Span<double> imag, int n, bool inverse)
    {
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            while ((j & bit) != 0)
            {
                j ^= bit;
                bit >>= 1;
            }

            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        var direction = inverse ? 1.0 : -1.0;
        for (var len = 2; len <= n; len <<= 1)
        {
            var halfLen = len >> 1;
            var angle = direction * TwoPi / len;
            var wLenR = Math.Cos(angle);
            var wLenI = Math.Sin(angle);

            for (var i = 0; i < n; i += len)
            {
                var wR = 1.0;
                var wI = 0.0;

                for (var k = 0; k < halfLen; k++)
                {
                    var evenIdx = i + k;
                    var oddIdx = evenIdx + halfLen;

                    var uR = real[evenIdx];
                    var uI = imag[evenIdx];
                    var vR = (real[oddIdx] * wR) - (imag[oddIdx] * wI);
                    var vI = (real[oddIdx] * wI) + (imag[oddIdx] * wR);

                    real[evenIdx] = uR + vR;
                    imag[evenIdx] = uI + vI;
                    real[oddIdx] = uR - vR;
                    imag[oddIdx] = uI - vI;

                    var nextWR = (wR * wLenR) - (wI * wLenI);
                    wI = (wR * wLenI) + (wI * wLenR);
                    wR = nextWR;
                }
            }
        }

        if (inverse)
        {
            var invN = 1.0 / n;
            for (var i = 0; i < n; i++)
            {
                real[i] *= invN;
                imag[i] *= invN;
            }
        }
    }
}
