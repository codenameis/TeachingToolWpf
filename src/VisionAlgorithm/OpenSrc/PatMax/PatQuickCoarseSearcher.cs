// File: src/VisionAlgorithm/OpenSrc/PatMax/PatQuickCoarseSearcher.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// PatQuick Coarse Search engine (US 7,016,539) executing quantized pose-space probing with AVX2 gradient dot-product accumulation,
/// compile-time polarity policy specialization, and Cauchy-Schwarz early termination.
/// </summary>
public static class PatQuickCoarseSearcher
{
    private const int EarlyExitCheckBlockShift = 4; // 1 << 4 == 16 dipoles per early-exit block
    // [Math Why]: Minimum angular step lower bound (approx 1.0 degree) when pattern radius is large.
    private const float MinAngleStepRadians = 0.0174532925f;
    // [Math Why]: Maximum angular step upper bound (approx 15.0 degrees) when pattern radius is tiny.
    private const float MaxAngleStepRadians = 0.2617993878f;
    // [Math Why]: Angular NMS separation (~20 degrees) allowing distinct rotation hypotheses at the same (X, Y) location to survive.
    private const float MinAngleSeparationRadians = 0.35f;
    private const float DefaultScaleStep = 0.05f;
    private const float WeightFloorEpsilon = 1e-6f;
    private const float TwoPi = MathF.PI * 2.0f;
    private const float CoarseNeighborDilationFactor = ZeroNormalDilation.NeighborDilationFactor;
    private const float AnchorDirectionalGateFactor = 0.40f;
    private const int Stride2CoarsePixelThreshold = 131_072; // 362 x 362 coarse pixels
    private const int ParallelWorkUnitsThreshold = 65_536;
    private const int MaxParallelDegree = 16;
    private const int MaxPoseLocalPeaks = 64;
    // [Guard 1]: Dynamic minimum pattern dimension (12 px) and minimum valid coarse probe count (16) for hierarchical octave selection.
    private const float MinCoarsePatternSidePixels = 12.0f;
    private const int MinCoarseSalientProbeCount = 16;
    // [Guard 2]: Minimum 32-bin 1-byte orientation LUT matches required for strict 2-anchor early gate.
    private const int MinAnchorLutMatches = 2;
    // [Guard 3]: Outer probe radius scale displacement threshold (Delta_px = R_coarse * |S - S_rep| <= 0.75 px) for single-scale coarse scan.
    private const float MaxSingleScaleOuterDisplacementPx = 0.75f;

    private readonly record struct CoarseGridPeak(int Cx, int Cy, float WeightedSum);

