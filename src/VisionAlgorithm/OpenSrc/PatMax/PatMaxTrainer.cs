// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxTrainer.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Multi-scale PatMax pattern trainer extracting Coarse-to-Fine boundary dipoles, centering coordinates on the pattern reference origin,
/// evaluating orientation stability (US 6,993,177), and pre-generating the 2D Vector Attraction Field (US 7,065,262).
/// </summary>
public static class PatMaxTrainer
{
    private const byte ExcludedMaskSentinel = byte.MaxValue;
    private const int MinInitialDipoleCapacity = 256;
    private const int MaxInitialDipoleCapacity = 65536;
    private const int MaxFineModelDipoles = 768;
    // [Perf Why]: Coarse PatQuick search uses at most 64 spatially-binned salient boundary probes for cache-resident SIMD probing.
    private const int MaxCoarseModelDipoles = 64;
    private const int CoarseSalientGridDimension = 8;
    private const int SpatialCellGridDimension = 4;
    private const int SpatialCellGridCount = SpatialCellGridDimension * SpatialCellGridDimension;
    private const float ContrastClampRatioToMin = 2.0f;
    private const float MinPeripheralContextWeightShare = 0.28f;
    private const int OrthogonalAnchorCount = 4;
    private const float OrthogonalityBaseBias = 0.25f;
    private const int AreaToDipoleRatioShift = 3; // (width * height) >> 3 == (width * height) / 8
    // [Math Why]: Inverse golden ratio conjugate (~0.61803398875) yields a low-discrepancy quasi-random stride across 1D raster dipole sequences.
    private const float GoldenRatioConjugate = 0.61803398875f;

    public static PatMaxModel Train(GrayImage image, in PatMaxTrainParams trainParams, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Train(
            image.Pixels.Span,
            image.Width,
            image.Height,
            image.Stride,
            in trainParams,
            image.Width * 0.5f,
            image.Height * 0.5f,
            image.ExcludedPixels.Span,
            cancellationToken);
    }

    public static PatMaxModel Train(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        in PatMaxTrainParams trainParams,
        float? customOriginX = null,
        float? customOriginY = null,
        ReadOnlySpan<byte> excludedMask = default,
        CancellationToken cancellationToken = default)
        => TrainCore(pixels, width, height, stride, in trainParams, customOriginX, customOriginY, excludedMask, buildAttractionField: true, cancellationToken);

    /// <summary>
    /// Fast non-blocking preview trainer that extracts multi-scale Coarse/Fine dipoles and evaluates stability (US 6,993,177)
    /// without pre-generating the 2D Attraction Field, achieving sub-millisecond interactive UI preview latency.
    /// </summary>
    public static PatMaxModel TrainPreview(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        in PatMaxTrainParams trainParams,
        float? customOriginX = null,
        float? customOriginY = null,
        ReadOnlySpan<byte> excludedMask = default,
        CancellationToken cancellationToken = default)
        => TrainCore(pixels, width, height, stride, in trainParams, customOriginX, customOriginY, excludedMask, buildAttractionField: false, cancellationToken);

