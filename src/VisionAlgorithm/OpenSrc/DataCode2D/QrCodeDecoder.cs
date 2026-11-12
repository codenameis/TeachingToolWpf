using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.DataCode2D;

/// <summary>
/// Commercial-grade, first-principles ISO/IEC 18004 2D QR Code detector, full 360-degree pose estimator,
/// GF(2^8) Reed-Solomon error-correcting decoder, and ISO/IEC 15415 print quality verifier.
/// </summary>
public static class QrCodeDecoder
{
    public const int DefaultMaxSymbols = 32;

    private const int MinImageDimension = 21;
    private const int HistogramBinCount = 256;
    private const int PercentileDivisor = 200;
    private const int UpperPercentileNumerator = 199;
    private const int FinderRunCount = 5;
    private const int MinCenterRunPixels = 2;
    private const int MaxFinderCandidates = 512;
    private const int MaxFinderClusters = 96;
    private const int MaxTopClustersForTriads = 48;
    private const int MaxCandidateTriads = 512;
    private const int MinFinderCount = 3;
    private const int MaxCoreFloodQueue = 4096;
    private const int MaxDimensionModules = 57;
    private const int MaxCodewordCount = 346;
    private const int FormatBitCount = 15;
    private const int VersionBitCount = 18;
    private const int BitsPerByte = 8;
    private const int ModeIndicatorBitCount = 4;
    private const int StackAllocByteLimit = 256;

    private const int ModeTerminator = 0b0000;
    private const int ModeNumeric = 0b0001;
    private const int ModeAlphanumeric = 0b0010;
    private const int ModeByte = 0b0100;
    private const int ModeEci = 0b0111;
    private const int AlphanumericRadix = 45;

    private const float HalfPixelOffset = 0.5f;
    private const float CenterCoreModuleSpan = 3.0f;
    private const float FinderModuleSpan = 7.0f;
    private const float FinderCenterModuleCoord = 3.5f;
    private const float AlignmentInsetFromEdgeModules = 6.5f;
    private const float FinderRatioTolerance = 0.65f;
    private const float DiagonalRatioTolerance = 0.72f;
    private const float DiagonalRayStepPx = 0.5f;
    private const float DiagonalInverseSqrt2 = 0.70710678f;
    private const float MinFinderTotalWidthPx = 7.0f;
    private const float MaxCrossAspectMismatchRatio = 0.45f;
    private const float ClusterMergeRadiusFactor = 0.65f;
    private const float MaxCoreIslandRadiusRatio = 0.42f;
    private const float WhiteMoatSampleRatio = 0.29f;
    private const float MaxRightAngleCosine = 0.32f;
    private const float MinLegLengthRatio = 0.70f;
    private const float MinEstimatedDimensionModules = 17.0f;
    private const float MaxEstimatedDimensionModules = 66.0f;
    private const float AlignmentSearchRadiusModules = 3.2f;
    private const float AlignmentCoreRadiusModules = 0.90f;
    private const float MinAlignmentRingContrast = 7.0f;
    private const float AlignmentDistancePenaltyWeight = 1.5f;
    private const float MinAlignmentScore = 10.0f;
    private const float LocalThresholdContrastMargin = 3.0f;
    private const float MinForegroundContrastGray = 16.0f;
    private const float MaxByteInverse = 1.0f / byte.MaxValue;
    private const float ZeroDenominatorEpsilon = 1e-6f;
    private const float DuplicateCenterDistanceRatio = 0.45f;
    private const float ReadingOrderRowBandRatio = 0.55f;

    private const float CandidateContrastRankWeight = 2.0f;
    private const float CandidateEccPenaltyWeight = 0.05f;
    private const float CandidateProximityRankWeight = 0.25f;
    private const float MeanModulationBlendWeight = 0.70f;
    private const float MinModulationBlendWeight = 0.30f;
    private const float AxialNonUniformityPenaltyScale = 4.0f;

    private const float IsoGradeAThreshold = 0.70f;
    private const float IsoGradeBThreshold = 0.55f;
    private const float IsoGradeCThreshold = 0.40f;
    private const float IsoGradeDThreshold = 0.25f;

    private readonly record struct FinderCandidate(float X, float Y, float SpanPx, float Weight);

    private readonly record struct FinderTriad(
        FinderCandidate TopLeft,
        FinderCandidate TopRight,
        FinderCandidate BottomLeft,
        int ClusterIndexA,
        int ClusterIndexB,
        int ClusterIndexC,
        float EstimatedModulePx,
        float GeometricScore);

    /// <summary>
    /// Locates, rectifies, error-corrects, and decodes an ISO/IEC 18004 QR Code symbol in <paramref name="imageView"/>,
    /// returning its decoded payload, sub-pixel world center <c>(X, Y)</c>, full 360-degree rotation angle,
    /// 4 perspective corners, and ISO/IEC 15415 print quality verification metrics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static QrCodeDecodeResult Decode(ReadOnlyImageView2D<byte> imageView)
    {
        imageView.EnsureValid();
        int width = imageView.Width;
        int height = imageView.Height;
        if (width < MinImageDimension || height < MinImageDimension)
        {
            return default;
        }

        ComputeIntensityStatistics(imageView, out byte darkPercentile, out byte lightPercentile, out float otsuThreshold);
        if ((lightPercentile - darkPercentile) < MinForegroundContrastGray)
        {
            return default;
        }

        int integralStride = width + 1;
        int integralLength = (height + 1) * integralStride;
        long[] integralPooled = ArrayPool<long>.Shared.Rent(integralLength);
        byte[] binaryPooled = ArrayPool<byte>.Shared.Rent(width * height);

        try
        {
            Span<long> integral = integralPooled.AsSpan(0, integralLength);
            Span<byte> binary = binaryPooled.AsSpan(0, width * height);

            BuildIntegralImage(imageView, integral, integralStride);

            if (TryDecodeWithPolarity(
                imageView,
                integral,
                integralStride,
                binary,
                otsuThreshold,
                isDarkOnLight: true,
                out QrCodeDecodeResult darkOnLightResult))
            {
                return darkOnLightResult;
            }

            if (TryDecodeWithPolarity(
                imageView,
                integral,
                integralStride,
                binary,
                otsuThreshold,
                isDarkOnLight: false,
                out QrCodeDecodeResult lightOnDarkResult))
            {
                return lightOnDarkResult;
            }

            return default;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(binaryPooled);
            ArrayPool<long>.Shared.Return(integralPooled);
        }
    }

    /// <summary>
    /// Locates, rectifies, error-corrects, and decodes all distinct ISO/IEC 18004 QR Code symbols inside <paramref name="imageView"/>
    /// across both Dark-on-Light and Light-on-Dark polarities, sorted in reading order (top-to-bottom row band, left-to-right).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static QrCodeDecodeResult[] DecodeAll(ReadOnlyImageView2D<byte> imageView, int maxSymbols = DefaultMaxSymbols)
    {
        imageView.EnsureValid();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSymbols);

        int width = imageView.Width;
        int height = imageView.Height;
        if (width < MinImageDimension || height < MinImageDimension)
        {
            return [];
        }

        ComputeIntensityStatistics(imageView, out byte darkPercentile, out byte lightPercentile, out float otsuThreshold);
        if ((lightPercentile - darkPercentile) < MinForegroundContrastGray)
        {
            return [];
        }

        int integralStride = width + 1;
        int integralLength = (height + 1) * integralStride;
        long[] integralPooled = ArrayPool<long>.Shared.Rent(integralLength);
        byte[] binaryPooled = ArrayPool<byte>.Shared.Rent(width * height);