    private readonly record struct AnchorGateState(
        bool Has4Probes,
        bool Has8Probes,
        bool Has12Probes,
        bool Has16Probes,
        bool UseStrictTwoAnchorGate,
        bool UseAtLeastOneAnchorGate,
        float RemainingAfter4,
        float RemainingAfter8,
        float RemainingAfter12,
        float RemainingAfter16,
        float MaxSingleWeight8,
        float MaxSingleWeight12,
        float MaxSingleWeight16,
        float W0,
        float W1,
        float W2,
        float W3,
        int Off0,
        int Off1,
        int Off2,
        int Off3,
        int Off4,
        int Off5,
        int Off6,
        int Off7,
        int Off8,
        int Off9,
        int Off10,
        int Off11,
        int Off12,
        int Off13,
        int Off14,
        int Off15,
        float C0,
        float S0,
        float C1,
        float S1,
        float C2,
        float S2,
        float C3,
        float S3,
        uint Mask0,
        uint Mask1,
        uint Mask2,
        uint Mask3,
        uint Mask4,
        uint Mask5,
        uint Mask6,
        uint Mask7,
        uint Mask8,
        uint Mask9,
        uint Mask10,
        uint Mask11,
        uint Mask12,
        uint Mask13,
        uint Mask14,
        uint Mask15);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Search(
        PatMaxModel model,
        GranularityPyramid runtimePyramid,
        in PatMaxSearchParams searchParams,
        Span<CoarsePoseCandidate> candidateBuffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(runtimePyramid);
        if (!model.IsTrained)
        {
            throw new ArgumentException("Model must be trained before searching.", nameof(model));
        }

        if (searchParams.IsEmpty)
        {
            throw new ArgumentException("PatMaxSearchParams must not be empty.", nameof(searchParams));
        }

        if (candidateBuffer.IsEmpty || runtimePyramid.LevelCount == 0)
        {
            return 0;
        }

        return searchParams.IgnorePolarity
            ? SearchWithPolicy<IgnorePolarityPolicy>(model, runtimePyramid, in searchParams, candidateBuffer, cancellationToken)
            : SearchWithPolicy<SignedPolarityPolicy>(model, runtimePyramid, in searchParams, candidateBuffer, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int SearchWithPolicy<TPolicy>(
        PatMaxModel model,
        GranularityPyramid runtimePyramid,
        in PatMaxSearchParams searchParams,
        Span<CoarsePoseCandidate> candidateBuffer,
        CancellationToken cancellationToken = default)
        where TPolicy : struct, IPolarityPolicy
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(runtimePyramid);
        cancellationToken.ThrowIfCancellationRequested();
        var coarseLevelIdx = SelectOptimalCoarseLevel(model, runtimePyramid);
        var modelLevel = model.GetLevel(coarseLevelIdx);
        var dipoles = modelLevel.Dipoles;
        if (dipoles.IsEmpty)
        {
            modelLevel = model.FineLevel;
            dipoles = modelLevel.Dipoles;
            coarseLevelIdx = 0;
            if (dipoles.IsEmpty)
            {
                return 0;
            }
        }

        var runtimeView = runtimePyramid.GetLevel(coarseLevelIdx);
        var width = runtimeView.Width;
        var height = runtimeView.Height;
        var invScaleToCoarse = 1.0f / runtimeView.ScaleFactor;
        var scaleToBase = runtimeView.ScaleFactor;
        var pixelCount = checked(width * height);
        var dipoleCount = dipoles.Count;

        // [Perf Why]: Rent contiguous float/int/byte pools once per search to keep coarse translation scans zero-allocation.
        var totalFloats = checked((pixelCount * 3) + (dipoleCount * 5) + (dipoleCount + 1));
        var floatPool = ArrayPool<float>.Shared.Rent(totalFloats);
        var intPool = ArrayPool<int>.Shared.Rent(dipoleCount);
        var orientPool = ArrayPool<byte>.Shared.Rent(pixelCount);

        try
        {
            var poolSpan = floatPool.AsSpan(0, totalFloats);
            var runtimeUnitX = poolSpan.Slice(0, pixelCount);
            var runtimeUnitY = poolSpan.Slice(pixelCount, pixelCount);
            var runtimeMag = poolSpan.Slice(pixelCount * 2, pixelCount);
            var runtimeOrientBins = orientPool.AsSpan(0, pixelCount);

            var rotCos = poolSpan.Slice(pixelCount * 3, dipoleCount);
            var rotSin = poolSpan.Slice((pixelCount * 3) + dipoleCount, dipoleCount);
            var probeWeights = poolSpan.Slice((pixelCount * 3) + (dipoleCount * 2), dipoleCount);
            var rotDx = poolSpan.Slice((pixelCount * 3) + (dipoleCount * 3), dipoleCount);
            var rotDy = poolSpan.Slice((pixelCount * 3) + (dipoleCount * 4), dipoleCount);
            var remainingWeights = poolSpan.Slice((pixelCount * 3) + (dipoleCount * 5), dipoleCount + 1);
            var linearOffsets = intPool.AsSpan(0, dipoleCount);

            var coarseEdgeThreshold = coarseLevelIdx == 0
                ? searchParams.RuntimeEdgeThreshold
                : MathF.Max(4.0f, searchParams.RuntimeEdgeThreshold / MathF.Sqrt(scaleToBase));

            SubPixelEdgeExtractor.ComputeDenseUnitNormals(
                runtimeView.Pixels,
                width,
                height,
                runtimeView.Stride,
                coarseEdgeThreshold,
                runtimeUnitX,
                runtimeUnitY,
                runtimeMag);

            ZeroNormalDilation.Dilate3x3(
                runtimeUnitX,
                runtimeUnitY,
                runtimeMag,
                width,
                height,
                coarseEdgeThreshold);

            RotatedProbeTable.BuildQuantizedOrientationBins(runtimeUnitX, runtimeUnitY, runtimeOrientBins);

            var totalWeight = BuildRemainingWeightSchedule(dipoles.Magnitude, probeWeights, remainingWeights);
            if (totalWeight <= WeightFloorEpsilon)
            {
                return 0;
            }

            var angleStep = ComputeQuantizedAngleStep(model, scaleToBase);
            var isFullCircleRotation = searchParams.AngleExtentRadians >= TwoPi - 1e-3f;
            var angleSteps = searchParams.AngleExtentRadians > 0.0f
                ? isFullCircleRotation
                    ? Math.Max(2, (int)MathF.Ceiling(TwoPi / angleStep))
                    : Math.Max(1, (int)MathF.Ceiling(searchParams.AngleExtentRadians / angleStep) + 1)
                : 1;
            if (!isFullCircleRotation && angleSteps > 1 && (angleSteps & 1) == 0)
            {
                angleSteps++;
            }

            var actualAngleStep = angleSteps > 1
                ? isFullCircleRotation
                    ? TwoPi / angleSteps
                    : searchParams.AngleExtentRadians / (angleSteps - 1)
                : 0.0f;

            var (scaleStart, scaleSteps, actualScaleStep) = ComputeCoarseScaleSchedule(
                dipoles,
                invScaleToCoarse,
                searchParams.ScaleMin,
                searchParams.ScaleMax);

            var maxAllowedCandidates = Math.Min(searchParams.MaxCandidates, candidateBuffer.Length);
            var foundCount = 0;
            var minSeparationBase = MathF.Max(scaleToBase, searchParams.MinSeparationPixels);
            var minSeparationSq = minSeparationBase * minSeparationBase;
            var coarseAcceptThreshold = MathF.Max(0.25f, searchParams.AcceptThreshold * 0.65f);
            var totalPoses = scaleSteps * angleSteps;

            if (totalPoses > 1 && width * height >= Stride2CoarsePixelThreshold && (long)pixelCount * totalPoses >= ParallelWorkUnitsThreshold)
            {
                foundCount = SearchPosesParallel<TPolicy>(
                    dipoles,
                    runtimeUnitX,
                    runtimeUnitY,
                    runtimeOrientBins,
                    width,
                    height,
                    scaleStart,
                    scaleSteps,
                    angleSteps,
                    in searchParams,
                    actualScaleStep,
                    actualAngleStep,
                    invScaleToCoarse,
                    scaleToBase,
                    probeWeights,
                    remainingWeights,
                    totalWeight,
                    coarseAcceptThreshold,
                    minSeparationSq,
                    maxAllowedCandidates,
                    candidateBuffer.Slice(0, maxAllowedCandidates),
                    cancellationToken);
            }
            else
            {
                for (var sIdx = 0; sIdx < scaleSteps; sIdx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scale = scaleStart + (sIdx * actualScaleStep);
                    var netScale = scale * invScaleToCoarse;

                    for (var aIdx = 0; aIdx < angleSteps; aIdx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var angle = searchParams.AngleStartRadians + (aIdx * actualAngleStep);
                        var bounds = RotatedProbeTable.Prepare(
                            dipoles,
                            angle,
                            netScale,
                            width,
                            rotDx,
                            rotDy,
                            rotCos,
                            rotSin,
                            linearOffsets);

                        var startX = Math.Max(1, -bounds.MinOffsetX);
                        var endX = Math.Min(width - 2, (width - 2) - bounds.MaxOffsetX);
                        var startY = Math.Max(1, -bounds.MinOffsetY);
                        var endY = Math.Min(height - 2, (height - 2) - bounds.MaxOffsetY);

                        if (startX > endX || startY > endY)
                        {
                            continue;
                        }

                        ScanTranslations<TPolicy>(
                            runtimeUnitX,
                            runtimeUnitY,
                            runtimeOrientBins,
                            width,
                            startX,
                            endX,
                            startY,
                            endY,
                            linearOffsets,
                            rotCos,
                            rotSin,
                            probeWeights,
                            remainingWeights,
                            totalWeight,
                            coarseAcceptThreshold,
                            scaleToBase,
                            angle,
                            scale,
                            minSeparationSq,
                            candidateBuffer.Slice(0, maxAllowedCandidates),
                            ref foundCount);
                    }
                }
            }

            if (foundCount > 0 && coarseLevelIdx >= 2)
            {
                RefineCandidatesAtMidLevel<TPolicy>(
                    model,
                    runtimePyramid,
                    coarseLevelIdx - 1,
                    in searchParams,
                    actualAngleStep,
                    candidateBuffer.Slice(0, foundCount),
                    cancellationToken);
            }

            return foundCount;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatPool);
            ArrayPool<int>.Shared.Return(intPool);
            ArrayPool<byte>.Shared.Return(orientPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int SelectOptimalCoarseLevel(PatMaxModel model, GranularityPyramid runtimePyramid)
    {
        var maxLevelIdx = Math.Min(model.LevelCount - 1, runtimePyramid.LevelCount - 1);
        var minBaseDimension = (float)Math.Min(model.PatternWidth, model.PatternHeight);

        for (var levelIdx = maxLevelIdx; levelIdx >= 0; levelIdx--)
        {
            var runtimeView = runtimePyramid.GetLevel(levelIdx);
            if (!runtimeView.HasPixels)
            {
                continue;
            }

            var modelLevel = model.GetLevel(levelIdx);
            var scaledMinSide = minBaseDimension / MathF.Max(1.0f, modelLevel.ScaleFactor);
            if (scaledMinSide >= MinCoarsePatternSidePixels && modelLevel.Dipoles.Count >= MinCoarseSalientProbeCount)
            {
                return levelIdx;
            }
        }

        for (var levelIdx = maxLevelIdx; levelIdx >= 0; levelIdx--)
        {
            var runtimeView = runtimePyramid.GetLevel(levelIdx);
            if (!runtimeView.HasPixels)
            {
                continue;
            }

            var modelLevel = model.GetLevel(levelIdx);
            var scaledMinSide = minBaseDimension / MathF.Max(1.0f, modelLevel.ScaleFactor);
            if (scaledMinSide >= MinCoarsePatternSidePixels && !modelLevel.Dipoles.IsEmpty)
            {
                return levelIdx;
            }
        }

        return runtimePyramid.LevelCount > 1 && !runtimePyramid.GetLevel(0).HasPixels ? 1 : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (float ScaleStart, int ScaleSteps, float ActualScaleStep) ComputeCoarseScaleSchedule(
        EdgeDipoleSoA dipoles,
        float invScaleToCoarse,
        float scaleMin,
        float scaleMax)
    {
        var scaleRange = scaleMax - scaleMin;
        if (scaleRange <= 1e-5f)
        {
            return (scaleMin, 1, 0.0f);
        }

        var count = dipoles.Count;
        var srcX = dipoles.X;
        var srcY = dipoles.Y;
        var maxRadiusSqBase = 0.0f;
        for (var i = 0; i < count; i++)
        {
            var rSq = (srcX[i] * srcX[i]) + (srcY[i] * srcY[i]);
            if (rSq > maxRadiusSqBase)
            {
                maxRadiusSqBase = rSq;
            }
        }

        var radiusCoarse = MathF.Sqrt(maxRadiusSqBase) * invScaleToCoarse;
        var repScale = (scaleMin <= 1.0f && scaleMax >= 1.0f) ? 1.0f : 0.5f * (scaleMin + scaleMax);
        var maxScaleDelta = MathF.Max(MathF.Abs(scaleMin - repScale), MathF.Abs(scaleMax - repScale));
        var maxOuterDisplacementPx = radiusCoarse * maxScaleDelta;

        if (maxOuterDisplacementPx <= MaxSingleScaleOuterDisplacementPx)
        {
            return (repScale, 1, 0.0f);
        }

        var safeScaleStep = MathF.Max(DefaultScaleStep, (2.0f * MaxSingleScaleOuterDisplacementPx) / MathF.Max(1.0f, radiusCoarse));
        var scaleSteps = Math.Max(1, (int)MathF.Ceiling(scaleRange / safeScaleStep) + 1);
        if (scaleSteps > 1 && (scaleSteps & 1) == 0)
        {
            scaleSteps++;
        }

        return (scaleMin, scaleSteps, scaleSteps > 1 ? scaleRange / (scaleSteps - 1) : 0.0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RefineCandidatesAtMidLevel<TPolicy>(
        PatMaxModel model,
        GranularityPyramid runtimePyramid,
        int midLevelIdx,
        in PatMaxSearchParams searchParams,
        float seedAngleStep,
        Span<CoarsePoseCandidate> candidates,
        CancellationToken cancellationToken)
        where TPolicy : struct, IPolarityPolicy
    {
        if (midLevelIdx <= 0 || candidates.IsEmpty)
        {
            return;
        }

        var modelLevel = model.GetLevel(midLevelIdx);
        var dipoles = modelLevel.Dipoles;
        if (dipoles.Count < MinCoarseSalientProbeCount)
        {
            return;
        }

        var runtimeView = runtimePyramid.GetLevel(midLevelIdx);
        if (!runtimeView.HasPixels)
        {
            return;
        }

        var width = runtimeView.Width;
        var height = runtimeView.Height;
        var stride = runtimeView.Stride;
        var scaleToBase = runtimeView.ScaleFactor;
        var invScaleToMid = 1.0f / scaleToBase;
        var dipoleCount = dipoles.Count;

        var maxLocalExtentX = MathF.Max(MathF.Abs(model.MinDx), MathF.Abs(model.MaxDx));
        var maxLocalExtentY = MathF.Max(MathF.Abs(model.MinDy), MathF.Abs(model.MaxDy));
        var modelDiagRadiusMid = MathF.Sqrt((maxLocalExtentX * maxLocalExtentX) + (maxLocalExtentY * maxLocalExtentY)) * searchParams.ScaleMax * invScaleToMid;
        var patchRadius = (int)MathF.Ceiling(modelDiagRadiusMid) + 6;
        var maxPatchDim = Math.Min(Math.Max(width, height), (patchRadius * 2) + 1);
        var maxPatchPixels = checked(maxPatchDim * maxPatchDim);

        // [Perf Why]: Rent buffer only for a single local candidate patch (e.g. 64x64) rather than the full 2048x2048 mid-level frame.
        var totalFloats = checked((maxPatchPixels * 3) + (dipoleCount * 5) + (dipoleCount + 1));
        var floatPool = ArrayPool<float>.Shared.Rent(totalFloats);
        var intPool = ArrayPool<int>.Shared.Rent(dipoleCount);

        try
        {
            var poolSpan = floatPool.AsSpan(0, totalFloats);
            var patchUnitX = poolSpan.Slice(0, maxPatchPixels);
            var patchUnitY = poolSpan.Slice(maxPatchPixels, maxPatchPixels);
            var patchMag = poolSpan.Slice(maxPatchPixels * 2, maxPatchPixels);
            var rotCos = poolSpan.Slice(maxPatchPixels * 3, dipoleCount);
            var rotSin = poolSpan.Slice((maxPatchPixels * 3) + dipoleCount, dipoleCount);
            var probeWeights = poolSpan.Slice((maxPatchPixels * 3) + (dipoleCount * 2), dipoleCount);
            var rotDx = poolSpan.Slice((maxPatchPixels * 3) + (dipoleCount * 3), dipoleCount);
            var rotDy = poolSpan.Slice((maxPatchPixels * 3) + (dipoleCount * 4), dipoleCount);
            var remainingWeights = poolSpan.Slice((maxPatchPixels * 3) + (dipoleCount * 5), dipoleCount + 1);
            var linearOffsets = intPool.AsSpan(0, dipoleCount);

            var totalWeight = BuildRemainingWeightSchedule(dipoles.Magnitude, probeWeights, remainingWeights);
            if (totalWeight <= WeightFloorEpsilon)
            {
                return;
            }

            var midEdgeThreshold = MathF.Max(4.0f, searchParams.RuntimeEdgeThreshold / MathF.Sqrt(scaleToBase));
            var invTotalWeight = 1.0f / totalWeight;
            var midAngleDelta = seedAngleStep > 0.0f ? 0.5f * seedAngleStep : 0.0f;
            var angleSubSteps = midAngleDelta > 1e-4f ? 3 : 1;
            var isFullCircleRotation = searchParams.AngleExtentRadians >= TwoPi - 1e-3f;

            var scaleRange = searchParams.ScaleMax - searchParams.ScaleMin;
            var scaleSubSteps = scaleRange > 1e-4f ? 3 : 1;
            var scaleSubStep = scaleSubSteps > 1 ? 0.5f * scaleRange : 0.0f;

            var viewPixels = runtimeView.Pixels;
            ref var offsetRef = ref MemoryMarshal.GetReference(linearOffsets);
            ref var cosRef = ref MemoryMarshal.GetReference(rotCos);
            ref var sinRef = ref MemoryMarshal.GetReference(rotSin);
            ref var weightRef = ref MemoryMarshal.GetReference(probeWeights);
            ref var remWeightRef = ref MemoryMarshal.GetReference(remainingWeights);

            for (var cIdx = 0; cIdx < candidates.Length; cIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seed = candidates[cIdx];
                var centerMidX = (int)MathF.Floor((seed.X * invScaleToMid) + 0.5f);
                var centerMidY = (int)MathF.Floor((seed.Y * invScaleToMid) + 0.5f);

                var patchLeft = Math.Clamp(centerMidX - patchRadius, 0, width - 1);
                var patchTop = Math.Clamp(centerMidY - patchRadius, 0, height - 1);
                var patchRight = Math.Clamp(centerMidX + patchRadius + 1, patchLeft + 1, width);
                var patchBottom = Math.Clamp(centerMidY + patchRadius + 1, patchTop + 1, height);
                var patchW = patchRight - patchLeft;
                var patchH = patchBottom - patchTop;
                var patchPixels = patchW * patchH;
                if (patchW < 5 || patchH < 5)
                {
                    continue;
                }

                var localUx = patchUnitX.Slice(0, patchPixels);
                var localUy = patchUnitY.Slice(0, patchPixels);
                var localMag = patchMag.Slice(0, patchPixels);
                var patchSlice = viewPixels.Slice((patchTop * stride) + patchLeft);

                SubPixelEdgeExtractor.ComputeDenseUnitNormals(
                    patchSlice,
                    patchW,
                    patchH,
                    stride,
                    midEdgeThreshold,
                    localUx,
                    localUy,
                    localMag);

                ZeroNormalDilation.Dilate3x3(
                    localUx,
                    localUy,
                    localMag,
                    patchW,
                    patchH,
                    midEdgeThreshold);

                ref var unitXRef = ref MemoryMarshal.GetReference(localUx);
                ref var unitYRef = ref MemoryMarshal.GetReference(localUy);
                var localCenterX = centerMidX - patchLeft;
                var localCenterY = centerMidY - patchTop;

                var bestScore = -1.0f;
                var bestCand = seed;

                for (var sIdx = 0; sIdx < scaleSubSteps; sIdx++)
                {
                    var testScale = scaleSubSteps > 1
                        ? Math.Clamp(searchParams.ScaleMin + (sIdx * scaleSubStep), searchParams.ScaleMin, searchParams.ScaleMax)
                        : seed.Scale;
                    var netScale = testScale * invScaleToMid;

                    for (var aIdx = 0; aIdx < angleSubSteps; aIdx++)
                    {
                        float testAngle;
                        if (angleSubSteps > 1)
                        {
                            var rawAngle = seed.AngleRadians + ((aIdx - 1) * midAngleDelta);
                            testAngle = isFullCircleRotation
                                ? WrapAnglePi(rawAngle)
                                : Math.Clamp(rawAngle, searchParams.AngleStartRadians, searchParams.AngleStartRadians + searchParams.AngleExtentRadians);
                        }
                        else
                        {
                            testAngle = seed.AngleRadians;
                        }

                        var bounds = RotatedProbeTable.Prepare(
                            dipoles,
                            testAngle,
                            netScale,
                            patchW,
                            rotDx,
                            rotDy,
                            rotCos,
                            rotSin,
                            linearOffsets);

                        var minValidX = Math.Max(1, -bounds.MinOffsetX);
                        var maxValidX = Math.Min(patchW - 2, (patchW - 2) - bounds.MaxOffsetX);
                        var minValidY = Math.Max(1, -bounds.MinOffsetY);
                        var maxValidY = Math.Min(patchH - 2, (patchH - 2) - bounds.MaxOffsetY);

                        var startX = Math.Max(minValidX, localCenterX - 2);
                        var endX = Math.Min(maxValidX, localCenterX + 2);
                        var startY = Math.Max(minValidY, localCenterY - 2);
                        var endY = Math.Min(maxValidY, localCenterY + 2);

                        for (var my = startY; my <= endY; my++)
                        {
                            var rowBase = my * patchW;
                            for (var mx = startX; mx <= endX; mx++)
                            {
                                var sum = EvaluatePoseProbes<TPolicy>(
                                    ref unitXRef,
                                    ref unitYRef,
                                    rowBase + mx,
                                    ref offsetRef,
                                    ref cosRef,
                                    ref sinRef,
                                    ref weightRef,
                                    ref remWeightRef,
                                    dipoleCount,
                                    totalWeight,
                                    0.1f,
                                    0.0f);

                                var normScore = sum * invTotalWeight;
                                if (normScore > bestScore)
                                {
                                    bestScore = normScore;
                                    bestCand = new CoarsePoseCandidate(
                                        X: (patchLeft + mx + 0.5f) * scaleToBase,
                                        Y: (patchTop + my + 0.5f) * scaleToBase,
                                        AngleRadians: testAngle,
                                        Scale: testScale,
                                        Score: MathF.Max(seed.Score, normScore));
                                }
                            }
                        }
                    }
                }

                candidates[cIdx] = bestCand;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatPool);
            ArrayPool<int>.Shared.Return(intPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe int SearchPosesParallel<TPolicy>(
        EdgeDipoleSoA dipoles,
        ReadOnlySpan<float> runtimeUnitX,
        ReadOnlySpan<float> runtimeUnitY,
        ReadOnlySpan<byte> runtimeOrientBins,
        int width,
        int height,
        float scaleStart,
        int scaleSteps,
        int angleSteps,
        in PatMaxSearchParams searchParams,
        float actualScaleStep,
        float actualAngleStep,
        float invScaleToCoarse,
        float scaleToBase,
        ReadOnlySpan<float> probeWeights,
        ReadOnlySpan<float> remainingWeights,
        float totalWeight,
        float coarseAcceptThreshold,
        float minSeparationSq,
        int maxAllowedCandidates,
        Span<CoarsePoseCandidate> candidateBuffer,
        CancellationToken cancellationToken)
        where TPolicy : struct, IPolarityPolicy
    {
        var totalPoses = scaleSteps * angleSteps;
        var workerCount = Math.Clamp(Math.Min(Environment.ProcessorCount, totalPoses), 1, MaxParallelDegree);
        var dipoleCount = dipoles.Count;
        var floatsPerWorker = dipoleCount * 4;
        var workerFloatPool = ArrayPool<float>.Shared.Rent(workerCount * floatsPerWorker);
        var workerIntPool = ArrayPool<int>.Shared.Rent(workerCount * dipoleCount);
        var workerCandPool = ArrayPool<CoarsePoseCandidate>.Shared.Rent(workerCount * maxAllowedCandidates);
        var workerCounts = new int[workerCount];

        try
        {
            var angleStart = searchParams.AngleStartRadians;
            var pixelCount = width * height;

            fixed (float* pUnitX = runtimeUnitX)
            fixed (float* pUnitY = runtimeUnitY)
            fixed (byte* pOrientBins = runtimeOrientBins)
            fixed (float* pWeights = probeWeights)
            fixed (float* pRemWeights = remainingWeights)
            fixed (float* pWorkerFloats = workerFloatPool)
            fixed (int* pWorkerInts = workerIntPool)
            fixed (CoarsePoseCandidate* pWorkerCands = workerCandPool)
            {
                var unitXAddr = (nint)pUnitX;
                var unitYAddr = (nint)pUnitY;
                var orientBinsAddr = (nint)pOrientBins;
                var weightsAddr = (nint)pWeights;
                var remWeightsAddr = (nint)pRemWeights;
                var workerFloatsAddr = (nint)pWorkerFloats;
                var workerIntsAddr = (nint)pWorkerInts;
                var workerCandsAddr = (nint)pWorkerCands;

                Parallel.For(0, workerCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = workerCount,
                    CancellationToken = cancellationToken
                }, w =>
                {
                    var poseStart = (w * totalPoses) / workerCount;
                    var poseEnd = ((w + 1) * totalPoses) / workerCount;
                    if (poseStart >= poseEnd)
                    {
                        return;
                    }

                    var localFloats = new Span<float>((float*)workerFloatsAddr + (w * floatsPerWorker), floatsPerWorker);
                    var rotCos = localFloats.Slice(0, dipoleCount);
                    var rotSin = localFloats.Slice(dipoleCount, dipoleCount);
                    var rotDx = localFloats.Slice(dipoleCount * 2, dipoleCount);
                    var rotDy = localFloats.Slice(dipoleCount * 3, dipoleCount);
                    var linearOffsets = new Span<int>((int*)workerIntsAddr + (w * dipoleCount), dipoleCount);
                    var localCandidates = new Span<CoarsePoseCandidate>((CoarsePoseCandidate*)workerCandsAddr + (w * maxAllowedCandidates), maxAllowedCandidates);
                    var localUnitX = new ReadOnlySpan<float>((float*)unitXAddr, pixelCount);
                    var localUnitY = new ReadOnlySpan<float>((float*)unitYAddr, pixelCount);
                    var localOrientBins = new ReadOnlySpan<byte>((byte*)orientBinsAddr, pixelCount);
                    var localWeights = new ReadOnlySpan<float>((float*)weightsAddr, dipoleCount);
                    var localRemWeights = new ReadOnlySpan<float>((float*)remWeightsAddr, dipoleCount + 1);
                    var localFound = 0;

                    for (var poseIdx = poseStart; poseIdx < poseEnd; poseIdx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sIdx = poseIdx / angleSteps;
                        var aIdx = poseIdx % angleSteps;
                        var scale = scaleStart + (sIdx * actualScaleStep);
                        var netScale = scale * invScaleToCoarse;
                        var angle = angleStart + (aIdx * actualAngleStep);

                        var bounds = RotatedProbeTable.Prepare(
                            dipoles,
                            angle,
                            netScale,
                            width,
                            rotDx,
                            rotDy,
                            rotCos,
                            rotSin,
                            linearOffsets);

                        var startX = Math.Max(1, -bounds.MinOffsetX);
                        var endX = Math.Min(width - 2, (width - 2) - bounds.MaxOffsetX);
                        var startY = Math.Max(1, -bounds.MinOffsetY);
                        var endY = Math.Min(height - 2, (height - 2) - bounds.MaxOffsetY);

                        if (startX > endX || startY > endY)
                        {
                            continue;
                        }

                        ScanTranslations<TPolicy>(
                            localUnitX,
                            localUnitY,
                            localOrientBins,
                            width,
                            startX,
                            endX,
                            startY,
                            endY,
                            linearOffsets,
                            rotCos,
                            rotSin,
                            localWeights,
                            localRemWeights,
                            totalWeight,
                            coarseAcceptThreshold,
                            scaleToBase,
                            angle,
                            scale,
                            minSeparationSq,
                            localCandidates,
                            ref localFound);
                    }

                    workerCounts[w] = localFound;
                });
            }

            var mergedCount = 0;
            for (var w = 0; w < workerCount; w++)
            {
                var count = workerCounts[w];
                var baseIdx = w * maxAllowedCandidates;
                for (var i = 0; i < count; i++)
                {
                    InsertOrSuppressCandidate(candidateBuffer, ref mergedCount, in workerCandPool[baseIdx + i], minSeparationSq);
                }
            }

            return mergedCount;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(workerFloatPool);
            ArrayPool<int>.Shared.Return(workerIntPool);
            ArrayPool<CoarsePoseCandidate>.Shared.Return(workerCandPool);
        }
    }

    private static float BuildRemainingWeightSchedule(
        ReadOnlySpan<float> sourceMagnitudes,
        Span<float> weights,
        Span<float> remainingWeights)
    {
        var count = sourceMagnitudes.Length;
        sourceMagnitudes.CopyTo(weights);

        var accum = 0.0f;
        remainingWeights[count] = 0.0f;
        for (var i = count - 1; i >= 0; i--)
        {
            accum += weights[i];
            remainingWeights[i] = accum;
        }

        return accum;
    }

    private static float ComputeQuantizedAngleStep(PatMaxModel model, float coarseScaleFactor)
    {
        var halfW = model.PatternWidth * 0.5f;
        var halfH = model.PatternHeight * 0.5f;
        var radius = MathF.Max(1.0f, MathF.Sqrt((halfW * halfW) + (halfH * halfH)) / coarseScaleFactor);
        var step = 1.0f / radius;
        return Math.Clamp(step, MinAngleStepRadians, MaxAngleStepRadians);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ScanTranslations<TPolicy>(
        ReadOnlySpan<float> runtimeUnitX,
        ReadOnlySpan<float> runtimeUnitY,
        ReadOnlySpan<byte> runtimeOrientBins,
        int coarseWidth,
        int startX,
        int endX,
        int startY,
        int endY,
        ReadOnlySpan<int> linearOffsets,
        ReadOnlySpan<float> rotCos,
        ReadOnlySpan<float> rotSin,
        ReadOnlySpan<float> probeWeights,
        ReadOnlySpan<float> remainingWeights,
        float totalWeight,
        float acceptThreshold,
        float scaleToBase,
        float angleRadians,
        float scale,
        float minSeparationSq,
        Span<CoarsePoseCandidate> candidateBuffer,
        ref int foundCount)
        where TPolicy : struct, IPolarityPolicy
    {
        // === Stage 1: Threshold & Reference Setup ===
        var requiredWeightedSum = acceptThreshold * totalWeight;
        var invTotalWeight = 1.0f / totalWeight;
        var count = linearOffsets.Length;
        var ignorePolarity = typeof(TPolicy) == typeof(IgnorePolarityPolicy);

        ref var unitXRef = ref MemoryMarshal.GetReference(runtimeUnitX);
        ref var unitYRef = ref MemoryMarshal.GetReference(runtimeUnitY);
        ref var orientBinRef = ref MemoryMarshal.GetReference(runtimeOrientBins);
        ref var offsetRef = ref MemoryMarshal.GetReference(linearOffsets);
        ref var cosRef = ref MemoryMarshal.GetReference(rotCos);
        ref var sinRef = ref MemoryMarshal.GetReference(rotSin);
        ref var weightRef = ref MemoryMarshal.GetReference(probeWeights);
        ref var remWeightRef = ref MemoryMarshal.GetReference(remainingWeights);

        var scanWidth = endX - startX + 1;
        var scanHeight = endY - startY + 1;
        var minAnchorDirectionalSum = 4.0f * acceptThreshold * AnchorDirectionalGateFactor;

        // === Stage 2: Stride-2 Coarse Peak Scan + 3x3 Peak Refinement (Large Frames) ===
        if (scanWidth * scanHeight >= Stride2CoarsePixelThreshold && count >= 4)
        {
            var stride2GateWeight = requiredWeightedSum * CoarseNeighborDilationFactor;
            var gate = BuildAnchorGateState(ref offsetRef, ref cosRef, ref sinRef, ref weightRef, ref remWeightRef, count, stride2GateWeight, ignorePolarity);
            ScanStride2AndEmit<TPolicy>(
                coarseWidth,
                startX,
                endX,
                startY,
                endY,
                ref unitXRef,
                ref unitYRef,
                ref orientBinRef,
                ref offsetRef,
                ref cosRef,
                ref sinRef,
                ref weightRef,
                ref remWeightRef,
                in gate,
                count,
                totalWeight,
                acceptThreshold,
                requiredWeightedSum,
                stride2GateWeight,
                minAnchorDirectionalSum,
                minSeparationSq,
                invTotalWeight,
                scaleToBase,
                angleRadians,
                scale,
                candidateBuffer,
                ref foundCount);
            return;
        }

        // === Stage 3: Dense Stride-1 Scan (Compact Frames or Small Probe Sets) ===
        var denseGate = count >= 4
            ? BuildAnchorGateState(ref offsetRef, ref cosRef, ref sinRef, ref weightRef, ref remWeightRef, count, requiredWeightedSum, ignorePolarity)
            : default;

        ScanDenseRows<TPolicy>(
            coarseWidth,
            startX,
            endX,
            startY,
            endY,
            ref unitXRef,
            ref unitYRef,
            ref orientBinRef,
            ref offsetRef,
            ref cosRef,
            ref sinRef,
            ref weightRef,
            ref remWeightRef,
            in denseGate,
            count,
            totalWeight,
            acceptThreshold,
            requiredWeightedSum,
            minAnchorDirectionalSum,
            minSeparationSq,
            scaleToBase,
            angleRadians,
            scale,
            invTotalWeight,
            candidateBuffer,
            ref foundCount);
    }

    // [Perf Why]: Dedicated NoInlining+AggressiveOptimization method isolates the Stride-2 stackalloc + peak refinement frame from the Stride-1 dense scan.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void ScanStride2AndEmit<TPolicy>(
        int coarseWidth,
        int startX,
        int endX,
        int startY,
        int endY,
        ref float unitXRef,
        ref float unitYRef,
        ref byte orientBinRef,
        ref int offsetRef,
        ref float cosRef,
        ref float sinRef,
        ref float weightRef,
        ref float remWeightRef,
        in AnchorGateState gate,
        int count,
        float totalWeight,
        float acceptThreshold,
        float requiredWeightedSum,
        float stride2GateWeight,
        float minAnchorDirectionalSum,
        float minSeparationSq,
        float invTotalWeight,
        float scaleToBase,
        float angleRadians,
        float scale,
        Span<CoarsePoseCandidate> candidateBuffer,
        ref int foundCount)
        where TPolicy : struct, IPolarityPolicy
    {
        var maxPosePeaks = Math.Clamp(candidateBuffer.Length, 1, MaxPoseLocalPeaks);
        Span<CoarseGridPeak> posePeaks = stackalloc CoarseGridPeak[maxPosePeaks];
        var peakCount = 0;
        var invScaleToBase = 1.0f / MathF.Max(1.0f, scaleToBase);
        var coarseSeparationSq = MathF.Max(4.0f, minSeparationSq * invScaleToBase * invScaleToBase);

        for (var cy = startY; cy <= endY; cy += 2)
        {
            ScanRowBlock<TPolicy>(
                cy,
                coarseWidth,
                startX,
                endX,
                stepX: 2,
                ref unitXRef,
                ref unitYRef,
                ref orientBinRef,
                ref offsetRef,
                ref cosRef,
                ref sinRef,
                ref weightRef,
                ref remWeightRef,
                in gate,
                count,
                totalWeight,
                acceptThreshold * CoarseNeighborDilationFactor,
                stride2GateWeight,
                minAnchorDirectionalSum,
                coarseSeparationSq,
                scaleToBase,
                angleRadians,
                scale,
                invTotalWeight,
                emitToPosePeaks: true,
                posePeaks,
                ref peakCount,
                candidateBuffer,
                ref foundCount);
        }

        // [Perf Why]: Execute 3x3 neighborhood refinement only once per surviving local maximum peak instead of on every passing grid cell.
        for (var p = 0; p < peakCount; p++)
        {
            EmitGridPeak<TPolicy>(
                in posePeaks[p],
                coarseWidth,
                startX,
                endX,
                startY,
                endY,
                ref unitXRef,
                ref unitYRef,
                ref offsetRef,
                ref cosRef,
                ref sinRef,
                ref weightRef,
                ref remWeightRef,
                count,
                totalWeight,
                acceptThreshold,
                requiredWeightedSum,
                invTotalWeight,
                scaleToBase,
                angleRadians,
                scale,
                minSeparationSq,
                candidateBuffer,
                ref foundCount);
        }
    }

    // [Perf Why]: Dedicated NoInlining+AggressiveOptimization method keeps AnchorGateState fields enregistered across the dense 2D scan.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void ScanDenseRows<TPolicy>(
        int coarseWidth,
        int startX,
        int endX,
        int startY,
        int endY,
        ref float unitXRef,
        ref float unitYRef,
        ref byte orientBinRef,
        ref int offsetRef,
        ref float cosRef,
        ref float sinRef,
        ref float weightRef,
        ref float remWeightRef,
        in AnchorGateState gate,
        int count,
        float totalWeight,
        float acceptThreshold,
        float requiredWeightedSum,
        float minAnchorDirectionalSum,
        float minSeparationSq,
        float scaleToBase,
        float angleRadians,
        float scale,
        float invTotalWeight,
        Span<CoarsePoseCandidate> candidateBuffer,
        ref int foundCount)
        where TPolicy : struct, IPolarityPolicy
    {
        var dummyPeakCount = 0;
        for (var cy = startY; cy <= endY; cy++)
        {
            ScanRowBlock<TPolicy>(
                cy,
                coarseWidth,
                startX,
                endX,
                stepX: 1,
                ref unitXRef,
                ref unitYRef,
                ref orientBinRef,
                ref offsetRef,
                ref cosRef,
                ref sinRef,
                ref weightRef,
                ref remWeightRef,
                in gate,
                count,
                totalWeight,
                acceptThreshold,
                requiredWeightedSum,
                minAnchorDirectionalSum,
                minSeparationSq,
                scaleToBase,
                angleRadians,
                scale,
                invTotalWeight,
                emitToPosePeaks: false,
                default,
                ref dummyPeakCount,
                candidateBuffer,
                ref foundCount);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ScanRowBlock<TPolicy>(
        int cy,
        int coarseWidth,
        int startX,
        int endX,
        int stepX,
        ref float unitXRef,
        ref float unitYRef,
        ref byte orientBinRef,
        ref int offsetRef,
        ref float cosRef,
        ref float sinRef,
        ref float weightRef,
        ref float remWeightRef,
        in AnchorGateState gate,
        int count,
        float totalWeight,
        float probeAcceptThreshold,
        float gateThreshold,
        float minAnchorDirectionalSum,
        float separationSq,
        float scaleToBase,
        float angleRadians,
        float scale,
        float invTotalWeight,
        bool emitToPosePeaks,
        Span<CoarseGridPeak> posePeaks,
        ref int peakCount,
        Span<CoarsePoseCandidate> candidateBuffer,
        ref int foundCount)
        where TPolicy : struct, IPolarityPolicy
    {
        var rowBase = cy * coarseWidth;
        for (var cx = startX; cx <= endX; cx += stepX)
        {
            var baseLinearIdx = rowBase + cx;
            if (gate.Has4Probes &&
                ApplyEarlyReject<TPolicy>(
                    baseLinearIdx,
                    ref unitXRef,
                    ref unitYRef,
                    ref orientBinRef,
                    ref offsetRef,
                    ref weightRef,
                    in gate,
                    count,
                    gateThreshold,
                    minAnchorDirectionalSum))
            {
                continue;
            }

            var scoreSum = EvaluatePoseProbes<TPolicy>(
                ref unitXRef,
                ref unitYRef,
                baseLinearIdx,
                ref offsetRef,
                ref cosRef,
                ref sinRef,
                ref weightRef,
                ref remWeightRef,
                count,
                totalWeight,
                probeAcceptThreshold,
                gateThreshold);

            if (scoreSum < gateThreshold)
            {
                continue;
            }

            if (emitToPosePeaks)
            {
                InsertOrSuppressGridPeak(posePeaks, ref peakCount, cx, cy, scoreSum, separationSq);
            }
            else
            {
                var candidate = new CoarsePoseCandidate(
                    X: (cx + 0.5f) * scaleToBase,
                    Y: (cy + 0.5f) * scaleToBase,
                    AngleRadians: angleRadians,
                    Scale: scale,
                    Score: scoreSum * invTotalWeight);
                InsertOrSuppressCandidate(candidateBuffer, ref foundCount, in candidate, separationSq);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ApplyEarlyReject<TPolicy>(
        int baseLinearIdx,
        ref float unitXRef,
        ref float unitYRef,
        ref byte orientBinRef,
        ref int offsetRef,
        ref float weightRef,
        in AnchorGateState gate,
        int count,
        float requiredThreshold,
        float minAnchorDirectionalSum)
        where TPolicy : struct, IPolarityPolicy
    {
        if (gate.Has8Probes)
        {
            var b0 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off0);
            var b1 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off1);
            var b2 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off2);
            var b3 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off3);
            var b4 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off4);
            var b5 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off5);
            var b6 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off6);
            var b7 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off7);

            var lutHits8 = ((b0 | b1 | b2 | b3 | b4 | b5 | b6 | b7) == 0)
                ? 0
                : (b0 != 0 ? (int)((gate.Mask0 >> (b0 - 1)) & 1u) : 0)
                    + (b1 != 0 ? (int)((gate.Mask1 >> (b1 - 1)) & 1u) : 0)
                    + (b2 != 0 ? (int)((gate.Mask2 >> (b2 - 1)) & 1u) : 0)
                    + (b3 != 0 ? (int)((gate.Mask3 >> (b3 - 1)) & 1u) : 0)
                    + (b4 != 0 ? (int)((gate.Mask4 >> (b4 - 1)) & 1u) : 0)
                    + (b5 != 0 ? (int)((gate.Mask5 >> (b5 - 1)) & 1u) : 0)
                    + (b6 != 0 ? (int)((gate.Mask6 >> (b6 - 1)) & 1u) : 0)
                    + (b7 != 0 ? (int)((gate.Mask7 >> (b7 - 1)) & 1u) : 0);

            if ((lutHits8 * gate.MaxSingleWeight8) + gate.RemainingAfter8 < requiredThreshold)
            {
                return true;
            }

            if (gate.Has12Probes && (lutHits8 < MinAnchorLutMatches || (lutHits8 * gate.MaxSingleWeight8) + gate.RemainingAfter12 < requiredThreshold))
            {
                var b8 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off8);
                var b9 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off9);
                var b10 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off10);
                var b11 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off11);
                var lutHits8To11 = ((b8 | b9 | b10 | b11) == 0)
                    ? 0
                    : (b8 != 0 ? (int)((gate.Mask8 >> (b8 - 1)) & 1u) : 0)
                        + (b9 != 0 ? (int)((gate.Mask9 >> (b9 - 1)) & 1u) : 0)
                        + (b10 != 0 ? (int)((gate.Mask10 >> (b10 - 1)) & 1u) : 0)
                        + (b11 != 0 ? (int)((gate.Mask11 >> (b11 - 1)) & 1u) : 0);
                var totalLutHits12 = lutHits8 + lutHits8To11;
                if ((totalLutHits12 * gate.MaxSingleWeight12) + gate.RemainingAfter12 < requiredThreshold)
                {
                    return true;
                }

                if (gate.Has16Probes && (totalLutHits12 < MinAnchorLutMatches || (totalLutHits12 * gate.MaxSingleWeight12) + gate.RemainingAfter16 < requiredThreshold))
                {
                    var b12 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off12);
                    var b13 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off13);
                    var b14 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off14);
                    var b15 = Unsafe.Add(ref orientBinRef, baseLinearIdx + gate.Off15);
                    var lutHits12To15 = ((b12 | b13 | b14 | b15) == 0)
                        ? 0
                        : (b12 != 0 ? (int)((gate.Mask12 >> (b12 - 1)) & 1u) : 0)
                            + (b13 != 0 ? (int)((gate.Mask13 >> (b13 - 1)) & 1u) : 0)
                            + (b14 != 0 ? (int)((gate.Mask14 >> (b14 - 1)) & 1u) : 0)
                            + (b15 != 0 ? (int)((gate.Mask15 >> (b15 - 1)) & 1u) : 0);
                    var totalLutHits16 = totalLutHits12 + lutHits12To15;
                    if ((totalLutHits16 * gate.MaxSingleWeight16) + gate.RemainingAfter16 < requiredThreshold)
                    {
                        return true;
                    }

                    if (totalLutHits16 == 0 &&
                        !CanRemainingNonZeroProbesReachThreshold(
                            ref orientBinRef,
                            baseLinearIdx,
                            ref offsetRef,
                            ref weightRef,
                            count,
                            startIndex: 16,
                            gate.RemainingAfter16,
                            requiredThreshold))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        if (gate.UseAtLeastOneAnchorGate)
        {
            var idx0 = baseLinearIdx + gate.Off0;
            var idx1 = baseLinearIdx + gate.Off1;
            var idx2 = baseLinearIdx + gate.Off2;
            var idx3 = baseLinearIdx + gate.Off3;

            var b0 = Unsafe.Add(ref orientBinRef, idx0);
            var b1 = Unsafe.Add(ref orientBinRef, idx1);
            var b2 = Unsafe.Add(ref orientBinRef, idx2);
            var b3 = Unsafe.Add(ref orientBinRef, idx3);
            var lutHits = (b0 != 0 ? (int)((gate.Mask0 >> (b0 - 1)) & 1u) : 0)
                + (b1 != 0 ? (int)((gate.Mask1 >> (b1 - 1)) & 1u) : 0)
                + (b2 != 0 ? (int)((gate.Mask2 >> (b2 - 1)) & 1u) : 0)
                + (b3 != 0 ? (int)((gate.Mask3 >> (b3 - 1)) & 1u) : 0);
            var requiredLutHits = gate.UseStrictTwoAnchorGate ? MinAnchorLutMatches : 1;
            if (lutHits < requiredLutHits)
            {
                return true;
            }

            var dot0 = (gate.C0 * Unsafe.Add(ref unitXRef, idx0)) + (gate.S0 * Unsafe.Add(ref unitYRef, idx0));
            var dot1 = (gate.C1 * Unsafe.Add(ref unitXRef, idx1)) + (gate.S1 * Unsafe.Add(ref unitYRef, idx1));
            var dot2 = (gate.C2 * Unsafe.Add(ref unitXRef, idx2)) + (gate.S2 * Unsafe.Add(ref unitYRef, idx2));
            var dot3 = (gate.C3 * Unsafe.Add(ref unitXRef, idx3)) + (gate.S3 * Unsafe.Add(ref unitYRef, idx3));
            var anchorWeightedSum = (TPolicy.EvaluateScalar(dot0) * gate.W0)
                + (TPolicy.EvaluateScalar(dot1) * gate.W1)
                + (TPolicy.EvaluateScalar(dot2) * gate.W2)
                + (TPolicy.EvaluateScalar(dot3) * gate.W3);
            if (anchorWeightedSum + gate.RemainingAfter4 < requiredThreshold ||
                (gate.UseStrictTwoAnchorGate &&
                 (TPolicy.EvaluateScalar(dot0) + TPolicy.EvaluateScalar(dot1) + TPolicy.EvaluateScalar(dot2) + TPolicy.EvaluateScalar(dot3)) < minAnchorDirectionalSum))
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitGridPeak<TPolicy>(
        in CoarseGridPeak peak,
        int coarseWidth,
        int startX,
        int endX,
        int startY,
        int endY,
        ref float unitXRef,
        ref float unitYRef,
        ref int offsetRef,
        ref float cosRef,
        ref float sinRef,
        ref float weightRef,
        ref float remWeightRef,
        int count,
        float totalWeight,
        float acceptThreshold,
        float requiredWeightedSum,
        float invTotalWeight,
        float scaleToBase,
        float angleRadians,
        float scale,
        float minSeparationSq,
        Span<CoarsePoseCandidate> candidateBuffer,
        ref int foundCount)
        where TPolicy : struct, IPolarityPolicy
    {
        var bestSum = peak.WeightedSum;
        var bestCx = peak.Cx;
        var bestCy = peak.Cy;
        var nyStart = Math.Max(startY, peak.Cy - 1);
        var nyEnd = Math.Min(endY, peak.Cy + 1);
        var nxStart = Math.Max(startX, peak.Cx - 1);
        var nxEnd = Math.Min(endX, peak.Cx + 1);

        for (var ny = nyStart; ny <= nyEnd; ny++)
        {
            var nRow = ny * coarseWidth;
            for (var nx = nxStart; nx <= nxEnd; nx++)
            {
                if (nx == peak.Cx && ny == peak.Cy)
                {
                    continue;
                }

                var s = EvaluatePoseProbes<TPolicy>(
                    ref unitXRef,
                    ref unitYRef,
                    nRow + nx,
                    ref offsetRef,
                    ref cosRef,
                    ref sinRef,
                    ref weightRef,
                    ref remWeightRef,
                    count,
                    totalWeight,
                    acceptThreshold,
                    requiredWeightedSum);

                if (s > bestSum)
                {
                    bestSum = s;
                    bestCx = nx;
                    bestCy = ny;
                }
            }
        }

        if (bestSum >= requiredWeightedSum)
        {
            var candidate = new CoarsePoseCandidate(
                X: (bestCx + 0.5f) * scaleToBase,
                Y: (bestCy + 0.5f) * scaleToBase,
                AngleRadians: angleRadians,
                Scale: scale,
                Score: bestSum * invTotalWeight);

            InsertOrSuppressCandidate(candidateBuffer, ref foundCount, in candidate, minSeparationSq);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AnchorGateState BuildAnchorGateState(
        ref int offsetRef,
        ref float cosRef,
        ref float sinRef,
        ref float weightRef,
        ref float remWeightRef,
        int count,
        float gateThreshold,
        bool ignorePolarity)
    {
        var remainingAfter4 = Unsafe.Add(ref remWeightRef, 4);
        var w0 = Unsafe.Add(ref weightRef, 0);
        var w1 = Unsafe.Add(ref weightRef, 1);
        var w2 = Unsafe.Add(ref weightRef, 2);
        var w3 = Unsafe.Add(ref weightRef, 3);
        var maxSingleAnchorWeight = MathF.Max(MathF.Max(w0, w1), MathF.Max(w2, w3));
        var useStrictTwoAnchorGate = remainingAfter4 + maxSingleAnchorWeight < gateThreshold;
        var useAtLeastOneAnchorGate = remainingAfter4 < gateThreshold;

        var c0 = Unsafe.Add(ref cosRef, 0);
        var s0 = Unsafe.Add(ref sinRef, 0);
        var c1 = Unsafe.Add(ref cosRef, 1);
        var s1 = Unsafe.Add(ref sinRef, 1);
        var c2 = Unsafe.Add(ref cosRef, 2);
        var s2 = Unsafe.Add(ref sinRef, 2);
        var c3 = Unsafe.Add(ref cosRef, 3);
        var s3 = Unsafe.Add(ref sinRef, 3);

        var has8Probes = count >= 8;
        var remainingAfter8 = has8Probes ? Unsafe.Add(ref remWeightRef, 8) : remainingAfter4;
        var maxSingleWeight8 = maxSingleAnchorWeight;
        if (has8Probes)
        {
            var w4 = Unsafe.Add(ref weightRef, 4);
            var w5 = Unsafe.Add(ref weightRef, 5);
            var w6 = Unsafe.Add(ref weightRef, 6);
            var w7 = Unsafe.Add(ref weightRef, 7);
            maxSingleWeight8 = MathF.Max(maxSingleAnchorWeight, MathF.Max(MathF.Max(w4, w5), MathF.Max(w6, w7)));
        }

        var has12Probes = count >= 12;
        var remainingAfter12 = has12Probes ? Unsafe.Add(ref remWeightRef, 12) : remainingAfter8;
        var maxSingleWeight12 = maxSingleWeight8;
        if (has12Probes)
        {
            var w8 = Unsafe.Add(ref weightRef, 8);
            var w9 = Unsafe.Add(ref weightRef, 9);
            var w10 = Unsafe.Add(ref weightRef, 10);
            var w11 = Unsafe.Add(ref weightRef, 11);
            maxSingleWeight12 = MathF.Max(maxSingleWeight8, MathF.Max(MathF.Max(w8, w9), MathF.Max(w10, w11)));
        }

        var has16Probes = count >= 16;
        var remainingAfter16 = has16Probes ? Unsafe.Add(ref remWeightRef, 16) : remainingAfter12;
        var maxSingleWeight16 = maxSingleWeight12;
        if (has16Probes)
        {
            var w12 = Unsafe.Add(ref weightRef, 12);
            var w13 = Unsafe.Add(ref weightRef, 13);
            var w14 = Unsafe.Add(ref weightRef, 14);
            var w15 = Unsafe.Add(ref weightRef, 15);
            maxSingleWeight16 = MathF.Max(maxSingleWeight12, MathF.Max(MathF.Max(w12, w13), MathF.Max(w14, w15)));
        }

        return new AnchorGateState(
            Has4Probes: true,
            Has8Probes: has8Probes,
            Has12Probes: has12Probes,
            Has16Probes: has16Probes,
            UseStrictTwoAnchorGate: useStrictTwoAnchorGate,
            UseAtLeastOneAnchorGate: useAtLeastOneAnchorGate,
            RemainingAfter4: remainingAfter4,
            RemainingAfter8: remainingAfter8,
            RemainingAfter12: remainingAfter12,
            RemainingAfter16: remainingAfter16,
            MaxSingleWeight8: maxSingleWeight8,
            MaxSingleWeight12: maxSingleWeight12,
            MaxSingleWeight16: maxSingleWeight16,
            W0: w0,
            W1: w1,
            W2: w2,
            W3: w3,
            Off0: Unsafe.Add(ref offsetRef, 0),
            Off1: Unsafe.Add(ref offsetRef, 1),
            Off2: Unsafe.Add(ref offsetRef, 2),
            Off3: Unsafe.Add(ref offsetRef, 3),
            Off4: has8Probes ? Unsafe.Add(ref offsetRef, 4) : 0,
            Off5: has8Probes ? Unsafe.Add(ref offsetRef, 5) : 0,
            Off6: has8Probes ? Unsafe.Add(ref offsetRef, 6) : 0,
            Off7: has8Probes ? Unsafe.Add(ref offsetRef, 7) : 0,
            Off8: has12Probes ? Unsafe.Add(ref offsetRef, 8) : 0,
            Off9: has12Probes ? Unsafe.Add(ref offsetRef, 9) : 0,
            Off10: has12Probes ? Unsafe.Add(ref offsetRef, 10) : 0,
            Off11: has12Probes ? Unsafe.Add(ref offsetRef, 11) : 0,
            Off12: has16Probes ? Unsafe.Add(ref offsetRef, 12) : 0,
            Off13: has16Probes ? Unsafe.Add(ref offsetRef, 13) : 0,
            Off14: has16Probes ? Unsafe.Add(ref offsetRef, 14) : 0,
            Off15: has16Probes ? Unsafe.Add(ref offsetRef, 15) : 0,
            C0: c0,
            S0: s0,
            C1: c1,
            S1: s1,
            C2: c2,
            S2: s2,
            C3: c3,
            S3: s3,
            Mask0: RotatedProbeTable.BuildAnchorOrientationMask(c0, s0, ignorePolarity),
            Mask1: RotatedProbeTable.BuildAnchorOrientationMask(c1, s1, ignorePolarity),
            Mask2: RotatedProbeTable.BuildAnchorOrientationMask(c2, s2, ignorePolarity),
            Mask3: RotatedProbeTable.BuildAnchorOrientationMask(c3, s3, ignorePolarity),
            Mask4: has8Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 4), Unsafe.Add(ref sinRef, 4), ignorePolarity) : 0u,
            Mask5: has8Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 5), Unsafe.Add(ref sinRef, 5), ignorePolarity) : 0u,
            Mask6: has8Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 6), Unsafe.Add(ref sinRef, 6), ignorePolarity) : 0u,
            Mask7: has8Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 7), Unsafe.Add(ref sinRef, 7), ignorePolarity) : 0u,
            Mask8: has12Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 8), Unsafe.Add(ref sinRef, 8), ignorePolarity) : 0u,
            Mask9: has12Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 9), Unsafe.Add(ref sinRef, 9), ignorePolarity) : 0u,
            Mask10: has12Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 10), Unsafe.Add(ref sinRef, 10), ignorePolarity) : 0u,
            Mask11: has12Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 11), Unsafe.Add(ref sinRef, 11), ignorePolarity) : 0u,
            Mask12: has16Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 12), Unsafe.Add(ref sinRef, 12), ignorePolarity) : 0u,
            Mask13: has16Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 13), Unsafe.Add(ref sinRef, 13), ignorePolarity) : 0u,
            Mask14: has16Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 14), Unsafe.Add(ref sinRef, 14), ignorePolarity) : 0u,
            Mask15: has16Probes ? RotatedProbeTable.BuildAnchorOrientationMask(Unsafe.Add(ref cosRef, 15), Unsafe.Add(ref sinRef, 15), ignorePolarity) : 0u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertOrSuppressGridPeak(
        Span<CoarseGridPeak> buffer,
        ref int count,
        int cx,
        int cy,
        float weightedSum,
        float minSeparationSq)
    {
        var policy = new CoarseGridPeakSeparationPolicy(minSeparationSq);
        var peak = new CoarseGridPeak(cx, cy, weightedSum);
        BoundedTopK.InsertOrSuppress(buffer, ref count, in peak, in policy);
    }

    private readonly struct CoarseGridPeakSeparationPolicy(float minSeparationSq) : ITopKSeparationPolicy<CoarseGridPeak>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreTooClose(in CoarseGridPeak existing, in CoarseGridPeak candidate)
        {
            var dx = existing.Cx - candidate.Cx;
            var dy = existing.Cy - candidate.Cy;
            return (dx * dx) + (dy * dy) <= minSeparationSq;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsHigherScore(in CoarseGridPeak candidate, in CoarseGridPeak existing)
            => candidate.WeightedSum > existing.WeightedSum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanRemainingNonZeroProbesReachThreshold(
        ref byte orientBinRef,
        int baseLinearIdx,
        ref int offsetRef,
        ref float weightRef,
        int count,
        int startIndex,
        float currentUpperBound,
        float requiredThreshold)
    {
        // [Math Why]: Zero orientation bin implies zero runtime normal vector, yielding a lossless upper-bound subtraction.
        var upperBound = currentUpperBound;
        for (var k = startIndex; k < count; k++)
        {
            if (Unsafe.Add(ref orientBinRef, baseLinearIdx + Unsafe.Add(ref offsetRef, k)) == 0)
            {
                upperBound -= Unsafe.Add(ref weightRef, k);
                if (upperBound < requiredThreshold)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float EvaluatePoseProbes<TPolicy>(
        ref float unitXRef,
        ref float unitYRef,
        int baseLinearIdx,
        ref int offsetRef,
        ref float cosRef,
        ref float sinRef,
        ref float weightRef,
        ref float remWeightRef,
        int count,
        float totalWeight,
        float acceptThreshold,
        float requiredWeightedSum)
        where TPolicy : struct, IPolarityPolicy
    {
        _ = totalWeight;
        _ = acceptThreshold;
        var index = 0;
        var accumulatedSum = 0.0f;
        var earlyExitBlockSize = 1 << EarlyExitCheckBlockShift;

        // [Perf Why]: Gather 8 runtime normal vectors via Avx2.GatherVector256 and accumulate weighted dot products with FMA, checking Cauchy-Schwarz upper bound every 16 probes.
        if (Avx2.IsSupported && count >= Vector256<float>.Count)
        {
            var simdLimit = count & ~(Vector256<float>.Count - 1);
            var vBaseIdx = Vector256.Create(baseLinearIdx);
            var vBlockSum = Vector256<float>.Zero;

            fixed (float* pUnitX = &unitXRef)
            fixed (float* pUnitY = &unitYRef)
            {
                {
                    var vOffsets = Vector256.LoadUnsafe(ref offsetRef) + vBaseIdx;
                    var rCos = Avx2.GatherVector256(pUnitX, vOffsets, sizeof(float));
                    var rSin = Avx2.GatherVector256(pUnitY, vOffsets, sizeof(float));

                    var mCos = Vector256.LoadUnsafe(ref cosRef);
                    var mSin = Vector256.LoadUnsafe(ref sinRef);
                    var w = Vector256.LoadUnsafe(ref weightRef);

                    var dot = Fma.IsSupported
                        ? Fma.MultiplyAdd(mCos, rCos, mSin * rSin)
                        : (mCos * rCos) + (mSin * rSin);

                    var similarity = TPolicy.EvaluateVector256(dot);
                    accumulatedSum = Vector256.Sum(similarity * w);
                    index = Vector256<float>.Count;

                    var remAfter8 = Unsafe.Add(ref remWeightRef, index);
                    if (accumulatedSum + remAfter8 < requiredWeightedSum)
                    {
                        return 0.0f;
                    }
                }

                while (index < simdLimit)
                {
                    var blockEnd = Math.Min(simdLimit, index + earlyExitBlockSize);
                    for (; index < blockEnd; index += Vector256<float>.Count)
                    {
                        var vOffsets = Vector256.LoadUnsafe(ref Unsafe.Add(ref offsetRef, index)) + vBaseIdx;
                        var rCos = Avx2.GatherVector256(pUnitX, vOffsets, sizeof(float));
                        var rSin = Avx2.GatherVector256(pUnitY, vOffsets, sizeof(float));

                        var mCos = Vector256.LoadUnsafe(ref Unsafe.Add(ref cosRef, index));
                        var mSin = Vector256.LoadUnsafe(ref Unsafe.Add(ref sinRef, index));
                        var w = Vector256.LoadUnsafe(ref Unsafe.Add(ref weightRef, index));

                        var dot = Fma.IsSupported
                            ? Fma.MultiplyAdd(mCos, rCos, mSin * rSin)
                            : (mCos * rCos) + (mSin * rSin);

                        var similarity = TPolicy.EvaluateVector256(dot);
                        vBlockSum = Fma.IsSupported
                            ? Fma.MultiplyAdd(similarity, w, vBlockSum)
                            : vBlockSum + (similarity * w);
                    }

                    accumulatedSum += Vector256.Sum(vBlockSum);
                    vBlockSum = Vector256<float>.Zero;

                    var remWeight = Unsafe.Add(ref remWeightRef, index);
                    if (accumulatedSum + remWeight < requiredWeightedSum)
                    {
                        return 0.0f;
                    }
                }
            }
        }

        for (; index < count; index++)
        {
            if ((index & (earlyExitBlockSize - 1)) == 0 &&
                accumulatedSum + Unsafe.Add(ref remWeightRef, index) < requiredWeightedSum)
            {
                return 0.0f;
            }

            var targetIdx = baseLinearIdx + Unsafe.Add(ref offsetRef, index);
            var rCos = Unsafe.Add(ref unitXRef, targetIdx);
            var rSin = Unsafe.Add(ref unitYRef, targetIdx);
            var mCos = Unsafe.Add(ref cosRef, index);
            var mSin = Unsafe.Add(ref sinRef, index);
            var w = Unsafe.Add(ref weightRef, index);

            var dot = (mCos * rCos) + (mSin * rSin);
            accumulatedSum += TPolicy.EvaluateScalar(dot) * w;
        }

        return accumulatedSum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertOrSuppressCandidate(
        Span<CoarsePoseCandidate> buffer,
        ref int count,
        in CoarsePoseCandidate candidate,
        float minSeparationSq)
    {
        var policy = new CoarsePoseCandidateSeparationPolicy(minSeparationSq);
        BoundedTopK.InsertOrSuppress(buffer, ref count, in candidate, in policy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float WrapAnglePi(float angle)
        => AngleMath.WrapRadiansPi(angle);

    private readonly struct CoarsePoseCandidateSeparationPolicy(float minSeparationSq) : ITopKSeparationPolicy<CoarsePoseCandidate>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AreTooClose(in CoarsePoseCandidate existing, in CoarsePoseCandidate candidate)
        {
            var dx = existing.X - candidate.X;
            var dy = existing.Y - candidate.Y;
            var dAngle = MathF.Abs(AngleMath.WrapRadiansPi(existing.AngleRadians - candidate.AngleRadians));
            return (dx * dx) + (dy * dy) <= minSeparationSq && dAngle <= MinAngleSeparationRadians;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsHigherScore(in CoarsePoseCandidate candidate, in CoarsePoseCandidate existing)
            => candidate.Score > existing.Score;
    }
}