    private static PatMaxModel TrainCore(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        in PatMaxTrainParams trainParams,
        float? customOriginX,
        float? customOriginY,
        ReadOnlySpan<byte> excludedMask,
        bool buildAttractionField,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (trainParams.IsEmpty)
        {
            throw new ArgumentException("PatMaxTrainParams must not be empty.", nameof(trainParams));
        }

        if (pixels.Length < ((height - 1) * stride) + width)
        {
            throw new ArgumentException("Pixel buffer is smaller than required by height and stride.", nameof(pixels));
        }

        if (!excludedMask.IsEmpty && excludedMask.Length < width * height)
        {
            throw new ArgumentException("Excluded mask length must match width * height.", nameof(excludedMask));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var originX = customOriginX ?? (width * 0.5f);
        var originY = customOriginY ?? (height * 0.5f);

        var pixelCount = checked(width * height);
        byte[]? filteredPool = PatMaxInspectionFilter.RequiresRuntimePreprocessingPass(in trainParams)
            ? System.Buffers.ArrayPool<byte>.Shared.Rent(pixelCount)
            : null;

        using var pyramid = new GranularityPyramid();
        try
        {
            if (filteredPool is not null)
            {
                var filteredSpan = filteredPool.AsSpan(0, pixelCount);
                PatMaxInspectionFilter.Apply(
                    pixels,
                    width,
                    height,
                    stride,
                    filteredSpan,
                    trainParams.PreprocessingFilter,
                    trainParams.FineGranularity,
                    trainParams.FilterSigma,
                    trainParams.EdgeThreshold,
                    forDisplayPreview: false);
                pyramid.Build(filteredSpan, width, height, width, trainParams.FineGranularity, trainParams.CoarseGranularity);
            }
            else
            {
                pyramid.Build(pixels, width, height, stride, trainParams.FineGranularity, trainParams.CoarseGranularity);
            }
        }
        finally
        {
            if (filteredPool is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(filteredPool);
            }
        }

        var levelCount = pyramid.LevelCount;
        var modelLevels = new PatMaxModelLevel[levelCount];
        var succeeded = false;

        try
        {
            for (var levelIdx = 0; levelIdx < levelCount; levelIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var view = pyramid.GetLevel(levelIdx);
                var initialCapacity = Math.Clamp((view.Width * view.Height) >> AreaToDipoleRatioShift, MinInitialDipoleCapacity, MaxInitialDipoleCapacity);
                using var rawDipoles = EdgeDipoleSoA.Rent(initialCapacity);

                // [Math Why]: Multi-octave binomial smoothing attenuates high-frequency step-edge gradients by ~1 / sqrt(ScaleFactor) per octave.
                var levelThreshold = levelIdx == 0
                    ? trainParams.EdgeThreshold
                    : MathF.Max(4.0f, trainParams.EdgeThreshold / MathF.Sqrt(view.ScaleFactor));

                SubPixelEdgeExtractor.Extract(
                    view.Pixels,
                    view.Width,
                    view.Height,
                    view.Stride,
                    levelThreshold,
                    rawDipoles,
                    view.ScaleFactor);

                var targetGranularity = levelIdx == 0
                    ? (float)trainParams.FineGranularity
                    : MathF.Max(view.ScaleFactor, trainParams.FineGranularity * view.ScaleFactor);
                var maxLevelDipoles = levelIdx == 0 ? MaxFineModelDipoles : MaxCoarseModelDipoles;

                var compactedDipoles = CompactAndCenterDipoles(
                    rawDipoles,
                    originX,
                    originY,
                    width,
                    height,
                    view.ScaleFactor,
                    targetGranularity,
                    maxLevelDipoles,
                    excludedMask,
                    out var totalWeight);

                modelLevels[levelIdx] = new PatMaxModelLevel(levelIdx, view.ScaleFactor, compactedDipoles, totalWeight);
            }

            var fineDipoles = modelLevels[0].Dipoles;
            var stability = StabilityMatrixAnalyzer.Evaluate(fineDipoles, in trainParams);

            var attractionField = buildAttractionField && !fineDipoles.IsEmpty
                ? AttractionFieldBuilder.Build(
                    fineDipoles,
                    width,
                    height,
                    originX,
                    originY,
                    trainParams.ElasticityPixels,
                    cancellationToken: cancellationToken)
                : AttractionField.Empty;

            succeeded = true;
            return new PatMaxModel(
                width,
                height,
                originX,
                originY,
                in trainParams,
                in stability,
                modelLevels,
                attractionField);
        }
        finally
        {
            if (!succeeded)
            {
                for (var i = 0; i < levelCount; i++)
                {
                    modelLevels[i]?.Dispose();
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static EdgeDipoleSoA CompactAndCenterDipoles(
        EdgeDipoleSoA raw,
        float originX,
        float originY,
        int baseWidth,
        int baseHeight,
        float levelScaleFactor,
        float targetGranularity,
        int maxAllowedDipoles,
        ReadOnlySpan<byte> excludedMask,
        out float totalWeight)
    {
        totalWeight = 0f;
        if (raw.IsEmpty)
        {
            return EdgeDipoleSoA.Empty;
        }

        var rawCount = raw.Count;
        var srcX = raw.X;
        var srcY = raw.Y;
        var srcCos = raw.CosTheta;
        var srcSin = raw.SinTheta;
        var srcMag = raw.Magnitude;
        var needsGranularityThinning = targetGranularity > levelScaleFactor + 0.25f;

        if (excludedMask.IsEmpty && !needsGranularityThinning && rawCount <= maxAllowedDipoles)
        {
            var centered = EdgeDipoleSoA.Rent(rawCount);
            if (rawCount <= 2)
            {
                CopyAndCenterAllSimd(srcX, srcY, srcCos, srcSin, srcMag, originX, originY, centered, out totalWeight);
                return centered;
            }

            using var tempCentered = EdgeDipoleSoA.Rent(rawCount);
            CopyAndCenterAllSimd(srcX, srcY, srcCos, srcSin, srcMag, originX, originY, tempCentered, out _);
            NormalizeContrastAndSpatialCellWeights(tempCentered, out totalWeight);
            PermuteSpatiallyInterleaved(tempCentered, centered);
            return centered;
        }

        using var filtered = EdgeDipoleSoA.Rent(rawCount);
        for (var i = 0; i < rawCount; i++)
        {
            var x = srcX[i];
            var y = srcY[i];
            if (!excludedMask.IsEmpty && IsExcludedAtScale(excludedMask, baseWidth, baseHeight, x, y, levelScaleFactor))
            {
                continue;
            }

            filtered.Add(x - originX, y - originY, srcCos[i], srcSin[i], srcMag[i]);
        }

        if (filtered.IsEmpty)
        {
            return EdgeDipoleSoA.Empty;
        }

        using var thinned = needsGranularityThinning
            ? ThinByGranularityGrid(filtered, baseWidth, baseHeight, originX, originY, targetGranularity)
            : EdgeDipoleSoA.Empty;
        var activeSource = needsGranularityThinning ? thinned : filtered;
        if (activeSource.IsEmpty)
        {
            return EdgeDipoleSoA.Empty;
        }

        using var capped = activeSource.Count > maxAllowedDipoles
            ? CapSalientGridAndStratified(activeSource, baseWidth, baseHeight, originX, originY, maxAllowedDipoles)
            : EdgeDipoleSoA.Empty;
        var finalSource = activeSource.Count > maxAllowedDipoles ? capped : activeSource;

        NormalizeContrastAndSpatialCellWeights(finalSource, out totalWeight);
        var result = EdgeDipoleSoA.Rent(finalSource.Count);
        PermuteSpatiallyInterleaved(finalSource, result);
        return result;
    }

    /// <summary>
    /// Rule 1: Contrast-invariant log/clamped edge weight + 4x4 Spatial Cell density normalization.
    /// Prevents high-contrast dense circular holes from monopolizing model weight while guaranteeing that peripheral
    /// context features (e.g., L-shaped PCB borders) contribute at least 28% of the total model weight when present.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void NormalizeContrastAndSpatialCellWeights(EdgeDipoleSoA dipoles, out float totalWeight)
    {
        var count = dipoles.Count;
        if (count == 0)
        {
            totalWeight = 0f;
            return;
        }

        var xSpan = dipoles.X;
        var ySpan = dipoles.Y;
        var magSpan = dipoles.WritableMagnitude.Slice(0, count);

        var minX = xSpan[0];
        var maxX = xSpan[0];
        var minY = ySpan[0];
        var maxY = ySpan[0];
        var minMag = MathF.Max(1.0f, magSpan[0]);

        for (var i = 1; i < count; i++)
        {
            var x = xSpan[i];
            var y = ySpan[i];
            var m = magSpan[i];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (m > 1.0f && m < minMag) minMag = m;
        }

        var spanX = MathF.Max(1.0f, maxX - minX);
        var spanY = MathF.Max(1.0f, maxY - minY);
        var invSpanX = SpatialCellGridDimension / (spanX + 1e-3f);
        var invSpanY = SpatialCellGridDimension / (spanY + 1e-3f);

        Span<int> cellCounts = stackalloc int[SpatialCellGridCount];
        cellCounts.Clear();

        for (var i = 0; i < count; i++)
        {
            var gx = Math.Clamp((int)((xSpan[i] - minX) * invSpanX), 0, SpatialCellGridDimension - 1);
            var gy = Math.Clamp((int)((ySpan[i] - minY) * invSpanY), 0, SpatialCellGridDimension - 1);
            cellCounts[(gy * SpatialCellGridDimension) + gx]++;
        }

        var populatedCells = 0;
        for (var c = 0; c < SpatialCellGridCount; c++)
        {
            if (cellCounts[c] > 0)
            {
                populatedCells++;
            }
        }

        var avgDensity = MathF.Max(1.0f, (float)count / Math.Max(1, populatedCells));
        var innerSum = 0.0f;
        var outerSum = 0.0f;

        for (var i = 0; i < count; i++)
        {
            var gx = Math.Clamp((int)((xSpan[i] - minX) * invSpanX), 0, SpatialCellGridDimension - 1);
            var gy = Math.Clamp((int)((ySpan[i] - minY) * invSpanY), 0, SpatialCellGridDimension - 1);
            var cellIdx = (gy * SpatialCellGridDimension) + gx;
            var cellDensity = MathF.Max(1.0f, cellCounts[cellIdx]);

            // Soft log/clamped contrast normalization: brightest edge cannot exceed 1.75x the weakest valid edge
            var ratio = Math.Clamp(magSpan[i] / minMag, 1.0f, 8.0f);
            var contrastFactor = Math.Clamp(1.0f + (0.22f * MathF.Log2(ratio)), 1.0f, ContrastClampRatioToMin);

            // Inverse square-root spatial cell density normalization: dampens over-dense circular rings and boosts sparse peripheral borders
            var densityFactor = Math.Clamp(MathF.Sqrt(avgDensity / cellDensity), 0.40f, 2.60f);
            var w = contrastFactor * densityFactor;
            magSpan[i] = w;

            var isOuterBorderCell = gx == 0 || gx == SpatialCellGridDimension - 1 || gy == 0 || gy == SpatialCellGridDimension - 1;
            if (isOuterBorderCell)
            {
                outerSum += w;
            }
            else
            {
                innerSum += w;
            }
        }

        var rawTotal = innerSum + outerSum;
        if (outerSum > 0.0f && innerSum > 0.0f && rawTotal > 1e-5f)
        {
            var outerShare = outerSum / rawTotal;
            if (outerShare < MinPeripheralContextWeightShare)
            {
                // Boost peripheral 4x4 border cells so outer L-shaped context holds at least ~28% of total weight
                var targetBoost = (MinPeripheralContextWeightShare * innerSum) / (outerSum * (1.0f - MinPeripheralContextWeightShare));
                var outerBoost = Math.Clamp(targetBoost, 1.0f, 3.2f);
                rawTotal = 0.0f;
                for (var i = 0; i < count; i++)
                {
                    var gx = Math.Clamp((int)((xSpan[i] - minX) * invSpanX), 0, SpatialCellGridDimension - 1);
                    var gy = Math.Clamp((int)((ySpan[i] - minY) * invSpanY), 0, SpatialCellGridDimension - 1);
                    if (gx == 0 || gx == SpatialCellGridDimension - 1 || gy == 0 || gy == SpatialCellGridDimension - 1)
                    {
                        magSpan[i] *= outerBoost;
                    }

                    rawTotal += magSpan[i];
                }
            }
        }

        totalWeight = rawTotal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsExcludedAtScale(
        ReadOnlySpan<byte> excludedMask,
        int baseWidth,
        int baseHeight,
        float x,
        float y,
        float scaleFactor)
    {
        var px = Math.Clamp((int)x, 0, baseWidth - 1);
        var py = Math.Clamp((int)y, 0, baseHeight - 1);
        if (excludedMask[(py * baseWidth) + px] != ExcludedMaskSentinel)
        {
            return false;
        }

        if (scaleFactor <= 1.0f)
        {
            return true;
        }

        var reach = Math.Max(1, (int)MathF.Ceiling(scaleFactor * 0.5f));
        var x0 = Math.Clamp(px - reach, 0, baseWidth - 1);
        var x1 = Math.Clamp(px + reach, 0, baseWidth - 1);
        var y0 = Math.Clamp(py - reach, 0, baseHeight - 1);
        var y1 = Math.Clamp(py + reach, 0, baseHeight - 1);

        return excludedMask[(py * baseWidth) + x0] == ExcludedMaskSentinel
            && excludedMask[(py * baseWidth) + x1] == ExcludedMaskSentinel
            && excludedMask[(y0 * baseWidth) + px] == ExcludedMaskSentinel
            && excludedMask[(y1 * baseWidth) + px] == ExcludedMaskSentinel;
    }

    private static EdgeDipoleSoA ThinByGranularityGrid(
        EdgeDipoleSoA source,
        int baseWidth,
        int baseHeight,
        float originX,
        float originY,
        float cellStep)
    {
        var invStep = 1.0f / MathF.Max(1.0f, cellStep);
        var gridW = Math.Max(1, (int)MathF.Ceiling(baseWidth * invStep) + 1);
        var gridH = Math.Max(1, (int)MathF.Ceiling(baseHeight * invStep) + 1);
        var cellCount = checked(gridW * gridH);
        var cellWinnerIdx = System.Buffers.ArrayPool<int>.Shared.Rent(cellCount);

        try
        {
            var winners = cellWinnerIdx.AsSpan(0, cellCount);
            winners.Fill(-1);

            var count = source.Count;
            var xSpan = source.X;
            var ySpan = source.Y;
            var magSpan = source.Magnitude;

            for (var i = 0; i < count; i++)
            {
                var gx = Math.Clamp((int)MathF.Floor((xSpan[i] + originX) * invStep), 0, gridW - 1);
                var gy = Math.Clamp((int)MathF.Floor((ySpan[i] + originY) * invStep), 0, gridH - 1);
                var cellIdx = (gy * gridW) + gx;
                var prev = winners[cellIdx];
                if (prev < 0 || magSpan[i] > magSpan[prev])
                {
                    winners[cellIdx] = i;
                }
            }

            var dst = EdgeDipoleSoA.Rent(Math.Min(count, cellCount));
            var cosSpan = source.CosTheta;
            var sinSpan = source.SinTheta;
            for (var c = 0; c < cellCount; c++)
            {
                var winIdx = winners[c];
                if (winIdx >= 0)
                {
                    dst.Add(xSpan[winIdx], ySpan[winIdx], cosSpan[winIdx], sinSpan[winIdx], magSpan[winIdx]);
                }
            }

            return dst;
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(cellWinnerIdx);
        }
    }

    private static EdgeDipoleSoA CapSalientGridAndStratified(
        EdgeDipoleSoA source,
        int baseWidth,
        int baseHeight,
        float originX,
        float originY,
        int maxDipoles)
    {
        var count = source.Count;
        var dst = EdgeDipoleSoA.Rent(maxDipoles);
        var xSpan = source.X;
        var ySpan = source.Y;
        var cosSpan = source.CosTheta;
        var sinSpan = source.SinTheta;
        var magSpan = source.Magnitude;

        if (maxDipoles <= MaxCoarseModelDipoles)
        {
            const int gridCells = CoarseSalientGridDimension * CoarseSalientGridDimension;
            Span<int> cellWinners = stackalloc int[gridCells];
            cellWinners.Fill(-1);
            Span<byte> selectedFlags = count <= 1024 ? stackalloc byte[count] : new byte[count];
            selectedFlags.Clear();

            var invCellW = CoarseSalientGridDimension / MathF.Max(1.0f, baseWidth);
            var invCellH = CoarseSalientGridDimension / MathF.Max(1.0f, baseHeight);

            for (var i = 0; i < count; i++)
            {
                var gx = Math.Clamp((int)MathF.Floor((xSpan[i] + originX) * invCellW), 0, CoarseSalientGridDimension - 1);
                var gy = Math.Clamp((int)MathF.Floor((ySpan[i] + originY) * invCellH), 0, CoarseSalientGridDimension - 1);
                var cIdx = (gy * CoarseSalientGridDimension) + gx;
                var prev = cellWinners[cIdx];
                if (prev < 0 || magSpan[i] > magSpan[prev])
                {
                    cellWinners[cIdx] = i;
                }
            }

            for (var c = 0; c < gridCells && dst.Count < maxDipoles; c++)
            {
                var win = cellWinners[c];
                if (win >= 0)
                {
                    selectedFlags[win] = 1;
                    dst.Add(xSpan[win], ySpan[win], cosSpan[win], sinSpan[win], magSpan[win]);
                }
            }

            // Fill remaining slots up to maxDipoles using stratified boundary segments
            var remaining = maxDipoles - dst.Count;
            for (var b = 0; b < remaining; b++)
            {
                var start = (int)((long)b * count / remaining);
                var end = (int)((long)(b + 1) * count / remaining);
                var bestIdx = -1;
                var bestMag = -1.0f;
                for (var i = start; i < end; i++)
                {
                    if (selectedFlags[i] == 0 && magSpan[i] > bestMag)
                    {
                        bestMag = magSpan[i];
                        bestIdx = i;
                    }
                }

                if (bestIdx >= 0)
                {
                    selectedFlags[bestIdx] = 1;
                    dst.Add(xSpan[bestIdx], ySpan[bestIdx], cosSpan[bestIdx], sinSpan[bestIdx], bestMag);
                }
            }

            return dst;
        }

        // Partition source into maxDipoles spatial segments along the boundary sequence and keep the strongest dipole in each segment
        for (var b = 0; b < maxDipoles; b++)
        {
            var start = (int)((long)b * count / maxDipoles);
            var end = (int)((long)(b + 1) * count / maxDipoles);
            if (end <= start)
            {
                end = Math.Min(count, start + 1);
            }

            var bestIdx = start;
            var bestMag = magSpan[start];
            for (var i = start + 1; i < end; i++)
            {
                if (magSpan[i] > bestMag)
                {
                    bestMag = magSpan[i];
                    bestIdx = i;
                }
            }

            dst.Add(xSpan[bestIdx], ySpan[bestIdx], cosSpan[bestIdx], sinSpan[bestIdx], bestMag);
        }

        return dst;
    }

    private static void PermuteSpatiallyInterleaved(EdgeDipoleSoA source, EdgeDipoleSoA destination)
    {
        var count = source.Count;
        if (count == 0)
        {
            return;
        }

        var step = ChooseCoprimeGoldenStep(count);
        var srcX = source.X;
        var srcY = source.Y;
        var srcCos = source.CosTheta;
        var srcSin = source.SinTheta;
        var srcMag = source.Magnitude;

        var dstX = destination.WritableX;
        var dstY = destination.WritableY;
        var dstCos = destination.WritableCosTheta;
        var dstSin = destination.WritableSinTheta;
        var dstMag = destination.WritableMagnitude;

        var srcIdx = 0;
        for (var i = 0; i < count; i++)
        {
            dstX[i] = srcX[srcIdx];
            dstY[i] = srcY[srcIdx];
            dstCos[i] = srcCos[srcIdx];
            dstSin[i] = srcSin[srcIdx];
            dstMag[i] = srcMag[srcIdx];

            srcIdx += step;
            if (srcIdx >= count)
            {
                srcIdx -= count;
            }
        }

        destination.SetCount(count);
        if (count >= OrthogonalAnchorCount)
        {
            PromoteFourOrthogonalAnchors(dstX.Slice(0, count), dstY.Slice(0, count), dstCos.Slice(0, count), dstSin.Slice(0, count), dstMag.Slice(0, count));
        }
    }

    private static void PromoteFourOrthogonalAnchors(
        Span<float> x,
        Span<float> y,
        Span<float> cos,
        Span<float> sin,
        Span<float> mag)
    {
        var count = x.Length;

        // Anchor 0: strongest gradient magnitude dipole
        var best0 = 0;
        var bestScore0 = mag[0];
        for (var i = 1; i < count; i++)
        {
            if (mag[i] > bestScore0)
            {
                bestScore0 = mag[i];
                best0 = i;
            }
        }
        SwapDipoleAt(x, y, cos, sin, mag, 0, best0);

        // Anchor 1: maximally orthogonal normal to Anchor 0 + spatially separated
        var best1 = 1;
        var bestScore1 = -1.0f;
        for (var i = 1; i < count; i++)
        {
            var cross = MathF.Abs((cos[0] * sin[i]) - (sin[0] * cos[i]));
            var dx = x[i] - x[0];
            var dy = y[i] - y[0];
            var dist = MathF.Sqrt((dx * dx) + (dy * dy)) + 1.0f;
            var score = mag[i] * (OrthogonalityBaseBias + cross) * dist;
            if (score > bestScore1)
            {
                bestScore1 = score;
                best1 = i;
            }
        }
        SwapDipoleAt(x, y, cos, sin, mag, 1, best1);

        // Anchor 2: maximally separated spatially from Anchors 0 & 1 and orthogonal to Anchor 1
        var best2 = 2;
        var bestScore2 = -1.0f;
        for (var i = 2; i < count; i++)
        {
            var cross1 = MathF.Abs((cos[1] * sin[i]) - (sin[1] * cos[i]));
            var d0Sq = ((x[i] - x[0]) * (x[i] - x[0])) + ((y[i] - y[0]) * (y[i] - y[0]));
            var d1Sq = ((x[i] - x[1]) * (x[i] - x[1])) + ((y[i] - y[1]) * (y[i] - y[1]));
            var minDist = MathF.Sqrt(MathF.Min(d0Sq, d1Sq)) + 1.0f;
            var score = mag[i] * (OrthogonalityBaseBias + cross1) * minDist;
            if (score > bestScore2)
            {
                bestScore2 = score;
                best2 = i;
            }
        }
        SwapDipoleAt(x, y, cos, sin, mag, 2, best2);

        // Anchor 3: maximally orthogonal to Anchor 2 and spatially distant from Anchors 0..2
        var best3 = 3;
        var bestScore3 = -1.0f;
        for (var i = 3; i < count; i++)
        {
            var cross2 = MathF.Abs((cos[2] * sin[i]) - (sin[2] * cos[i]));
            var d0Sq = ((x[i] - x[0]) * (x[i] - x[0])) + ((y[i] - y[0]) * (y[i] - y[0]));
            var d1Sq = ((x[i] - x[1]) * (x[i] - x[1])) + ((y[i] - y[1]) * (y[i] - y[1]));
            var d2Sq = ((x[i] - x[2]) * (x[i] - x[2])) + ((y[i] - y[2]) * (y[i] - y[2]));
            var minDist = MathF.Sqrt(MathF.Min(d0Sq, MathF.Min(d1Sq, d2Sq))) + 1.0f;
            var score = mag[i] * (OrthogonalityBaseBias + cross2) * minDist;
            if (score > bestScore3)
            {
                bestScore3 = score;
                best3 = i;
            }
        }
        SwapDipoleAt(x, y, cos, sin, mag, 3, best3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapDipoleAt(Span<float> x, Span<float> y, Span<float> cos, Span<float> sin, Span<float> mag, int a, int b)
    {
        if (a == b)
        {
            return;
        }

        (x[a], x[b]) = (x[b], x[a]);
        (y[a], y[b]) = (y[b], y[a]);
        (cos[a], cos[b]) = (cos[b], cos[a]);
        (sin[a], sin[b]) = (sin[b], sin[a]);
        (mag[a], mag[b]) = (mag[b], mag[a]);
    }

    private static int ChooseCoprimeGoldenStep(int count)
    {
        if (count <= 2)
        {
            return 1;
        }

        var candidate = Math.Clamp((int)MathF.Round(count * GoldenRatioConjugate), 1, count - 1);
        while (GreatestCommonDivisor(candidate, count) != 1)
        {
            candidate++;
            if (candidate >= count)
            {
                candidate = 1;
            }
        }

        return candidate;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var r = a % b;
            a = b;
            b = r;
        }

        return a;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CopyAndCenterAllSimd(
        ReadOnlySpan<float> srcX,
        ReadOnlySpan<float> srcY,
        ReadOnlySpan<float> srcCos,
        ReadOnlySpan<float> srcSin,
        ReadOnlySpan<float> srcMag,
        float originX,
        float originY,
        EdgeDipoleSoA destination,
        out float totalWeight)
    {
        var count = srcX.Length;
        var dstX = destination.WritableX;
        var dstY = destination.WritableY;
        srcCos.CopyTo(destination.WritableCosTheta);
        srcSin.CopyTo(destination.WritableSinTheta);
        srcMag.CopyTo(destination.WritableMagnitude);

        ref var sX = ref MemoryMarshal.GetReference(srcX);
        ref var sY = ref MemoryMarshal.GetReference(srcY);
        ref var sM = ref MemoryMarshal.GetReference(srcMag);
        ref var dX = ref MemoryMarshal.GetReference(dstX);
        ref var dY = ref MemoryMarshal.GetReference(dstY);

        var i = 0;
        var sumWeight = 0f;

        // [Perf Why]: Center 8 dipole coordinates and accumulate total edge magnitude weight per cycle in AVX2.
        if (Avx2.IsSupported)
        {
            var simdLimit = count & ~(Vector256<float>.Count - 1);
            var vOriginX = Vector256.Create(originX);
            var vOriginY = Vector256.Create(originY);
            var vWeightSum = Vector256<float>.Zero;

            for (; i < simdLimit; i += Vector256<float>.Count)
            {
                var vx = Vector256.LoadUnsafe(ref Unsafe.Add(ref sX, i)) - vOriginX;
                var vy = Vector256.LoadUnsafe(ref Unsafe.Add(ref sY, i)) - vOriginY;
                var vm = Vector256.LoadUnsafe(ref Unsafe.Add(ref sM, i));

                vx.StoreUnsafe(ref Unsafe.Add(ref dX, i));
                vy.StoreUnsafe(ref Unsafe.Add(ref dY, i));
                vWeightSum += vm;
            }

            sumWeight = Vector256.Sum(vWeightSum);
        }

        for (; i < count; i++)
        {
            Unsafe.Add(ref dX, i) = Unsafe.Add(ref sX, i) - originX;
            Unsafe.Add(ref dY, i) = Unsafe.Add(ref sY, i) - originY;
            sumWeight += Unsafe.Add(ref sM, i);
        }

        destination.SetCount(count);
        totalWeight = sumWeight;
    }
}