        try
        {
            Span<long> integral = integralPooled.AsSpan(0, integralLength);
            Span<byte> binary = binaryPooled.AsSpan(0, width * height);

            BuildIntegralImage(imageView, integral, integralStride);

            var accepted = new List<QrCodeDecodeResult>(Math.Min(maxSymbols, 8));
            CollectSymbolsWithPolarity(
                imageView,
                integral,
                integralStride,
                binary,
                otsuThreshold,
                isDarkOnLight: true,
                maxSymbols,
                accepted);

            if (accepted.Count < maxSymbols)
            {
                CollectSymbolsWithPolarity(
                    imageView,
                    integral,
                    integralStride,
                    binary,
                    otsuThreshold,
                    isDarkOnLight: false,
                    maxSymbols,
                    accepted);
            }

            if (accepted.Count == 0)
            {
                return [];
            }

            SortResultsInReadingOrder(accepted);
            return accepted.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(binaryPooled);
            ArrayPool<long>.Shared.Return(integralPooled);
        }
    }

    /// <summary>
    /// Decodes all QR Code symbols in <paramref name="imageView"/> and returns a standardized <see cref="VisionMultiResult{TItem}"/>
    /// supporting lossless multi-envelope SPC &amp; <c>VisionCanvas</c> replay export.
    /// </summary>
    public static VisionMultiResult<QrCodeDecodeResult> DecodeMulti(ReadOnlyImageView2D<byte> imageView, int maxSymbols = DefaultMaxSymbols)
    {
        QrCodeDecodeResult[] items = DecodeAll(imageView, maxSymbols);
        return VisionMultiResult<QrCodeDecodeResult>.Create(VisionToolCategory.OcrVerification, items);
    }

    private static void CollectSymbolsWithPolarity(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<long> integral,
        int integralStride,
        Span<byte> binary,
        float globalThreshold,
        bool isDarkOnLight,
        int maxSymbols,
        List<QrCodeDecodeResult> accepted)
    {
        BinarizeAdaptiveHybrid(imageView, integral, integralStride, binary, globalThreshold, isDarkOnLight);

        FinderCandidate[] rawPooled = ArrayPool<FinderCandidate>.Shared.Rent(MaxFinderCandidates);
        FinderTriad[] triadPooled = ArrayPool<FinderTriad>.Shared.Rent(MaxCandidateTriads);
        try
        {
            Span<FinderCandidate> rawCandidates = rawPooled.AsSpan(0, MaxFinderCandidates);
            int rawCount = ScanFinderCandidates(imageView, binary, globalThreshold, isDarkOnLight, rawCandidates);
            if (rawCount < MinFinderCount)
            {
                return;
            }

            Span<FinderCandidate> clusters = stackalloc FinderCandidate[MaxFinderClusters];
            int clusterCount = ClusterAndRefineFinders(imageView, binary, rawCandidates.Slice(0, rawCount), globalThreshold, isDarkOnLight, clusters);
            if (clusterCount < MinFinderCount)
            {
                return;
            }

            Span<FinderTriad> triads = triadPooled.AsSpan(0, MaxCandidateTriads);
            int triadCount = BuildCandidateTriads(clusters.Slice(0, clusterCount), triads);
            if (triadCount == 0)
            {
                return;
            }

            ulong claimedClusterBits0 = 0UL;
            ulong claimedClusterBits1 = 0UL;

            for (int t = 0; t < triadCount && accepted.Count < maxSymbols; t++)
            {
                ref readonly FinderTriad triad = ref triads[t];
                if (IsClusterClaimed(triad.ClusterIndexA, claimedClusterBits0, claimedClusterBits1) ||
                    IsClusterClaimed(triad.ClusterIndexB, claimedClusterBits0, claimedClusterBits1) ||
                    IsClusterClaimed(triad.ClusterIndexC, claimedClusterBits0, claimedClusterBits1))
                {
                    continue;
                }

                if (!TryDecodeTriad(imageView, in triad, isDarkOnLight, out QrCodeDecodeResult candidate))
                {
                    continue;
                }

                ClaimCluster(triad.ClusterIndexA, ref claimedClusterBits0, ref claimedClusterBits1);
                ClaimCluster(triad.ClusterIndexB, ref claimedClusterBits0, ref claimedClusterBits1);
                ClaimCluster(triad.ClusterIndexC, ref claimedClusterBits0, ref claimedClusterBits1);

                MergeOrAppendSymbol(accepted, in candidate, maxSymbols);
            }
        }
        finally
        {
            ArrayPool<FinderTriad>.Shared.Return(triadPooled);
            ArrayPool<FinderCandidate>.Shared.Return(rawPooled);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsClusterClaimed(int clusterIndex, ulong bits0, ulong bits1)
        => clusterIndex < 64
            ? ((bits0 >> clusterIndex) & 1UL) != 0UL
            : ((bits1 >> (clusterIndex - 64)) & 1UL) != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClaimCluster(int clusterIndex, ref ulong bits0, ref ulong bits1)
    {
        if (clusterIndex < 64)
        {
            bits0 |= 1UL << clusterIndex;
        }
        else if (clusterIndex < 128)
        {
            bits1 |= 1UL << (clusterIndex - 64);
        }
    }

    private static void MergeOrAppendSymbol(List<QrCodeDecodeResult> accepted, in QrCodeDecodeResult candidate, int maxSymbols)
    {
        float candSpan = MathF.Min(candidate.WidthPixels, candidate.HeightPixels);
        for (int i = 0; i < accepted.Count; i++)
        {
            QrCodeDecodeResult existing = accepted[i];
            float minSpan = MathF.Max(MinFinderTotalWidthPx, MathF.Min(candSpan, MathF.Min(existing.WidthPixels, existing.HeightPixels)));
            if (Vector2.Distance(candidate.CenterWorld, existing.CenterWorld) <= (minSpan * DuplicateCenterDistanceRatio))
            {
                if (ComputeSymbolQualityRank(in candidate) > ComputeSymbolQualityRank(in existing))
                {
                    accepted[i] = candidate;
                }

                return;
            }
        }

        if (accepted.Count < maxSymbols)
        {
            accepted.Add(candidate);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeSymbolQualityRank(in QrCodeDecodeResult symbol)
        => (symbol.SymbolContrast * CandidateContrastRankWeight)
           + symbol.ModulationScore
           + symbol.FixedPatternDamageScore
           - (symbol.CorrectedByteErrors * CandidateEccPenaltyWeight);

    private static void SortResultsInReadingOrder(List<QrCodeDecodeResult> results)
    {
        if (results.Count <= 1)
        {
            return;
        }

        results.Sort(static (a, b) =>
        {
            float rowBand = MathF.Max(MinFinderTotalWidthPx, MathF.Min(a.HeightPixels, b.HeightPixels) * ReadingOrderRowBandRatio);
            float dy = a.CenterWorld.Y - b.CenterWorld.Y;
            if (MathF.Abs(dy) > rowBand)
            {
                return dy < 0.0f ? -1 : 1;
            }

            return a.CenterWorld.X.CompareTo(b.CenterWorld.X);
        });
    }

    private static bool TryDecodeWithPolarity(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<long> integral,
        int integralStride,
        Span<byte> binary,
        float globalThreshold,
        bool isDarkOnLight,
        out QrCodeDecodeResult bestResult)
    {
        bestResult = default;
        int width = imageView.Width;
        int height = imageView.Height;

        BinarizeAdaptiveHybrid(imageView, integral, integralStride, binary, globalThreshold, isDarkOnLight);

        FinderCandidate[] rawPooled = ArrayPool<FinderCandidate>.Shared.Rent(MaxFinderCandidates);
        FinderTriad[] triadPooled = ArrayPool<FinderTriad>.Shared.Rent(MaxCandidateTriads);
        try
        {
            Span<FinderCandidate> rawCandidates = rawPooled.AsSpan(0, MaxFinderCandidates);
            int rawCount = ScanFinderCandidates(imageView, binary, globalThreshold, isDarkOnLight, rawCandidates);
            if (rawCount < MinFinderCount)
            {
                return false;
            }

            Span<FinderCandidate> clusters = stackalloc FinderCandidate[MaxFinderClusters];
            int clusterCount = ClusterAndRefineFinders(imageView, binary, rawCandidates.Slice(0, rawCount), globalThreshold, isDarkOnLight, clusters);
            if (clusterCount < MinFinderCount)
            {
                return false;
            }

            Span<FinderTriad> triads = triadPooled.AsSpan(0, MaxCandidateTriads);
            int triadCount = BuildCandidateTriads(clusters.Slice(0, clusterCount), triads);
            if (triadCount == 0)
            {
                return false;
            }

            Vector2 roiCenter = new(width * HalfPixelOffset, height * HalfPixelOffset);
            float maxDiag = MathF.Max(1.0f, MathF.Sqrt((width * width) + (height * height)));
            float bestRankScore = float.NegativeInfinity;

            for (int t = 0; t < triadCount; t++)
            {
                if (!TryDecodeTriad(imageView, in triads[t], isDarkOnLight, out QrCodeDecodeResult candidate))
                {
                    continue;
                }

                float proximityBonus = 1.0f - (Vector2.Distance(candidate.CenterWorld, roiCenter) / maxDiag);
                float rankScore = ComputeSymbolQualityRank(in candidate) + (proximityBonus * CandidateProximityRankWeight);

                if (!bestResult.IsDecoded || rankScore > bestRankScore)
                {
                    bestRankScore = rankScore;
                    bestResult = candidate;
                }
            }

            return bestResult.IsDecoded;
        }
        finally
        {
            ArrayPool<FinderTriad>.Shared.Return(triadPooled);
            ArrayPool<FinderCandidate>.Shared.Return(rawPooled);
        }
    }

    private static void ComputeIntensityStatistics(
        ReadOnlyImageView2D<byte> imageView,
        out byte p5Dark,
        out byte p95Light,
        out float otsuThreshold)
    {
        Span<int> hist = stackalloc int[HistogramBinCount];
        hist.Clear();

        int width = imageView.Width;
        int height = imageView.Height;
        int stride = imageView.Stride;
        ReadOnlySpan<byte> pixels = imageView.Buffer;

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, width);
            for (int x = 0; x < width; x++)
            {
                hist[row[x]]++;
            }
        }

        int total = width * height;
        int targetLow = Math.Max(1, total / PercentileDivisor);
        int targetHigh = Math.Max(1, (total * UpperPercentileNumerator) / PercentileDivisor);

        int accum = 0;
        p5Dark = 0;
        for (int i = 0; i < HistogramBinCount; i++)
        {
            accum += hist[i];
            if (accum >= targetLow)
            {
                p5Dark = (byte)i;
                break;
            }
        }

        accum = 0;
        p95Light = byte.MaxValue;
        for (int i = 0; i < HistogramBinCount; i++)
        {
            accum += hist[i];
            if (accum >= targetHigh)
            {
                p95Light = (byte)i;
                break;
            }
        }

        double totalSum = 0.0;
        for (int i = 0; i < HistogramBinCount; i++)
        {
            totalSum += (double)i * hist[i];
        }

        double sumBg = 0.0;
        int weightBg = 0;
        double maxVariance = -1.0;
        int bestThreshold = (p5Dark + p95Light) / 2;

        for (int t = 0; t < HistogramBinCount - 1; t++)
        {
            weightBg += hist[t];
            if (weightBg == 0)
            {
                continue;
            }

            int weightFg = total - weightBg;
            if (weightFg == 0)
            {
                break;
            }

            sumBg += (double)t * hist[t];
            double meanBg = sumBg / weightBg;
            double meanFg = (totalSum - sumBg) / weightFg;
            double diff = meanBg - meanFg;
            double betweenVar = (double)weightBg * weightFg * diff * diff;
            if (betweenVar > maxVariance)
            {
                maxVariance = betweenVar;
                bestThreshold = t;
            }
        }

        otsuThreshold = bestThreshold + HalfPixelOffset;
    }

    private static void BuildIntegralImage(ReadOnlyImageView2D<byte> imageView, Span<long> integral, int integralStride)
    {
        int width = imageView.Width;
        int height = imageView.Height;
        int stride = imageView.Stride;
        ReadOnlySpan<byte> pixels = imageView.Buffer;

        integral.Slice(0, integralStride).Clear();

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = pixels.Slice(y * stride, width);
            ReadOnlySpan<long> prevIntRow = integral.Slice(y * integralStride, integralStride);
            Span<long> dstIntRow = integral.Slice((y + 1) * integralStride, integralStride);
            dstIntRow[0] = 0;

            long rowAccum = 0;
            for (int x = 0; x < width; x++)
            {
                rowAccum += srcRow[x];
                dstIntRow[x + 1] = prevIntRow[x + 1] + rowAccum;
            }
        }
    }

    private static void BinarizeAdaptiveHybrid(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<long> integral,
        int integralStride,
        Span<byte> binary,
        float globalThreshold,
        bool isDarkOnLight)
    {
        int width = imageView.Width;
        int height = imageView.Height;
        int stride = imageView.Stride;
        ReadOnlySpan<byte> pixels = imageView.Buffer;

        const int minWindowHalf = 10;
        const int maxWindowHalf = 48;
        int halfWin = Math.Clamp(Math.Min(width, height) / BitsPerByte, minWindowHalf, maxWindowHalf);

        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - halfWin);
            int y1 = Math.Min(height, y + halfWin + 1);
            ReadOnlySpan<long> intTop = integral.Slice(y0 * integralStride, integralStride);
            ReadOnlySpan<long> intBot = integral.Slice(y1 * integralStride, integralStride);
            ReadOnlySpan<byte> srcRow = pixels.Slice(y * stride, width);
            Span<byte> dstRow = binary.Slice(y * width, width);

            int spanH = y1 - y0;
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - halfWin);
                int x1 = Math.Min(width, x + halfWin + 1);
                long boxSum = (intBot[x1] - intBot[x0]) - (intTop[x1] - intTop[x0]);
                float localMean = (float)boxSum / (spanH * (x1 - x0));

                float thresh = localMean;
                byte val = srcRow[x];
                bool isForeground = isDarkOnLight
                    ? (val <= (thresh - LocalThresholdContrastMargin) || val <= (globalThreshold - LocalThresholdContrastMargin))
                    : (val >= (thresh + LocalThresholdContrastMargin) || val >= (globalThreshold + LocalThresholdContrastMargin));
                dstRow[x] = isForeground ? (byte)1 : (byte)0;
            }
        }
    }

    private static int ScanFinderCandidates(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<byte> binary,
        float globalThreshold,
        bool isDarkOnLight,
        Span<FinderCandidate> candidates)
    {
        int width = imageView.Width;
        int height = imageView.Height;
        int count = 0;

        Span<int> runs = stackalloc int[FinderRunCount];

        for (int y = 1; y < height - 1; y++)
        {
            ReadOnlySpan<byte> binRow = binary.Slice(y * width, width);
            runs.Clear();
            int state = 0;

            for (int x = 0; x < width; x++)
            {
                bool fg = binRow[x] != 0;
                if (fg)
                {
                    if ((state & 1) == 1)
                    {
                        state++;
                    }

                    runs[state]++;
                }
                else
                {
                    if ((state & 1) == 1)
                    {
                        runs[state]++;
                    }
                    else
                    {
                        if (state == 4)
                        {
                            if (IsFinderRatioValid(runs, FinderRatioTolerance, out float totalW) &&
                                TryVerifyAndRefineFinderCross(imageView, binary, x, y, totalW, globalThreshold, isDarkOnLight, out FinderCandidate verified))
                            {
                                AppendOrMergeCandidate(candidates, ref count, verified);
                            }

                            runs[0] = runs[2];
                            runs[1] = runs[3];
                            runs[2] = runs[4];
                            runs[3] = 1;
                            runs[4] = 0;
                            state = 3;
                        }
                        else
                        {
                            state++;
                            runs[state]++;
                        }
                    }
                }
            }

            if (state == 4 &&
                IsFinderRatioValid(runs, FinderRatioTolerance, out float tailW) &&
                TryVerifyAndRefineFinderCross(imageView, binary, width, y, tailW, globalThreshold, isDarkOnLight, out FinderCandidate tailVerified))
            {
                AppendOrMergeCandidate(candidates, ref count, tailVerified);
            }
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFinderRatioValid(ReadOnlySpan<int> runs, float ratioTolerance, out float totalWidth)
    {
        int n0 = runs[0];
        int n1 = runs[1];
        int n2 = runs[2];
        int n3 = runs[3];
        int n4 = runs[4];
        int sum = n0 + n1 + n2 + n3 + n4;
        totalWidth = sum;

        if (sum < MinFinderTotalWidthPx || n0 == 0 || n1 == 0 || n2 < MinCenterRunPixels || n3 == 0 || n4 == 0)
        {
            return false;
        }

        float moduleSize = sum / FinderModuleSpan;
        float maxVar = moduleSize * ratioTolerance;
        return MathF.Abs(n0 - moduleSize) <= maxVar
            && MathF.Abs(n1 - moduleSize) <= maxVar
            && MathF.Abs(n2 - (CenterCoreModuleSpan * moduleSize)) <= (CenterCoreModuleSpan * maxVar)
            && MathF.Abs(n3 - moduleSize) <= maxVar
            && MathF.Abs(n4 - moduleSize) <= maxVar;
    }

    private static bool TryVerifyAndRefineFinderCross(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<byte> binary,
        int endExclusiveX,
        int rowY,
        float horizTotalW,
        float globalThreshold,
        bool isDarkOnLight,
        out FinderCandidate candidate)
    {
        candidate = default;
        int width = imageView.Width;
        int height = imageView.Height;

        int startX = endExclusiveX - (int)horizTotalW;
        float centerX = ComputeSubPixelRunCenterHorizontal(imageView, rowY, startX, endExclusiveX, globalThreshold, isDarkOnLight);
        int centerCol = Math.Clamp((int)MathF.Floor(centerX), 0, width - 1);
        if (binary[(rowY * width) + centerCol] == 0)
        {
            return false;
        }

        const float maxScanMultiplier = 1.5f;
        int maxRadius = Math.Max(BitsPerByte, (int)MathF.Ceiling(horizTotalW * maxScanMultiplier));
        if (!MeasureSymmetricAxisFiveRuns(binary, width, height, centerCol, rowY, dx: 0, dy: 1, maxRadius, out int topStart, out int botEndExclusive, out float vertSpan))
        {
            return false;
        }

        if (MathF.Abs(vertSpan - horizTotalW) > MathF.Max(horizTotalW, vertSpan) * MaxCrossAspectMismatchRatio)
        {
            return false;
        }

        float centerY = ComputeSubPixelRunCenterVertical(imageView, centerCol, topStart, botEndExclusive, globalThreshold, isDarkOnLight);
        int centerRow = Math.Clamp((int)MathF.Floor(centerY), 0, height - 1);

        if (centerRow != rowY &&
            MeasureSymmetricAxisFiveRuns(binary, width, height, centerCol, centerRow, dx: 1, dy: 0, maxRadius, out int refLeft, out int refRightExcl, out float refHorizSpan))
        {
            centerX = ComputeSubPixelRunCenterHorizontal(imageView, centerRow, refLeft, refRightExcl, globalThreshold, isDarkOnLight);
            horizTotalW = refHorizSpan;
        }

        float avgSpan = HalfPixelOffset * (horizTotalW + vertSpan);

        // [Math Why]: Sub-pixel half-pixel stepping along both +-45 deg rays prevents 8-connected diagonal lattice steps
        // from jumping across thin 1-module white rings when a QR symbol is rotated near 45 or 135 degrees.
        float maxRayDist = avgSpan * maxScanMultiplier;
        if (!VerifyDiagonalRaySubPixel(imageView, centerX, centerY, DiagonalInverseSqrt2, DiagonalInverseSqrt2, maxRayDist, globalThreshold, isDarkOnLight) ||
            !VerifyDiagonalRaySubPixel(imageView, centerX, centerY, DiagonalInverseSqrt2, -DiagonalInverseSqrt2, maxRayDist, globalThreshold, isDarkOnLight))
        {
            return false;
        }

        candidate = new FinderCandidate(centerX, centerY, avgSpan, Weight: 1.0f);
        return true;
    }

    private static bool VerifyDiagonalRaySubPixel(
        ReadOnlyImageView2D<byte> imageView,
        float cx,
        float cy,
        float dirX,
        float dirY,
        float maxDistancePx,
        float globalThreshold,
        bool isDarkOnLight)
    {
        int width = imageView.Width;
        int height = imageView.Height;
        int maxSteps = Math.Max(14, (int)MathF.Ceiling(maxDistancePx / DiagonalRayStepPx));

        float centerVal = SampleBilinearWorld(imageView, cx, cy);
        float thresh = HalfPixelOffset * (centerVal + globalThreshold);
        if (isDarkOnLight)
        {
            thresh = MathF.Max(thresh, globalThreshold);
        }
        else
        {
            thresh = MathF.Min(thresh, globalThreshold);
        }

        Span<int> runs = stackalloc int[FinderRunCount];
        runs[2] = 1;

        // Negative ray (-dirX, -dirY)
        int steps = 1;
        while (steps <= maxSteps && IsForegroundSample(imageView, cx - (steps * DiagonalRayStepPx * dirX), cy - (steps * DiagonalRayStepPx * dirY), width, height, thresh, isDarkOnLight, out bool inBounds) && inBounds)
        {
            runs[2]++;
            steps++;
        }

        while (steps <= maxSteps && !IsForegroundSample(imageView, cx - (steps * DiagonalRayStepPx * dirX), cy - (steps * DiagonalRayStepPx * dirY), width, height, thresh, isDarkOnLight, out bool inBounds) && inBounds)
        {
            runs[1]++;
            steps++;
        }

        if (runs[1] == 0)
        {
            return false;
        }

        while (steps <= maxSteps && IsForegroundSample(imageView, cx - (steps * DiagonalRayStepPx * dirX), cy - (steps * DiagonalRayStepPx * dirY), width, height, thresh, isDarkOnLight, out bool inBounds) && inBounds)
        {
            runs[0]++;
            steps++;
        }

        if (runs[0] == 0)
        {
            return false;
        }

        // Positive ray (+dirX, +dirY)
        steps = 1;
        while (steps <= maxSteps && IsForegroundSample(imageView, cx + (steps * DiagonalRayStepPx * dirX), cy + (steps * DiagonalRayStepPx * dirY), width, height, thresh, isDarkOnLight, out bool inBounds) && inBounds)
        {
            runs[2]++;
            steps++;
        }

        while (steps <= maxSteps && !IsForegroundSample(imageView, cx + (steps * DiagonalRayStepPx * dirX), cy + (steps * DiagonalRayStepPx * dirY), width, height, thresh, isDarkOnLight, out bool inBounds) && inBounds)
        {
            runs[3]++;
            steps++;
        }

        if (runs[3] == 0)
        {
            return false;
        }

        while (steps <= maxSteps && IsForegroundSample(imageView, cx + (steps * DiagonalRayStepPx * dirX), cy + (steps * DiagonalRayStepPx * dirY), width, height, thresh, isDarkOnLight, out bool inBounds) && inBounds)
        {
            runs[4]++;
            steps++;
        }

        if (runs[4] == 0)
        {
            return false;
        }

        return IsFinderRatioValid(runs, DiagonalRatioTolerance, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsForegroundSample(
        ReadOnlyImageView2D<byte> imageView,
        float wx,
        float wy,
        int width,
        int height,
        float threshold,
        bool isDarkOnLight,
        out bool inBounds)
    {
        if (wx < 0.0f || wx >= width || wy < 0.0f || wy >= height)
        {
            inBounds = false;
            return false;
        }

        inBounds = true;
        float gray = SampleBilinearWorld(imageView, wx, wy);
        return isDarkOnLight ? (gray <= threshold) : (gray >= threshold);
    }

    private static bool MeasureSymmetricAxisFiveRuns(
        ReadOnlySpan<byte> binary,
        int width,
        int height,
        int cx,
        int cy,
        int dx,
        int dy,
        int maxSteps,
        out int negOuterStart,
        out int posOuterEndExclusive,
        out float stepSpan)
    {
        negOuterStart = 0;
        posOuterEndExclusive = 0;
        stepSpan = 0.0f;

        if (binary[(cy * width) + cx] == 0)
        {
            return false;
        }

        Span<int> runs = stackalloc int[FinderRunCount];
        runs[2] = 1;

        int x = cx - dx;
        int y = cy - dy;
        int steps = 0;
        while ((uint)x < (uint)width && (uint)y < (uint)height && binary[(y * width) + x] != 0 && steps <= maxSteps)
        {
            runs[2]++;
            x -= dx;
            y -= dy;
            steps++;
        }

        if ((uint)x >= (uint)width || (uint)y >= (uint)height || steps > maxSteps)
        {
            return false;
        }

        while ((uint)x < (uint)width && (uint)y < (uint)height && binary[(y * width) + x] == 0 && steps <= maxSteps)
        {
            runs[1]++;
            x -= dx;
            y -= dy;
            steps++;
        }

        if (runs[1] == 0 || (uint)x >= (uint)width || (uint)y >= (uint)height || steps > maxSteps)
        {
            return false;
        }

        int lastOuterNegCoord = dx != 0 ? x : y;
        while ((uint)x < (uint)width && (uint)y < (uint)height && binary[(y * width) + x] != 0 && steps <= maxSteps)
        {
            lastOuterNegCoord = dx != 0 ? x : y;
            runs[0]++;
            x -= dx;
            y -= dy;
            steps++;
        }

        if (runs[0] == 0 || steps > maxSteps)
        {
            return false;
        }

        x = cx + dx;
        y = cy + dy;
        steps = 0;
        while ((uint)x < (uint)width && (uint)y < (uint)height && binary[(y * width) + x] != 0 && steps <= maxSteps)
        {
            runs[2]++;
            x += dx;
            y += dy;
            steps++;
        }

        if ((uint)x >= (uint)width || (uint)y >= (uint)height || steps > maxSteps)
        {
            return false;
        }

        while ((uint)x < (uint)width && (uint)y < (uint)height && binary[(y * width) + x] == 0 && steps <= maxSteps)
        {
            runs[3]++;
            x += dx;
            y += dy;
            steps++;
        }

        if (runs[3] == 0 || (uint)x >= (uint)width || (uint)y >= (uint)height || steps > maxSteps)
        {
            return false;
        }

        int lastOuterPosCoord = dx != 0 ? x : y;
        while ((uint)x < (uint)width && (uint)y < (uint)height && binary[(y * width) + x] != 0 && steps <= maxSteps)
        {
            lastOuterPosCoord = dx != 0 ? x : y;
            runs[4]++;
            x += dx;
            y += dy;
            steps++;
        }

        if (runs[4] == 0 || steps > maxSteps)
        {
            return false;
        }

        if (!IsFinderRatioValid(runs, FinderRatioTolerance, out float rawSum))
        {
            return false;
        }

        negOuterStart = lastOuterNegCoord;
        posOuterEndExclusive = lastOuterPosCoord + 1;
        stepSpan = rawSum;
        return true;
    }

    private static float ComputeSubPixelRunCenterHorizontal(
        ReadOnlyImageView2D<byte> imageView,
        int rowY,
        int startX,
        int endExclusiveX,
        float threshold,
        bool isDarkOnLight)
    {
        ReadOnlySpan<byte> row = imageView.Buffer.Slice(rowY * imageView.Stride, imageView.Width);
        float leftEdge = startX;
        if (startX > 0 && startX < imageView.Width)
        {
            float outside = row[startX - 1];
            float inside = row[startX];
            float localMid = HalfPixelOffset * (outside + inside);
            float t = isDarkOnLight ? MathF.Min(threshold, localMid) : MathF.Max(threshold, localMid);
            float denom = outside - inside;
            if (MathF.Abs(denom) > ZeroDenominatorEpsilon)
            {
                float frac = Math.Clamp((outside - t) / denom, 0.0f, 1.0f);
                leftEdge = (startX - HalfPixelOffset) + frac;
            }
        }

        float rightEdge = endExclusiveX;
        if (endExclusiveX > 0 && endExclusiveX < imageView.Width)
        {
            float inside = row[endExclusiveX - 1];
            float outside = row[endExclusiveX];
            float localMid = HalfPixelOffset * (outside + inside);
            float t = isDarkOnLight ? MathF.Min(threshold, localMid) : MathF.Max(threshold, localMid);
            float denom = inside - outside;
            if (MathF.Abs(denom) > ZeroDenominatorEpsilon)
            {
                float frac = Math.Clamp((inside - t) / denom, 0.0f, 1.0f);
                rightEdge = (endExclusiveX - HalfPixelOffset) + frac;
            }
        }

        return HalfPixelOffset * (leftEdge + rightEdge);
    }

    private static float ComputeSubPixelRunCenterVertical(
        ReadOnlyImageView2D<byte> imageView,
        int colX,
        int startY,
        int endExclusiveY,
        float threshold,
        bool isDarkOnLight)
    {
        int stride = imageView.Stride;
        ReadOnlySpan<byte> buf = imageView.Buffer;

        float topEdge = startY;
        if (startY > 0 && startY < imageView.Height)
        {
            float outside = buf[((startY - 1) * stride) + colX];
            float inside = buf[(startY * stride) + colX];
            float localMid = HalfPixelOffset * (outside + inside);
            float t = isDarkOnLight ? MathF.Min(threshold, localMid) : MathF.Max(threshold, localMid);
            float denom = outside - inside;
            if (MathF.Abs(denom) > ZeroDenominatorEpsilon)
            {
                float frac = Math.Clamp((outside - t) / denom, 0.0f, 1.0f);
                topEdge = (startY - HalfPixelOffset) + frac;
            }
        }

        float botEdge = endExclusiveY;
        if (endExclusiveY > 0 && endExclusiveY < imageView.Height)
        {
            float inside = buf[((endExclusiveY - 1) * stride) + colX];
            float outside = buf[(endExclusiveY * stride) + colX];
            float localMid = HalfPixelOffset * (outside + inside);
            float t = isDarkOnLight ? MathF.Min(threshold, localMid) : MathF.Max(threshold, localMid);
            float denom = inside - outside;
            if (MathF.Abs(denom) > ZeroDenominatorEpsilon)
            {
                float frac = Math.Clamp((inside - t) / denom, 0.0f, 1.0f);
                botEdge = (endExclusiveY - HalfPixelOffset) + frac;
            }
        }

        return HalfPixelOffset * (topEdge + botEdge);
    }

    private static void AppendOrMergeCandidate(Span<FinderCandidate> list, ref int count, FinderCandidate item)
    {
        for (int i = 0; i < count; i++)
        {
            float dx = item.X - list[i].X;
            float dy = item.Y - list[i].Y;
            float avgSpan = HalfPixelOffset * (item.SpanPx + list[i].SpanPx);
            float radius = avgSpan * ClusterMergeRadiusFactor;
            if (((dx * dx) + (dy * dy)) <= (radius * radius))
            {
                float w0 = list[i].Weight;
                float wSum = w0 + item.Weight;
                float invW = 1.0f / wSum;
                list[i] = new FinderCandidate(
                    ((list[i].X * w0) + (item.X * item.Weight)) * invW,
                    ((list[i].Y * w0) + (item.Y * item.Weight)) * invW,
                    ((list[i].SpanPx * w0) + (item.SpanPx * item.Weight)) * invW,
                    wSum);
                return;
            }
        }

        if (count < list.Length)
        {
            list[count++] = item;
        }
    }

    private static int ClusterAndRefineFinders(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<byte> binary,
        ReadOnlySpan<FinderCandidate> raw,
        float globalThreshold,
        bool isDarkOnLight,
        Span<FinderCandidate> clusters)
    {
        int count = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            AppendOrMergeCandidate(clusters, ref count, raw[i]);
        }

        for (int i = 1; i < count; i++)
        {
            FinderCandidate key = clusters[i];
            int j = i - 1;
            while (j >= 0 && clusters[j].Weight < key.Weight)
            {
                clusters[j + 1] = clusters[j];
                j--;
            }

            clusters[j + 1] = key;
        }

        for (int i = 0; i < count; i++)
        {
            clusters[i] = RefineFinderCoreIslandSubPixel(imageView, binary, clusters[i], globalThreshold, isDarkOnLight);
        }

        return count;
    }

    /// <summary>
    /// Refines a finder pattern center to sub-pixel precision (&lt; 0.03 px) by flood-collecting its 4-connected
    /// 3x3 central island (which is completely isolated by the surrounding 1-module white moat at every rotation angle)
    /// plus its 1-pixel anti-aliased boundary skirt, weighted by continuous local contrast.
    /// </summary>
    private static FinderCandidate RefineFinderCoreIslandSubPixel(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<byte> binary,
        FinderCandidate initial,
        float globalThreshold,
        bool isDarkOnLight)
    {
        int width = imageView.Width;
        int height = imageView.Height;
        int stride = imageView.Stride;
        ReadOnlySpan<byte> buf = imageView.Buffer;

        int seedX = Math.Clamp((int)MathF.Floor(initial.X), 0, width - 1);
        int seedY = Math.Clamp((int)MathF.Floor(initial.Y), 0, height - 1);
        if (binary[(seedY * width) + seedX] == 0)
        {
            return initial;
        }

        int maxHalfBox = Math.Clamp((int)MathF.Ceiling(initial.SpanPx * MaxCoreIslandRadiusRatio), 3, 28);
        int minX = Math.Max(0, seedX - maxHalfBox);
        int maxX = Math.Min(width - 1, seedX + maxHalfBox);
        int minY = Math.Max(0, seedY - maxHalfBox);
        int maxY = Math.Min(height - 1, seedY + maxHalfBox);
        int boxW = (maxX - minX) + 1;
        int boxH = (maxY - minY) + 1;
        if ((boxW * boxH) > MaxCoreFloodQueue)
        {
            return initial;
        }

        Span<byte> visited = stackalloc byte[MaxCoreFloodQueue].Slice(0, boxW * boxH);
        visited.Clear();
        Span<int> queueX = stackalloc int[MaxCoreFloodQueue];
        Span<int> queueY = stackalloc int[MaxCoreFloodQueue];

        int head = 0;
        int tail = 0;
        queueX[tail] = seedX;
        queueY[tail] = seedY;
        tail++;
        visited[((seedY - minY) * boxW) + (seedX - minX)] = 1;

        bool hitBoxBorder = false;
        ReadOnlySpan<int> dirX = [-1, 1, 0, 0];
        ReadOnlySpan<int> dirY = [0, 0, -1, 1];

        while (head < tail)
        {
            int cx = queueX[head];
            int cy = queueY[head];
            head++;

            if (cx == minX || cx == maxX || cy == minY || cy == maxY)
            {
                hitBoxBorder = true;
            }

            for (int d = 0; d < 4; d++)
            {
                int nx = cx + dirX[d];
                int ny = cy + dirY[d];
                if (nx < minX || nx > maxX || ny < minY || ny > maxY)
                {
                    continue;
                }

                int vIdx = ((ny - minY) * boxW) + (nx - minX);
                if (visited[vIdx] != 0)
                {
                    continue;
                }

                if (binary[(ny * width) + nx] != 0)
                {
                    visited[vIdx] = 1;
                    if (tail < MaxCoreFloodQueue)
                    {
                        queueX[tail] = nx;
                        queueY[tail] = ny;
                        tail++;
                    }
                }
                else
                {
                    // Mark immediate 1-pixel anti-aliased skirt around the 3x3 island so fractional edge coverage is integrated symmetrically.
                    visited[vIdx] = 2;
                }
            }
        }

        if (hitBoxBorder)
        {
            return initial;
        }

        float moatR = MathF.Max(2.0f, initial.SpanPx * WhiteMoatSampleRatio);
        float moatGray = 0.25f * (
            SampleBilinearWorld(imageView, initial.X + moatR, initial.Y) +
            SampleBilinearWorld(imageView, initial.X - moatR, initial.Y) +
            SampleBilinearWorld(imageView, initial.X, initial.Y + moatR) +
            SampleBilinearWorld(imageView, initial.X, initial.Y - moatR));
        float coreGray = buf[(seedY * stride) + seedX];
        float contrast = isDarkOnLight ? (moatGray - coreGray) : (coreGray - moatGray);
        float refBg = contrast > MinForegroundContrastGray
            ? moatGray
            : (isDarkOnLight ? MathF.Max(moatGray, globalThreshold) : MathF.Min(moatGray, globalThreshold));

        double sumW = 0.0;
        double sumX = 0.0;
        double sumY = 0.0;

        for (int ly = 0; ly < boxH; ly++)
        {
            int wy = minY + ly;
            ReadOnlySpan<byte> row = buf.Slice(wy * stride, width);
            for (int lx = 0; lx < boxW; lx++)
            {
                if (visited[(ly * boxW) + lx] == 0)
                {
                    continue;
                }

                int wx = minX + lx;
                float gray = row[wx];
                float excess = isDarkOnLight ? (refBg - gray) : (gray - refBg);
                if (excess > 0.0f)
                {
                    float px = wx + HalfPixelOffset;
                    float py = wy + HalfPixelOffset;
                    sumW += excess;
                    sumX += excess * px;
                    sumY += excess * py;
                }
            }
        }

        if (sumW <= ZeroDenominatorEpsilon)
        {
            return initial;
        }

        return initial with
        {
            X = (float)(sumX / sumW),
            Y = (float)(sumY / sumW)
        };
    }

    private static int BuildCandidateTriads(ReadOnlySpan<FinderCandidate> clusters, Span<FinderTriad> triads)
    {
        int n = Math.Min(clusters.Length, MaxTopClustersForTriads);
        int triadCount = 0;

        for (int i = 0; i < n - 2; i++)
        {
            for (int j = i + 1; j < n - 1; j++)
            {
                for (int k = j + 1; k < n; k++)
                {
                    if (!TryOrderRightIsoscelesTriad(clusters[i], clusters[j], clusters[k], i, j, k, out FinderTriad triad))
                    {
                        continue;
                    }

                    if (triadCount < triads.Length)
                    {
                        triads[triadCount++] = triad;
                    }
                }
            }
        }

        for (int i = 1; i < triadCount; i++)
        {
            FinderTriad key = triads[i];
            int j = i - 1;
            while (j >= 0 && triads[j].GeometricScore < key.GeometricScore)
            {
                triads[j + 1] = triads[j];
                j--;
            }

            triads[j + 1] = key;
        }

        return triadCount;
    }

    private static bool TryOrderRightIsoscelesTriad(
        FinderCandidate a,
        FinderCandidate b,
        FinderCandidate c,
        int indexA,
        int indexB,
        int indexC,
        out FinderTriad triad)
    {
        triad = default;

        float avgSpan = (a.SpanPx + b.SpanPx + c.SpanPx) / CenterCoreModuleSpan;
        float maxSpanDiff = MathF.Max(MathF.Abs(a.SpanPx - avgSpan), MathF.Max(MathF.Abs(b.SpanPx - avgSpan), MathF.Abs(c.SpanPx - avgSpan)));
        if (maxSpanDiff > (avgSpan * MaxCrossAspectMismatchRatio))
        {
            return false;
        }

        Vector2 pa = new(a.X, a.Y);
        Vector2 pb = new(b.X, b.Y);
        Vector2 pc = new(c.X, c.Y);

        float dab = Vector2.DistanceSquared(pa, pb);
        float dbc = Vector2.DistanceSquared(pb, pc);
        float dca = Vector2.DistanceSquared(pc, pa);

        FinderCandidate topLeft;
        FinderCandidate p1;
        FinderCandidate p2;
        if (dab >= dbc && dab >= dca)
        {
            topLeft = c;
            p1 = a;
            p2 = b;
        }
        else if (dbc >= dab && dbc >= dca)
        {
            topLeft = a;
            p1 = b;
            p2 = c;
        }
        else
        {
            topLeft = b;
            p1 = a;
            p2 = c;
        }

        Vector2 vtl = new(topLeft.X, topLeft.Y);
        Vector2 u = new Vector2(p1.X, p1.Y) - vtl;
        Vector2 v = new Vector2(p2.X, p2.Y) - vtl;

        float lenU = u.Length();
        float lenV = v.Length();
        if (lenU < MinFinderTotalWidthPx || lenV < MinFinderTotalWidthPx)
        {
            return false;
        }

        float legRatio = MathF.Min(lenU, lenV) / MathF.Max(lenU, lenV);
        if (legRatio < MinLegLengthRatio)
        {
            return false;
        }

        float cosAngle = MathF.Abs(Vector2.Dot(u, v) / (lenU * lenV));
        if (cosAngle > MaxRightAngleCosine)
        {
            return false;
        }

        float cross = (u.X * v.Y) - (u.Y * v.X);
        FinderCandidate topRight;
        FinderCandidate bottomLeft;
        if (cross >= 0.0f)
        {
            topRight = p1;
            bottomLeft = p2;
        }
        else
        {
            topRight = p2;
            bottomLeft = p1;
            (u, v) = (v, u);
            (lenU, lenV) = (lenV, lenU);
        }

        float theta0 = MathF.Atan2(u.Y - v.X, u.X + v.Y);
        (float sinT, float cosT) = MathF.SinCos(theta0);
        float secantInv = MathF.Max(MathF.Abs(cosT), MathF.Abs(sinT));
        float estModulePx = MathF.Max(1.0f, (avgSpan * secantInv) / FinderModuleSpan);

        float avgLeg = HalfPixelOffset * (lenU + lenV);
        float estDimension = (avgLeg / estModulePx) + FinderModuleSpan;
        if (estDimension < MinEstimatedDimensionModules || estDimension > MaxEstimatedDimensionModules)
        {
            return false;
        }

        const float geomScoreScale = 10.0f;
        const float maxWeightCap = 30.0f;
        float weightSum = topLeft.Weight + topRight.Weight + bottomLeft.Weight;
        float geomScore = (legRatio * (1.0f - cosAngle) * geomScoreScale) + MathF.Min(weightSum, maxWeightCap);
        triad = new FinderTriad(topLeft, topRight, bottomLeft, indexA, indexB, indexC, estModulePx, geomScore);
        return true;
    }

    private static bool TryDecodeTriad(
        ReadOnlyImageView2D<byte> imageView,
        in FinderTriad triad,
        bool isDarkOnLight,
        out QrCodeDecodeResult result)
    {
        result = default;

        Vector2 ftl = new(triad.TopLeft.X, triad.TopLeft.Y);
        Vector2 ftr = new(triad.TopRight.X, triad.TopRight.Y);
        Vector2 fbl = new(triad.BottomLeft.X, triad.BottomLeft.Y);

        const float versionQuarterStep = 0.25f;
        float avgLegPx = HalfPixelOffset * (Vector2.Distance(ftl, ftr) + Vector2.Distance(ftl, fbl));
        float rawModulesBetweenFinders = avgLegPx / triad.EstimatedModulePx;
        int estVersion = Math.Clamp(
            (int)MathF.Round((rawModulesBetweenFinders + FinderModuleSpan - MinEstimatedDimensionModules) * versionQuarterStep),
            QrCodeTables.MinSupportedVersion,
            QrCodeTables.MaxSupportedVersion);

        ReadOnlySpan<int> versionOffsets = [0, -1, 1, -2, 2];
        for (int vo = 0; vo < versionOffsets.Length; vo++)
        {
            int version = estVersion + versionOffsets[vo];
            if (version < QrCodeTables.MinSupportedVersion || version > QrCodeTables.MaxSupportedVersion)
            {
                continue;
            }

            if (TryDecodeTriadAtVersion(imageView, ftl, ftr, fbl, version, isDarkOnLight, out result))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryDecodeTriadAtVersion(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 ftl,
        Vector2 ftr,
        Vector2 fbl,
        int version,
        bool isDarkOnLight,
        out QrCodeDecodeResult result)
    {
        result = default;
        int n = QrCodeTables.DimensionForVersion(version);
        float finderSpanModules = n - FinderModuleSpan;
        float modulePitchPx = HalfPixelOffset * (Vector2.Distance(ftl, ftr) + Vector2.Distance(ftl, fbl)) / finderSpanModules;
        if (modulePitchPx < 1.0f)
        {
            return false;
        }

        Vector2 uVec = ftr - ftl;
        Vector2 vVec = fbl - ftl;

        Span<Vector2> srcPts = stackalloc Vector2[4];
        Span<Vector2> dstPts = stackalloc Vector2[4];
        srcPts[0] = new Vector2(FinderCenterModuleCoord, FinderCenterModuleCoord);
        srcPts[1] = new Vector2(n - FinderCenterModuleCoord, FinderCenterModuleCoord);
        srcPts[2] = new Vector2(FinderCenterModuleCoord, n - FinderCenterModuleCoord);
        dstPts[0] = ftl;
        dstPts[1] = ftr;
        dstPts[2] = fbl;

        bool usedAlignment = false;
        if (version >= 2)
        {
            float alignCoord = n - AlignmentInsetFromEdgeModules;
            float scaleRatio = (alignCoord - FinderCenterModuleCoord) / finderSpanModules;
            Vector2 predictedAlign = ftl + (scaleRatio * (uVec + vVec));

            if (TryLocateAlignmentPattern(imageView, predictedAlign, modulePitchPx, isDarkOnLight, out Vector2 refinedAlign))
            {
                srcPts[3] = new Vector2(alignCoord, alignCoord);
                dstPts[3] = refinedAlign;
                usedAlignment = true;
            }
        }

        if (!usedAlignment)
        {
            srcPts[3] = new Vector2(n - FinderCenterModuleCoord, n - FinderCenterModuleCoord);
            dstPts[3] = ftr + fbl - ftl;
        }

        if (TrySampleAndDecodeGrid(imageView, srcPts, dstPts, version, n, ftl, ftr, fbl, isDarkOnLight, out result))
        {
            return true;
        }

        if (usedAlignment)
        {
            srcPts[3] = new Vector2(n - FinderCenterModuleCoord, n - FinderCenterModuleCoord);
            dstPts[3] = ftr + fbl - ftl;
            return TrySampleAndDecodeGrid(imageView, srcPts, dstPts, version, n, ftl, ftr, fbl, isDarkOnLight, out result);
        }

        return false;
    }

    private static bool TryLocateAlignmentPattern(
        ReadOnlyImageView2D<byte> imageView,
        Vector2 predicted,
        float modulePitchPx,
        bool isDarkOnLight,
        out Vector2 refinedCenter)
    {
        refinedCenter = predicted;
        int width = imageView.Width;
        int height = imageView.Height;
        int stride = imageView.Stride;
        ReadOnlySpan<byte> buf = imageView.Buffer;

        const int borderGuardPx = 3;
        int searchRadius = Math.Max(2, (int)MathF.Ceiling(modulePitchPx * AlignmentSearchRadiusModules));
        int cx0 = Math.Max(borderGuardPx, (int)MathF.Floor(predicted.X) - searchRadius);
        int cx1 = Math.Min(width - borderGuardPx - 1, (int)MathF.Ceiling(predicted.X) + searchRadius);
        int cy0 = Math.Max(borderGuardPx, (int)MathF.Floor(predicted.Y) - searchRadius);
        int cy1 = Math.Min(height - borderGuardPx - 1, (int)MathF.Ceiling(predicted.Y) + searchRadius);
        if (cx0 > cx1 || cy0 > cy1)
        {
            return false;
        }

        const float quarterAverage = 0.25f;
        float ring1R = MathF.Max(1.0f, modulePitchPx);
        float ring2R = MathF.Max(2.0f, 2.0f * modulePitchPx);
        float bestScore = MinAlignmentScore;
        Vector2 bestPt = predicted;
        bool found = false;

        for (int y = cy0; y <= cy1; y++)
        {
            for (int x = cx0; x <= cx1; x++)
            {
                float wx = x + HalfPixelOffset;
                float wy = y + HalfPixelOffset;
                float cVal = buf[(y * stride) + x];

                float r1Avg = quarterAverage * (
                    SampleBilinearWorld(imageView, wx + ring1R, wy) +
                    SampleBilinearWorld(imageView, wx - ring1R, wy) +
                    SampleBilinearWorld(imageView, wx, wy + ring1R) +
                    SampleBilinearWorld(imageView, wx, wy - ring1R));
                float r2Avg = quarterAverage * (
                    SampleBilinearWorld(imageView, wx + ring2R, wy) +
                    SampleBilinearWorld(imageView, wx - ring2R, wy) +
                    SampleBilinearWorld(imageView, wx, wy + ring2R) +
                    SampleBilinearWorld(imageView, wx, wy - ring2R));

                float contrast1 = isDarkOnLight ? (r1Avg - cVal) : (cVal - r1Avg);
                float contrast2 = isDarkOnLight ? (r1Avg - r2Avg) : (r2Avg - r1Avg);
                if (contrast1 <= MinAlignmentRingContrast || contrast2 <= MinAlignmentRingContrast)
                {
                    continue;
                }

                float distPenalty = Vector2.Distance(new Vector2(wx, wy), predicted) * AlignmentDistancePenaltyWeight;
                float score = contrast1 + contrast2 - distPenalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPt = new Vector2(wx, wy);
                    found = true;
                }
            }
        }

        if (!found)
        {
            return false;
        }

        float coreR = MathF.Max(1.2f, modulePitchPx * AlignmentCoreRadiusModules);
        float coreRSq = coreR * coreR;
        int rx0 = Math.Max(0, (int)MathF.Floor(bestPt.X - coreR));
        int rx1 = Math.Min(width - 1, (int)MathF.Ceiling(bestPt.X + coreR));
        int ry0 = Math.Max(0, (int)MathF.Floor(bestPt.Y - coreR));
        int ry1 = Math.Min(height - 1, (int)MathF.Ceiling(bestPt.Y + coreR));

        float ringSample = quarterAverage * (
            SampleBilinearWorld(imageView, bestPt.X + ring1R, bestPt.Y) +
            SampleBilinearWorld(imageView, bestPt.X - ring1R, bestPt.Y) +
            SampleBilinearWorld(imageView, bestPt.X, bestPt.Y + ring1R) +
            SampleBilinearWorld(imageView, bestPt.X, bestPt.Y - ring1R));
        float centerSample = SampleBilinearWorld(imageView, bestPt.X, bestPt.Y);
        float thresh = HalfPixelOffset * (ringSample + centerSample);

        double sumW = 0.0;
        double sumX = 0.0;
        double sumY = 0.0;
        for (int y = ry0; y <= ry1; y++)
        {
            float py = y + HalfPixelOffset;
            float dy = py - bestPt.Y;
            ReadOnlySpan<byte> row = buf.Slice(y * stride, width);
            for (int x = rx0; x <= rx1; x++)
            {
                float px = x + HalfPixelOffset;
                float dx = px - bestPt.X;
                if (((dx * dx) + (dy * dy)) > coreRSq)
                {
                    continue;
                }

                float w = isDarkOnLight ? (thresh - row[x]) : (row[x] - thresh);
                if (w > 0.0f)
                {
                    sumW += w;
                    sumX += w * px;
                    sumY += w * py;
                }
            }
        }

        refinedCenter = sumW > ZeroDenominatorEpsilon
            ? new Vector2((float)(sumX / sumW), (float)(sumY / sumW))
            : bestPt;
        return true;
    }

    private static bool TrySampleAndDecodeGrid(
        ReadOnlyImageView2D<byte> imageView,
        ReadOnlySpan<Vector2> srcPts,
        ReadOnlySpan<Vector2> dstPts,
        int version,
        int n,
        Vector2 ftl,
        Vector2 ftr,
        Vector2 fbl,
        bool isDarkOnLight,
        out QrCodeDecodeResult result)
    {
        result = default;

        if (!QrCodeTables.TrySolveHomography(srcPts, dstPts, out QrCodeTables.Homography3x3 homography))
        {
            return false;
        }

        int totalModules = n * n;
        Span<float> grayGrid = stackalloc float[MaxDimensionModules * MaxDimensionModules].Slice(0, totalModules);
        Span<bool> bitGrid = stackalloc bool[MaxDimensionModules * MaxDimensionModules].Slice(0, totalModules);
        Span<bool> isFunction = stackalloc bool[MaxDimensionModules * MaxDimensionModules].Slice(0, totalModules);
        Span<bool> expectedDark = stackalloc bool[MaxDimensionModules * MaxDimensionModules].Slice(0, totalModules);

        QrCodeTables.BuildFunctionMask(version, isFunction, expectedDark);

        double sumFixedDark = 0.0;
        int countFixedDark = 0;
        double sumFixedLight = 0.0;
        int countFixedLight = 0;

        for (int r = 0; r < n; r++)
        {
            double v = r + HalfPixelOffset;
            for (int c = 0; c < n; c++)
            {
                Vector2 worldPt = homography.Project(c + HalfPixelOffset, v);
                if (worldPt.X < -1.0f || worldPt.X > imageView.Width + 1.0f ||
                    worldPt.Y < -1.0f || worldPt.Y > imageView.Height + 1.0f)
                {
                    return false;
                }

                float sample = SampleBilinearWorld(imageView, worldPt.X, worldPt.Y);
                int idx = (r * n) + c;
                grayGrid[idx] = sample;

                bool inFinderZone = (r < BitsPerByte && c < BitsPerByte)
                    || (r < BitsPerByte && c >= n - BitsPerByte)
                    || (r >= n - BitsPerByte && c < BitsPerByte);
                if (inFinderZone)
                {
                    if (expectedDark[idx])
                    {
                        sumFixedDark += sample;
                        countFixedDark++;
                    }
                    else
                    {
                        sumFixedLight += sample;
                        countFixedLight++;
                    }
                }
            }
        }

        if (countFixedDark == 0 || countFixedLight == 0)
        {
            return false;
        }

        float darkMean = (float)(sumFixedDark / countFixedDark);
        float lightMean = (float)(sumFixedLight / countFixedLight);
        float rawContrast = isDarkOnLight ? (lightMean - darkMean) : (darkMean - lightMean);
        if (rawContrast < MinForegroundContrastGray)
        {
            return false;
        }

        float moduleThreshold = HalfPixelOffset * (darkMean + lightMean);
        for (int i = 0; i < totalModules; i++)
        {
            bitGrid[i] = isDarkOnLight ? (grayGrid[i] <= moduleThreshold) : (grayGrid[i] >= moduleThreshold);
        }

        if (version >= QrCodeTables.MinVersionWithVersionInfo)
        {
            ReadVersionBits(bitGrid, n, out int verBits1, out int verBits2);
            if (!QrCodeTables.TryDecodeVersionBits(verBits1, verBits2, out int decodedVer) || decodedVer != version)
            {
                return false;
            }
        }

        ReadFormatBits(bitGrid, n, out ushort formatCopy1, out ushort formatCopy2);
        if (!QrCodeTables.TryDecodeFormatBits(formatCopy1, formatCopy2, out QrErrorCorrectionLevel ecLevel, out int maskPattern, out int formatBitErrors))
        {
            return false;
        }

        QrCodeTables.QrBlockLayout layout = QrCodeTables.GetBlockLayout(version, ecLevel);
        Span<byte> rawCodewords = stackalloc byte[MaxCodewordCount].Slice(0, layout.TotalCodewords);
        ExtractZigZagCodewords(bitGrid, isFunction, n, maskPattern, rawCodewords);

        Span<byte> dataStream = stackalloc byte[MaxCodewordCount].Slice(0, layout.TotalDataCodewords);
        if (!TryDeinterleaveAndCorrectBlocks(rawCodewords, in layout, dataStream, out int totalCorrectedByteErrors))
        {
            return false;
        }

        if (!TryDecodeDataBitstream(dataStream, version, out string decodedText))
        {
            return false;
        }

        Vector2 tl = homography.Project(0.0, 0.0);
        Vector2 tr = homography.Project(n, 0.0);
        Vector2 br = homography.Project(n, n);
        Vector2 bl = homography.Project(0.0, n);
        Vector2 centerWorld = homography.Project(n * HalfPixelOffset, n * HalfPixelOffset);

        Vector2 horizVec = HalfPixelOffset * ((tr - tl) + (br - bl));
        Vector2 vertVec = HalfPixelOffset * ((bl - tl) + (br - tr));
        float widthPx = horizVec.Length();
        float heightPx = vertVec.Length();
        float angleRad = MathF.Atan2(horizVec.Y - vertVec.X, horizVec.X + vertVec.Y);

        ComputeIso15415Metrics(
            grayGrid,
            bitGrid,
            isFunction,
            expectedDark,
            n,
            darkMean,
            lightMean,
            moduleThreshold,
            widthPx,
            heightPx,
            formatBitErrors,
            totalCorrectedByteErrors,
            layout.TotalBlocks * layout.EccCodewordsPerBlock,
            out float symbolContrast,
            out float modulationScore,
            out float axialNonUniformity,
            out float fixedPatternDamageScore,
            out char overallIsoGrade);

        result = new QrCodeDecodeResult(
            IsDecoded: true,
            DecodedText: decodedText,
            Version: version,
            Dimension: n,
            ErrorCorrectionLevel: ecLevel,
            MaskPattern: maskPattern,
            CenterWorld: centerWorld,
            AngleRad: angleRad,
            WidthPixels: widthPx,
            HeightPixels: heightPx,
            TopLeftCorner: tl,
            TopRightCorner: tr,
            BottomRightCorner: br,
            BottomLeftCorner: bl,
            TopLeftFinderCenter: ftl,
            TopRightFinderCenter: ftr,
            BottomLeftFinderCenter: fbl,
            SymbolContrast: symbolContrast,
            ModulationScore: modulationScore,
            AxialNonUniformity: axialNonUniformity,
            FixedPatternDamageScore: fixedPatternDamageScore,
            OverallIsoGrade: overallIsoGrade,
            CorrectedByteErrors: totalCorrectedByteErrors);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SampleBilinearWorld(ReadOnlyImageView2D<byte> imageView, float worldX, float worldY)
    {
        // [Math Why]: Pixel (ix, iy) covers [ix, ix+1) x [iy, iy+1) with sample center at (ix + 0.5, iy + 0.5).
        float gx = Math.Clamp(worldX - HalfPixelOffset, 0.0f, imageView.Width - 1.0f);
        float gy = Math.Clamp(worldY - HalfPixelOffset, 0.0f, imageView.Height - 1.0f);

        int x0 = (int)gx;
        int y0 = (int)gy;
        int x1 = Math.Min(x0 + 1, imageView.Width - 1);
        int y1 = Math.Min(y0 + 1, imageView.Height - 1);
        float fx = gx - x0;
        float fy = gy - y0;

        int stride = imageView.Stride;
        ReadOnlySpan<byte> buf = imageView.Buffer;
        float i00 = buf[(y0 * stride) + x0];
        float i10 = buf[(y0 * stride) + x1];
        float i01 = buf[(y1 * stride) + x0];
        float i11 = buf[(y1 * stride) + x1];

        float top = i00 + ((i10 - i00) * fx);
        float bot = i01 + ((i11 - i01) * fx);
        return top + ((bot - top) * fy);
    }

    private static void ReadFormatBits(ReadOnlySpan<bool> bitGrid, int n, out ushort copy1, out ushort copy2)
    {
        int c1 = 0;
        ReadOnlySpan<int> copy1Cols = [0, 1, 2, 3, 4, 5, 7, 8, 8, 8, 8, 8, 8, 8, 8];
        ReadOnlySpan<int> copy1Rows = [8, 8, 8, 8, 8, 8, 8, 8, 7, 5, 4, 3, 2, 1, 0];
        for (int i = 0; i < FormatBitCount; i++)
        {
            c1 = (c1 << 1) | (bitGrid[(copy1Rows[i] * n) + copy1Cols[i]] ? 1 : 0);
        }

        int c2 = 0;
        const int formatRowColIndex = 8;
        const int secondCopyLeftSpan = 7;
        for (int r = n - 1; r >= n - secondCopyLeftSpan; r--)
        {
            c2 = (c2 << 1) | (bitGrid[(r * n) + formatRowColIndex] ? 1 : 0);
        }

        for (int c = n - BitsPerByte; c < n; c++)
        {
            c2 = (c2 << 1) | (bitGrid[(formatRowColIndex * n) + c] ? 1 : 0);
        }

        copy1 = (ushort)c1;
        copy2 = (ushort)c2;
    }

    private static void ReadVersionBits(ReadOnlySpan<bool> bitGrid, int n, out int bits1, out int bits2)
    {
        bits1 = 0;
        bits2 = 0;
        const int versionInset = 11;
        const int versionStripThickness = 3;

        for (int i = 0; i < VersionBitCount; i++)
        {
            int r1 = (n - versionInset) + (i % versionStripThickness);
            int c1 = i / versionStripThickness;
            if (bitGrid[(r1 * n) + c1])
            {
                bits1 |= 1 << i;
            }

            int r2 = i / versionStripThickness;
            int c2 = (n - versionInset) + (i % versionStripThickness);
            if (bitGrid[(r2 * n) + c2])
            {
                bits2 |= 1 << i;
            }
        }
    }

    private static void ExtractZigZagCodewords(
        ReadOnlySpan<bool> bitGrid,
        ReadOnlySpan<bool> isFunction,
        int n,
        int maskPattern,
        Span<byte> codewordsOut)
    {
        codewordsOut.Clear();
        int byteIdx = 0;
        int bitCountInByte = 0;
        int currentByte = 0;
        bool upward = true;
        const int timingColumnIndex = 6;

        for (int rightCol = n - 1; rightCol >= 1; rightCol -= 2)
        {
            if (rightCol == timingColumnIndex)
            {
                rightCol = timingColumnIndex - 1;
            }

            for (int step = 0; step < n; step++)
            {
                int row = upward ? (n - 1 - step) : step;
                for (int dx = 0; dx < 2; dx++)
                {
                    int col = rightCol - dx;
                    int idx = (row * n) + col;
                    if (isFunction[idx])
                    {
                        continue;
                    }

                    bool bit = bitGrid[idx] ^ QrCodeTables.EvaluateMaskBit(maskPattern, row, col);
                    currentByte = (currentByte << 1) | (bit ? 1 : 0);
                    bitCountInByte++;

                    if (bitCountInByte == BitsPerByte)
                    {
                        if (byteIdx < codewordsOut.Length)
                        {
                            codewordsOut[byteIdx++] = (byte)currentByte;
                        }

                        currentByte = 0;
                        bitCountInByte = 0;
                        if (byteIdx >= codewordsOut.Length)
                        {
                            return;
                        }
                    }
                }
            }

            upward = !upward;
        }
    }

    private static bool TryDeinterleaveAndCorrectBlocks(
        ReadOnlySpan<byte> rawCodewords,
        in QrCodeTables.QrBlockLayout layout,
        Span<byte> dataOut,
        out int totalCorrectedErrors)
    {
        totalCorrectedErrors = 0;
        int totalBlocks = layout.TotalBlocks;
        int maxDataPerBlock = Math.Max(layout.Group1DataCodewords, layout.Group2DataCodewords);
        int eccLen = layout.EccCodewordsPerBlock;
        int maxBlockLen = maxDataPerBlock + eccLen;

        Span<byte> allBlocks = stackalloc byte[totalBlocks * maxBlockLen];
        Span<int> blockDataLens = stackalloc int[totalBlocks];

        for (int b = 0; b < totalBlocks; b++)
        {
            blockDataLens[b] = b < layout.Group1Blocks ? layout.Group1DataCodewords : layout.Group2DataCodewords;
        }

        int srcIdx = 0;
        for (int col = 0; col < maxDataPerBlock; col++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                if (col < blockDataLens[b] && srcIdx < rawCodewords.Length)
                {
                    allBlocks[(b * maxBlockLen) + col] = rawCodewords[srcIdx++];
                }
            }
        }

        for (int col = 0; col < eccLen; col++)
        {
            for (int b = 0; b < totalBlocks; b++)
            {
                int dataLen = blockDataLens[b];
                if (srcIdx < rawCodewords.Length)
                {
                    allBlocks[(b * maxBlockLen) + dataLen + col] = rawCodewords[srcIdx++];
                }
            }
        }

        int dstOffset = 0;
        for (int b = 0; b < totalBlocks; b++)
        {
            int dataLen = blockDataLens[b];
            Span<byte> blockSlice = allBlocks.Slice(b * maxBlockLen, dataLen + eccLen);
            if (!QrCodeTables.TryCorrectReedSolomonBlock(blockSlice, eccLen, out int blockErrors))
            {
                return false;
            }

            totalCorrectedErrors += blockErrors;
            blockSlice.Slice(0, dataLen).CopyTo(dataOut.Slice(dstOffset, dataLen));
            dstOffset += dataLen;
        }

        return true;
    }

    private static bool TryDecodeDataBitstream(ReadOnlySpan<byte> dataCodewords, int version, out string decodedText)
    {
        decodedText = string.Empty;
        int totalBits = dataCodewords.Length * BitsPerByte;
        int bitPos = 0;
        var sb = new StringBuilder(dataCodewords.Length);
        Encoding activeEncoding = Encoding.UTF8;

        while (bitPos + ModeIndicatorBitCount <= totalBits)
        {
            int mode = ReadBits(dataCodewords, ref bitPos, ModeIndicatorBitCount);
            if (mode == ModeTerminator)
            {
                break;
            }

            if (mode == ModeEci)
            {
                if (!TryReadEciAssignment(dataCodewords, totalBits, ref bitPos, ref activeEncoding))
                {
                    return false;
                }

                continue;
            }

            int countBits = QrCodeTables.GetCharacterCountIndicatorBits(mode, version);
            if (countBits == 0 || bitPos + countBits > totalBits)
            {
                return false;
            }

            int charCount = ReadBits(dataCodewords, ref bitPos, countBits);
            bool segmentOk = mode switch
            {
                ModeNumeric => TryAppendNumericSegment(dataCodewords, totalBits, ref bitPos, charCount, sb),
                ModeAlphanumeric => TryAppendAlphanumericSegment(dataCodewords, totalBits, ref bitPos, charCount, sb),
                ModeByte => TryAppendByteSegment(dataCodewords, totalBits, ref bitPos, charCount, activeEncoding, sb),
                _ => false
            };

            if (!segmentOk)
            {
                return false;
            }
        }

        if (sb.Length == 0)
        {
            return false;
        }

        decodedText = sb.ToString();
        return true;
    }

    private static bool TryReadEciAssignment(
        ReadOnlySpan<byte> dataCodewords,
        int totalBits,
        ref int bitPos,
        ref Encoding activeEncoding)
    {
        const int eciUtf8 = 26;
        const int eciLatin1 = 3;

        if (bitPos + BitsPerByte > totalBits)
        {
            return false;
        }

        int firstByte = ReadBits(dataCodewords, ref bitPos, BitsPerByte);
        int eciValue;
        if ((firstByte & 0x80) == 0)
        {
            eciValue = firstByte & 0x7F;
        }
        else if ((firstByte & 0xC0) == 0x80)
        {
            if (bitPos + BitsPerByte > totalBits)
            {
                return false;
            }

            eciValue = ((firstByte & 0x3F) << BitsPerByte) | ReadBits(dataCodewords, ref bitPos, BitsPerByte);
        }
        else if ((firstByte & 0xE0) == 0xC0)
        {
            const int twoByteBits = 16;
            if (bitPos + twoByteBits > totalBits)
            {
                return false;
            }

            eciValue = ((firstByte & 0x1F) << twoByteBits) | ReadBits(dataCodewords, ref bitPos, twoByteBits);
        }
        else
        {
            return false;
        }

        if (eciValue == eciUtf8)
        {
            activeEncoding = Encoding.UTF8;
        }
        else if (eciValue == eciLatin1)
        {
            activeEncoding = Encoding.Latin1;
        }

        return true;
    }

    private static bool TryAppendNumericSegment(
        ReadOnlySpan<byte> dataCodewords,
        int totalBits,
        ref int bitPos,
        int charCount,
        StringBuilder sb)
    {
        const int tripleBits = 10;
        const int pairBits = 7;
        const int singleBits = 4;
        const int maxTriple = 1000;
        const int maxPair = 100;
        const int decimalRadix = 10;

        int rem = charCount;
        while (rem >= 3)
        {
            if (bitPos + tripleBits > totalBits)
            {
                return false;
            }

            int triple = ReadBits(dataCodewords, ref bitPos, tripleBits);
            if (triple >= maxTriple)
            {
                return false;
            }

            sb.Append((char)('0' + (triple / maxPair)));
            sb.Append((char)('0' + ((triple / decimalRadix) % decimalRadix)));
            sb.Append((char)('0' + (triple % decimalRadix)));
            rem -= 3;
        }

        if (rem == 2)
        {
            if (bitPos + pairBits > totalBits)
            {
                return false;
            }

            int pair = ReadBits(dataCodewords, ref bitPos, pairBits);
            if (pair >= maxPair)
            {
                return false;
            }

            sb.Append((char)('0' + (pair / decimalRadix)));
            sb.Append((char)('0' + (pair % decimalRadix)));
        }
        else if (rem == 1)
        {
            if (bitPos + singleBits > totalBits)
            {
                return false;
            }

            int single = ReadBits(dataCodewords, ref bitPos, singleBits);
            if (single >= decimalRadix)
            {
                return false;
            }

            sb.Append((char)('0' + single));
        }

        return true;
    }

    private static bool TryAppendAlphanumericSegment(
        ReadOnlySpan<byte> dataCodewords,
        int totalBits,
        ref int bitPos,
        int charCount,
        StringBuilder sb)
    {
        const int pairBits = 11;
        const int singleBits = 6;

        int rem = charCount;
        while (rem >= 2)
        {
            if (bitPos + pairBits > totalBits)
            {
                return false;
            }

            int pair = ReadBits(dataCodewords, ref bitPos, pairBits);
            int c0 = pair / AlphanumericRadix;
            int c1 = pair % AlphanumericRadix;
            if (!QrCodeTables.TryMapAlphanumericChar(c0, out char ch0) ||
                !QrCodeTables.TryMapAlphanumericChar(c1, out char ch1))
            {
                return false;
            }

            sb.Append(ch0);
            sb.Append(ch1);
            rem -= 2;
        }

        if (rem == 1)
        {
            if (bitPos + singleBits > totalBits)
            {
                return false;
            }

            int single = ReadBits(dataCodewords, ref bitPos, singleBits);
            if (!QrCodeTables.TryMapAlphanumericChar(single, out char chSingle))
            {
                return false;
            }

            sb.Append(chSingle);
        }

        return true;
    }

    private static bool TryAppendByteSegment(
        ReadOnlySpan<byte> dataCodewords,
        int totalBits,
        ref int bitPos,
        int charCount,
        Encoding activeEncoding,
        StringBuilder sb)
    {
        if (charCount < 0 || bitPos + (charCount * BitsPerByte) > totalBits)
        {
            return false;
        }

        byte[]? rented = null;
        Span<byte> segment = charCount <= StackAllocByteLimit
            ? stackalloc byte[StackAllocByteLimit].Slice(0, charCount)
            : (rented = ArrayPool<byte>.Shared.Rent(charCount)).AsSpan(0, charCount);

        try
        {
            for (int i = 0; i < charCount; i++)
            {
                segment[i] = (byte)ReadBits(dataCodewords, ref bitPos, BitsPerByte);
            }

            sb.Append(activeEncoding.GetString(segment));
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadBits(ReadOnlySpan<byte> bytes, ref int bitPos, int count)
    {
        const int byteBitMask = 7;
        const int byteShift = 3;
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIndex = bitPos >> byteShift;
            int shift = byteBitMask - (bitPos & byteBitMask);
            value = (value << 1) | ((bytes[byteIndex] >> shift) & 1);
            bitPos++;
        }

        return value;
    }

    private static void ComputeIso15415Metrics(
        ReadOnlySpan<float> grayGrid,
        ReadOnlySpan<bool> bitGrid,
        ReadOnlySpan<bool> isFunction,
        ReadOnlySpan<bool> expectedDark,
        int n,
        float darkMean,
        float lightMean,
        float threshold,
        float widthPx,
        float heightPx,
        int formatBitErrors,
        int correctedByteErrors,
        int totalEccCodewords,
        out float symbolContrast,
        out float modulationScore,
        out float axialNonUniformity,
        out float fixedPatternDamageScore,
        out char overallIsoGrade)
    {
        float halfSpan = MathF.Max(1.0f, MathF.Abs(lightMean - darkMean) * HalfPixelOffset);
        symbolContrast = Math.Clamp(MathF.Abs(lightMean - darkMean) * MaxByteInverse, 0.0f, 1.0f);

        int fixedMatches = 0;
        int fixedTotal = 0;
        double modSum = 0.0;
        float minMod = 1.0f;
        int total = n * n;
        const int timingLineIndex = 6;
        const int formatLineIndex = 8;
        const int bottomFormatSpan = 7;

        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                int idx = (r * n) + c;
                float margin = Math.Clamp(MathF.Abs(grayGrid[idx] - threshold) / halfSpan, 0.0f, 1.0f);
                modSum += margin;
                if (margin < minMod)
                {
                    minMod = margin;
                }

                bool isFormatSlot = (r == formatLineIndex && (c <= formatLineIndex || c >= n - BitsPerByte) && c != timingLineIndex)
                    || (c == formatLineIndex && (r <= formatLineIndex || r >= n - bottomFormatSpan) && r != timingLineIndex);
                if (isFunction[idx] && !isFormatSlot)
                {
                    fixedTotal++;
                    if (bitGrid[idx] == expectedDark[idx])
                    {
                        fixedMatches++;
                    }
                }
            }
        }

        float meanMod = (float)(modSum / Math.Max(1, total));
        modulationScore = Math.Clamp((MeanModulationBlendWeight * meanMod) + (MinModulationBlendWeight * minMod), 0.0f, 1.0f);

        float avgDim = MathF.Max(1.0f, HalfPixelOffset * (widthPx + heightPx));
        axialNonUniformity = Math.Clamp(MathF.Abs(widthPx - heightPx) / avgDim, 0.0f, 1.0f);

        float formatHealth = Math.Clamp(1.0f - (formatBitErrors / (float)(QrCodeTables.MaxFormatBitErrors + 1)), 0.0f, 1.0f);
        float fixedRatio = fixedTotal > 0 ? (float)fixedMatches / fixedTotal : 1.0f;
        fixedPatternDamageScore = Math.Clamp(MathF.Min(fixedRatio, formatHealth), 0.0f, 1.0f);

        float unusedEcc = totalEccCodewords > 0
            ? Math.Clamp(1.0f - ((2.0f * correctedByteErrors) / totalEccCodewords), 0.0f, 1.0f)
            : 1.0f;
        float anuQuality = Math.Clamp(1.0f - (axialNonUniformity * AxialNonUniformityPenaltyScale), 0.0f, 1.0f);

        float composite = MathF.Min(
            MathF.Min(symbolContrast, modulationScore),
            MathF.Min(fixedPatternDamageScore, MathF.Min(unusedEcc, anuQuality)));

        overallIsoGrade = composite >= IsoGradeAThreshold ? 'A'
            : composite >= IsoGradeBThreshold ? 'B'
            : composite >= IsoGradeCThreshold ? 'C'
            : composite >= IsoGradeDThreshold ? 'D'
            : 'F';
    }
}
