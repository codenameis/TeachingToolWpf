// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxFineAligner.cs
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// 4-Step Weighted Least-Squares (WLS) Attraction fine alignment engine (US 7,065,262).
/// Projects runtime dipoles into the pre-generated attraction field and solves the 4-DOF similarity linear system
/// (dU, dV, dTheta, dScale) while computing Coverage and Clutter.
/// </summary>
public static class PatMaxFineAligner
{
    public const int PreferredAttractionSteps = 6;
    internal const int ScratchArraysPerDipole = 6;
    private const int LinearSystemDimension = 4;
    private const int HuberStepCount = 2;
    private const float ParameterDeltaConvergence = 1e-4f;
    private const float FixedScaleRangeTolerance = 1e-4f;
    // [Math Why]: Tikhonov diagonal regularization prevents singular matrix inversion when scale observability is weak.
    private const float DiagonalRegularization = 1e-3f;
    private const float PivotSingularityEpsilon = 1e-7f;
    private const float MaxStepTranslationPixels = 4.0f;
    private const float MaxStepAngleRadians = 0.25f;
    private const float MaxStepScaleDelta = 0.15f;
    private const float MinAllowedScale = 0.25f;
    private const float MaxAllowedScale = 4.0f;
    private const int SpatialSectorGridDimension = 4;
    private const int SpatialSectorGridCount = SpatialSectorGridDimension * SpatialSectorGridDimension;
    private const float MissingSectorCoverageThreshold = 0.20f;
    private const float MissingSectorNonlinearPenaltyGain = 1.65f;
    private const float MaxMissingSectorPenalty = 0.36f;
    private const float ClutterSafeZoneElasticityMultiplier = 1.5f;
    private const float ClutterPenaltyFactor = 0.40f;
    private const float WeightFloorEpsilon = 1e-6f;
    private const float MinHuberDeltaPixels = 0.5f;
    private const float HuberElasticityRatio = 0.5f;
    private const float MinNormalPolarityAgreement = 0.25f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static PatMaxResult Align(
        PatMaxModel model,
        EdgeDipoleSoA runtimeFineDipoles,
        in CoarsePoseCandidate candidate,
        in PatMaxSearchParams searchParams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(runtimeFineDipoles);
        if (!model.IsTrained || runtimeFineDipoles.IsEmpty || !candidate.IsValid)
        {
            return PatMaxResult.Empty;
        }

        return searchParams.IgnorePolarity
            ? AlignWithPolicy<IgnorePolarityPolicy>(model, runtimeFineDipoles, in candidate, in searchParams, cancellationToken)
            : AlignWithPolicy<SignedPolarityPolicy>(model, runtimeFineDipoles, in candidate, in searchParams, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static PatMaxResult AlignWithPolicy<TPolicy>(
        PatMaxModel model,
        EdgeDipoleSoA runtimeFineDipoles,
        in CoarsePoseCandidate candidate,
        in PatMaxSearchParams searchParams,
        CancellationToken cancellationToken = default)
        where TPolicy : struct, IPolarityPolicy
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelDipoles = model.FineLevel.Dipoles;
        var modelCount = modelDipoles.Count;
        if (modelCount == 0 || runtimeFineDipoles.IsEmpty)
        {
            return PatMaxResult.Empty;
        }

        var floatScratchLen = checked(modelCount * ScratchArraysPerDipole);
        var floatPool = ArrayPool<float>.Shared.Rent(floatScratchLen);

        try
        {
            return AlignWithScratch<TPolicy>(
                model,
                runtimeFineDipoles,
                in candidate,
                in searchParams,
                floatPool.AsSpan(0, floatScratchLen),
                cancellationToken);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(floatPool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static PatMaxResult AlignWithScratch(
        PatMaxModel model,
        EdgeDipoleSoA runtimeFineDipoles,
        in CoarsePoseCandidate candidate,
        in PatMaxSearchParams searchParams,
        Span<float> scratch,
        CancellationToken cancellationToken = default)
        => searchParams.IgnorePolarity
            ? AlignWithScratch<IgnorePolarityPolicy>(model, runtimeFineDipoles, in candidate, in searchParams, scratch, cancellationToken)
            : AlignWithScratch<SignedPolarityPolicy>(model, runtimeFineDipoles, in candidate, in searchParams, scratch, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static PatMaxResult AlignWithScratch<TPolicy>(
        PatMaxModel model,
        EdgeDipoleSoA runtimeFineDipoles,
        in CoarsePoseCandidate candidate,
        in PatMaxSearchParams searchParams,
        Span<float> scratch,
        CancellationToken cancellationToken = default)
        where TPolicy : struct, IPolarityPolicy
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modelDipoles = model.FineLevel.Dipoles;
        var modelCount = modelDipoles.Count;
        if (modelCount == 0 || runtimeFineDipoles.IsEmpty)
        {
            return PatMaxResult.Empty;
        }

        var minScale = Math.Clamp(searchParams.ScaleMin, MinAllowedScale, MaxAllowedScale);
        var maxScale = Math.Clamp(MathF.Max(minScale, searchParams.ScaleMax), MinAllowedScale, MaxAllowedScale);
        var isFixedScale = MathF.Abs(maxScale - minScale) <= FixedScaleRangeTolerance;

        var poseX = candidate.X;
        var poseY = candidate.Y;
        var angle = candidate.AngleRadians;
        var scale = Math.Clamp(candidate.Scale, minScale, maxScale);
        var aspect = 1.0f;
        var elasticity = MathF.Max(0.25f, model.TrainParams.ElasticityPixels);
        // [Math Why]: In Huber steps (0~1), allow capture radius up to MinCaptureBasinPixels (2.5 px) so Coarse grid quantization offset
        // does not reject valid boundary correspondences before pose convergence; Tukey steps (2~5) and final scoring enforce strict user elasticity.
        var initialCaptureElasticity = MathF.Max(elasticity, AttractionFieldBuilder.MinCaptureBasinPixels);

        var jacU = scratch.Slice(0, modelCount);
        var jacV = scratch.Slice(modelCount, modelCount);
        var jacRot = scratch.Slice(modelCount * 2, modelCount);
        var jacScale = scratch.Slice(modelCount * 3, modelCount);
        var weightedRes = scratch.Slice(modelCount * 4, modelCount);
        var bestModelScoreOrWeight = scratch.Slice(modelCount * 5, modelCount);

        var iterations = 0;
        for (var step = 0; step < PreferredAttractionSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations = step + 1;
            var stepElasticity = step < HuberStepCount ? initialCaptureElasticity : elasticity;
            var stepElasticitySq = stepElasticity * stepElasticity;
            var activeCount = BuildAttractionCorrespondences<TPolicy>(
                model,
                runtimeFineDipoles,
                poseX,
                poseY,
                angle,
                scale,
                aspect,
                stepElasticity,
                stepElasticitySq,
                step,
                jacU,
                jacV,
                jacRot,
                jacScale,
                weightedRes,
                bestModelScoreOrWeight);

            if (activeCount < LinearSystemDimension)
            {
                break;
            }

            if (!Solve4DofNormalEquationsSimd(
                jacU,
                jacV,
                jacRot,
                jacScale,
                weightedRes,
                activeCount,
                out var dU,
                out var dV,
                out var dTheta,
                out var dScale))
            {
                break;
            }

            dU = Math.Clamp(dU, -MaxStepTranslationPixels, MaxStepTranslationPixels);
            dV = Math.Clamp(dV, -MaxStepTranslationPixels, MaxStepTranslationPixels);
            dTheta = Math.Clamp(dTheta, -MaxStepAngleRadians, MaxStepAngleRadians);
            dScale = isFixedScale ? 0.0f : Math.Clamp(dScale, -MaxStepScaleDelta, MaxStepScaleDelta);

            var (sin, cos) = MathF.SinCos(angle);
            poseX -= ((dU * cos) - (dV * sin)) * scale;
            poseY -= ((dU * sin) + (dV * cos)) * scale;
            angle -= dTheta;
            scale = isFixedScale
                ? minScale
                : Math.Clamp(scale * (1.0f - dScale), minScale, maxScale);

            if ((dU * dU) + (dV * dV) <= ParameterDeltaConvergence * ParameterDeltaConvergence &&
                MathF.Abs(dTheta) <= ParameterDeltaConvergence &&
                MathF.Abs(dScale) <= ParameterDeltaConvergence)
            {
                break;
            }
        }

        return EvaluateFinalCoverageAndClutter<TPolicy>(
            model,
            runtimeFineDipoles,
            poseX,
            poseY,
            angle,
            scale,
            aspect,
            elasticity,
            iterations,
            bestModelScoreOrWeight);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int BuildAttractionCorrespondences<TPolicy>(
        PatMaxModel model,
        EdgeDipoleSoA runtimeDipoles,
        float poseX,
        float poseY,
        float angle,
        float scale,
        float aspect,
        float elasticity,
        float elasticitySq,
        int step,
        Span<float> jacU,
        Span<float> jacV,
        Span<float> jacRot,
        Span<float> jacScale,
        Span<float> weightedRes,
        Span<float> bestModelWeights)
        where TPolicy : struct, IPolarityPolicy
    {
        bestModelWeights.Clear();
        var field = model.FineAttractionField;
        var modelDipoles = model.FineLevel.Dipoles;
        var mX = modelDipoles.X;
        var mY = modelDipoles.Y;
        var mCos = modelDipoles.CosTheta;
        var mSin = modelDipoles.SinTheta;

        var rX = runtimeDipoles.X;
        var rY = runtimeDipoles.Y;
        var rCos = runtimeDipoles.CosTheta;
        var rSin = runtimeDipoles.SinTheta;
        var rMag = runtimeDipoles.Magnitude;

        var (sin, cos) = MathF.SinCos(angle);
        var scaleX = MathF.Max(MinAllowedScale, scale);
        var scaleY = MathF.Max(MinAllowedScale, scale * aspect);
        var invScaleX = 1.0f / scaleX;
        var invScaleY = 1.0f / scaleY;
        var invElasticity = 1.0f / elasticity;
        var invElasticitySq = 1.0f / elasticitySq;
        var huberDelta = MathF.Max(MinHuberDeltaPixels, elasticity * HuberElasticityRatio);
        var useTukeyBiweight = step >= HuberStepCount;

        ComputeWorldBoundingBox(
            model.MinDx - elasticity,
            model.MaxDx + elasticity,
            model.MinDy - elasticity,
            model.MaxDy + elasticity,
            poseX,
            poseY,
            cos,
            sin,
            scaleX,
            scaleY,
            out var minWorldX,
            out var maxWorldX,
            out var minWorldY,
            out var maxWorldY);

        var modelCount = jacU.Length;
        var runtimeCount = runtimeDipoles.Count;
        var fOriginX = field.OriginX;
        var fOriginY = field.OriginY;
        var fInvCellSize = field.InvCellSize;
        var fWidth = field.Width;
        var fHeight = field.Height;
        var fStride = field.Stride;
        var fCells = field.Cells;

        for (var i = 0; i < runtimeCount; i++)
        {
            var wx = rX[i];
            var wy = rY[i];
            if (wx < minWorldX || wx > maxWorldX || wy < minWorldY || wy > maxWorldY)
            {
                continue;
            }

            var dx = wx - poseX;
            var dy = wy - poseY;

            // Inverse transform runtime dipole into continuous model coordinate frame T(p)^-1 * x_r
            var modelSpaceX = ((dx * cos) + (dy * sin)) * invScaleX;
            var modelSpaceY = ((-dx * sin) + (dy * cos)) * invScaleY;

            if (!TrySampleExactVoronoiDipole(
                fCells,
                fWidth,
                fHeight,
                fStride,
                fOriginX,
                fOriginY,
                fInvCellSize,
                modelSpaceX,
                modelSpaceY,
                mX,
                mY,
                out var dipIdx,
                out var targetX,
                out var targetY,
                out var errX,
                out var errY,
                out var distSq))
            {
                continue;
            }

            // Cut off outliers beyond Elasticity perimeter tolerance
            if (distSq > elasticitySq)
            {
                continue;
            }

            // [Math Why]: Exact sub-pixel model normal (nx, ny) directly from Voronoi dipole index eliminates grid-cell quantization error.
            var nx = mCos[dipIdx];
            var ny = mSin[dipIdx];

            // Rotate runtime normal into model coordinate frame
            var rnx = (rCos[i] * cos) + (rSin[i] * sin);
            var rny = (-rCos[i] * sin) + (rSin[i] * cos);
            var dot = (rnx * nx) + (rny * ny);
            var polaritySim = TPolicy.EvaluateScalar(dot);
            if (polaritySim <= MinNormalPolarityAgreement)
            {
                continue;
            }

            // [Math Why]:Squared normal alignment (n_m . R(theta)n_r)^2 suppresses cross-edge interference at corners.
            var normalAgreementSq = polaritySim * polaritySim;

            // Analytic normal residual r_i = n_mx * (x'_r - x_m) + n_my * (y'_r - y_m)
            var normalResidual = (errX * nx) + (errY * ny);
            var absResidual = MathF.Abs(normalResidual);

            float mEstimatorWeight;
            if (!useTukeyBiweight)
            {
                // Steps 1~2: Huber M-Estimator for wide convex basin of convergence
                mEstimatorWeight = absResidual <= huberDelta ? 1.0f : huberDelta / absResidual;
            }
            else
            {
                // Steps 3~6: Tukey Biweight M-Estimator completely zeroes out outlier particle/noise pull
                if (absResidual >= elasticity)
                {
                    continue;
                }

                var u = absResidual * invElasticity;
                var oneMinusUSq = 1.0f - (u * u);
                mEstimatorWeight = oneMinusUSq * oneMinusUSq;
            }

            var springWeight = mEstimatorWeight * (1.0f - (distSq * invElasticitySq)) * normalAgreementSq * rMag[i];
            if (springWeight <= bestModelWeights[dipIdx] || springWeight <= WeightFloorEpsilon)
            {
                continue;
            }

            bestModelWeights[dipIdx] = springWeight;
            var sqrtW = MathF.Sqrt(springWeight);

            // Store 1-to-1 best Jacobian row per model dipole index so bottom edges are never truncated
            jacU[dipIdx] = sqrtW * nx;
            jacV[dipIdx] = sqrtW * ny;
            jacRot[dipIdx] = sqrtW * ((-targetY * nx) + (targetX * ny));
            jacScale[dipIdx] = sqrtW * ((targetX * nx) + (targetY * ny));
            weightedRes[dipIdx] = -sqrtW * normalResidual;
        }

        // Compact active correspondences into [0 .. activeCount - 1] for contiguous AVX2 normal equation accumulation
        var activeCount = 0;
        for (var m = 0; m < modelCount; m++)
        {
            if (bestModelWeights[m] <= WeightFloorEpsilon)
            {
                continue;
            }

            if (activeCount != m)
            {
                jacU[activeCount] = jacU[m];
                jacV[activeCount] = jacV[m];
                jacRot[activeCount] = jacRot[m];
                jacScale[activeCount] = jacScale[m];
                weightedRes[activeCount] = weightedRes[m];
            }

            activeCount++;
        }

        return activeCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TrySampleExactVoronoiDipole(
        ReadOnlySpan<AttractionCell> cells,
        int width,
        int height,
        int stride,
        float originX,
        float originY,
        float invCellSize,
        float modelX,
        float modelY,
        ReadOnlySpan<float> mX,
        ReadOnlySpan<float> mY,
        out int bestDipIdx,
        out float targetX,
        out float targetY,
        out float errX,
        out float errY,
        out float distSq)
    {
        var gridX = (int)MathF.Round((modelX - originX) * invCellSize);
        var gridY = (int)MathF.Round((modelY - originY) * invCellSize);
        if ((uint)gridX >= (uint)width || (uint)gridY >= (uint)height)
        {
            bestDipIdx = -1;
            targetX = 0f;
            targetY = 0f;
            errX = 0f;
            errY = 0f;
            distSq = float.PositiveInfinity;
            return false;
        }

        var centerIdx = (gridY * stride) + gridX;
        var centerCell = cells[centerIdx];
        if (!centerCell.HasNearestDipole)
        {
            bestDipIdx = -1;
            targetX = 0f;
            targetY = 0f;
            errX = 0f;
            errY = 0f;
            distSq = float.PositiveInfinity;
            return false;
        }

        bestDipIdx = centerCell.NearestDipoleIndex;
        targetX = mX[bestDipIdx];
        targetY = mY[bestDipIdx];
        errX = modelX - targetX;
        errY = modelY - targetY;
        distSq = (errX * errX) + (errY * errY);

        // Check 4-connected neighbor Voronoi cells to eliminate cell-boundary quantization artifacts
        if (gridX > 0)
        {
            CheckVoronoiCandidate(in cells[centerIdx - 1], modelX, modelY, mX, mY, ref bestDipIdx, ref targetX, ref targetY, ref errX, ref errY, ref distSq);
        }

        if (gridX + 1 < width)
        {
            CheckVoronoiCandidate(in cells[centerIdx + 1], modelX, modelY, mX, mY, ref bestDipIdx, ref targetX, ref targetY, ref errX, ref errY, ref distSq);
        }

        if (gridY > 0)
        {
            CheckVoronoiCandidate(in cells[centerIdx - stride], modelX, modelY, mX, mY, ref bestDipIdx, ref targetX, ref targetY, ref errX, ref errY, ref distSq);
        }

        if (gridY + 1 < height)
        {
            CheckVoronoiCandidate(in cells[centerIdx + stride], modelX, modelY, mX, mY, ref bestDipIdx, ref targetX, ref targetY, ref errX, ref errY, ref distSq);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CheckVoronoiCandidate(
        in AttractionCell neighborCell,
        float modelX,
        float modelY,
        ReadOnlySpan<float> mX,
        ReadOnlySpan<float> mY,
        ref int bestDipIdx,
        ref float targetX,
        ref float targetY,
        ref float errX,
        ref float errY,
        ref float distSq)
    {
        var candIdx = neighborCell.NearestDipoleIndex;
        if (candIdx < 0 || candIdx == bestDipIdx)
        {
            return;
        }

        var tx = mX[candIdx];
        var ty = mY[candIdx];
        var ex = modelX - tx;
        var ey = modelY - ty;
        var dSq = (ex * ex) + (ey * ey);
        if (dSq < distSq)
        {
            bestDipIdx = candIdx;
            targetX = tx;
            targetY = ty;
            errX = ex;
            errY = ey;
            distSq = dSq;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputeWorldBoundingBox(
        float minLocalX,
        float maxLocalX,
        float minLocalY,
        float maxLocalY,
        float poseX,
        float poseY,
        float cos,
        float sin,
        float scaleX,
        float scaleY,
        out float minWorldX,
        out float maxWorldX,
        out float minWorldY,
        out float maxWorldY)
    {
        var sx0 = minLocalX * scaleX;
        var sx1 = maxLocalX * scaleX;
        var sy0 = minLocalY * scaleY;
        var sy1 = maxLocalY * scaleY;

        var x00 = (sx0 * cos) - (sy0 * sin);
        var y00 = (sx0 * sin) + (sy0 * cos);
        var x10 = (sx1 * cos) - (sy0 * sin);
        var y10 = (sx1 * sin) + (sy0 * cos);
        var x01 = (sx0 * cos) - (sy1 * sin);
        var y01 = (sx0 * sin) + (sy1 * cos);
        var x11 = (sx1 * cos) - (sy1 * sin);
        var y11 = (sx1 * sin) + (sy1 * cos);

        minWorldX = poseX + MathF.Min(MathF.Min(x00, x10), MathF.Min(x01, x11));
        maxWorldX = poseX + MathF.Max(MathF.Max(x00, x10), MathF.Max(x01, x11));
        minWorldY = poseY + MathF.Min(MathF.Min(y00, y10), MathF.Min(y01, y11));
        maxWorldY = poseY + MathF.Max(MathF.Max(y00, y10), MathF.Max(y01, y11));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Solve4DofNormalEquationsSimd(
        ReadOnlySpan<float> j0,
        ReadOnlySpan<float> j1,
        ReadOnlySpan<float> j2,
        ReadOnlySpan<float> j3,
        ReadOnlySpan<float> rhsVec,
        int count,
        out float dU,
        out float dV,
        out float dTheta,
        out float dScale)
    {
        dU = 0f;
        dV = 0f;
        dTheta = 0f;
        dScale = 0f;

        ref var r0 = ref MemoryMarshal.GetReference(j0);
        ref var r1 = ref MemoryMarshal.GetReference(j1);
        ref var r2 = ref MemoryMarshal.GetReference(j2);
        ref var r3 = ref MemoryMarshal.GetReference(j3);
        ref var rb = ref MemoryMarshal.GetReference(rhsVec);

        float a00 = DiagonalRegularization, a01 = 0f, a02 = 0f, a03 = 0f;
        float a11 = DiagonalRegularization, a12 = 0f, a13 = 0f;
        float a22 = DiagonalRegularization, a23 = 0f;
        float a33 = DiagonalRegularization;
        float b0 = 0f, b1 = 0f, b2 = 0f, b3 = 0f;

        var i = 0;
        // [Perf Why]: Accumulate 14 normal-equation matrix/vector products simultaneously across 8 correspondences per cycle using AVX2 FMA.
        if (Avx2.IsSupported && count >= Vector256<float>.Count)
        {
            var simdLimit = count & ~(Vector256<float>.Count - 1);
            var v00 = Vector256<float>.Zero;
            var v01 = Vector256<float>.Zero;
            var v02 = Vector256<float>.Zero;
            var v03 = Vector256<float>.Zero;
            var v11 = Vector256<float>.Zero;
            var v12 = Vector256<float>.Zero;
            var v13 = Vector256<float>.Zero;
            var v22 = Vector256<float>.Zero;
            var v23 = Vector256<float>.Zero;
            var v33 = Vector256<float>.Zero;
            var vb0 = Vector256<float>.Zero;
            var vb1 = Vector256<float>.Zero;
            var vb2 = Vector256<float>.Zero;
            var vb3 = Vector256<float>.Zero;

            for (; i < simdLimit; i += Vector256<float>.Count)
            {
                var x0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref r0, i));
                var x1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref r1, i));
                var x2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref r2, i));
                var x3 = Vector256.LoadUnsafe(ref Unsafe.Add(ref r3, i));
                var y = Vector256.LoadUnsafe(ref Unsafe.Add(ref rb, i));

                if (Fma.IsSupported)
                {
                    v00 = Fma.MultiplyAdd(x0, x0, v00);
                    v01 = Fma.MultiplyAdd(x0, x1, v01);
                    v02 = Fma.MultiplyAdd(x0, x2, v02);
                    v03 = Fma.MultiplyAdd(x0, x3, v03);
                    v11 = Fma.MultiplyAdd(x1, x1, v11);
                    v12 = Fma.MultiplyAdd(x1, x2, v12);
                    v13 = Fma.MultiplyAdd(x1, x3, v13);
                    v22 = Fma.MultiplyAdd(x2, x2, v22);
                    v23 = Fma.MultiplyAdd(x2, x3, v23);
                    v33 = Fma.MultiplyAdd(x3, x3, v33);

                    vb0 = Fma.MultiplyAdd(x0, y, vb0);
                    vb1 = Fma.MultiplyAdd(x1, y, vb1);
                    vb2 = Fma.MultiplyAdd(x2, y, vb2);
                    vb3 = Fma.MultiplyAdd(x3, y, vb3);
                }
                else
                {
                    v00 += x0 * x0;
                    v01 += x0 * x1;
                    v02 += x0 * x2;
                    v03 += x0 * x3;
                    v11 += x1 * x1;
                    v12 += x1 * x2;
                    v13 += x1 * x3;
                    v22 += x2 * x2;
                    v23 += x2 * x3;
                    v33 += x3 * x3;

                    vb0 += x0 * y;
                    vb1 += x1 * y;
                    vb2 += x2 * y;
                    vb3 += x3 * y;
                }
            }

            a00 += Vector256.Sum(v00);
            a01 += Vector256.Sum(v01);
            a02 += Vector256.Sum(v02);
            a03 += Vector256.Sum(v03);
            a11 += Vector256.Sum(v11);
            a12 += Vector256.Sum(v12);
            a13 += Vector256.Sum(v13);
            a22 += Vector256.Sum(v22);
            a23 += Vector256.Sum(v23);
            a33 += Vector256.Sum(v33);

            b0 += Vector256.Sum(vb0);
            b1 += Vector256.Sum(vb1);
            b2 += Vector256.Sum(vb2);
            b3 += Vector256.Sum(vb3);
        }

        for (; i < count; i++)
        {
            var x0 = Unsafe.Add(ref r0, i);
            var x1 = Unsafe.Add(ref r1, i);
            var x2 = Unsafe.Add(ref r2, i);
            var x3 = Unsafe.Add(ref r3, i);
            var y = Unsafe.Add(ref rb, i);

            a00 += x0 * x0;
            a01 += x0 * x1;
            a02 += x0 * x2;
            a03 += x0 * x3;
            a11 += x1 * x1;
            a12 += x1 * x2;
            a13 += x1 * x3;
            a22 += x2 * x2;
            a23 += x2 * x3;
            a33 += x3 * x3;

            b0 += x0 * y;
            b1 += x1 * y;
            b2 += x2 * y;
            b3 += x3 * y;
        }

        Span<float> mat = stackalloc float[16]
        {
            a00, a01, a02, a03,
            a01, a11, a12, a13,
            a02, a12, a22, a23,
            a03, a13, a23, a33
        };
        Span<float> rhs = stackalloc float[4] { b0, b1, b2, b3 };

        if (!SolveGaussian4x4(mat, rhs))
        {
            return false;
        }

        dU = rhs[0];
        dV = rhs[1];
        dTheta = rhs[2];
        dScale = rhs[3];
        return true;
    }

    private static bool SolveGaussian4x4(Span<float> a, Span<float> b)
    {
        const int n = 4;
        for (var col = 0; col < n; col++)
        {
            var pivotRow = col;
            var maxVal = MathF.Abs(a[(col * n) + col]);
            for (var row = col + 1; row < n; row++)
            {
                var val = MathF.Abs(a[(row * n) + col]);
                if (val > maxVal)
                {
                    maxVal = val;
                    pivotRow = row;
                }
            }

            if (maxVal < PivotSingularityEpsilon)
            {
                return false;
            }

            if (pivotRow != col)
            {
                for (var k = col; k < n; k++)
                {
                    (a[(col * n) + k], a[(pivotRow * n) + k]) = (a[(pivotRow * n) + k], a[(col * n) + k]);
                }

                (b[col], b[pivotRow]) = (b[pivotRow], b[col]);
            }

            var invPivot = 1.0f / a[(col * n) + col];
            for (var row = col + 1; row < n; row++)
            {
                var factor = a[(row * n) + col] * invPivot;
                a[(row * n) + col] = 0.0f;
                for (var k = col + 1; k < n; k++)
                {
                    a[(row * n) + k] -= factor * a[(col * n) + k];
                }

                b[row] -= factor * b[col];
            }
        }

        for (var row = n - 1; row >= 0; row--)
        {
            var sum = b[row];
            for (var k = row + 1; k < n; k++)
            {
                sum -= a[(row * n) + k] * b[k];
            }

            b[row] = sum / a[(row * n) + row];
        }

        return float.IsFinite(b[0]) && float.IsFinite(b[1]) && float.IsFinite(b[2]) && float.IsFinite(b[3]);
    }

    private static PatMaxResult EvaluateFinalCoverageAndClutter<TPolicy>(
        PatMaxModel model,
        EdgeDipoleSoA runtimeDipoles,
        float poseX,
        float poseY,
        float angle,
        float scale,
        float aspect,
        float elasticity,
        int iterations,
        Span<float> bestModelContribution)
        where TPolicy : struct, IPolarityPolicy
    {
        bestModelContribution.Clear();
        var field = model.FineAttractionField;
        var modelLevel = model.FineLevel;
        var modelDipoles = modelLevel.Dipoles;
        var mX = modelDipoles.X;
        var mY = modelDipoles.Y;
        var mMag = modelDipoles.Magnitude;

        var rX = runtimeDipoles.X;
        var rY = runtimeDipoles.Y;
        var rCos = runtimeDipoles.CosTheta;
        var rSin = runtimeDipoles.SinTheta;
        var rMag = runtimeDipoles.Magnitude;

        var (sin, cos) = MathF.SinCos(angle);
        var scaleX = MathF.Max(MinAllowedScale, scale);
        var scaleY = MathF.Max(MinAllowedScale, scale * aspect);
        var invScaleX = 1.0f / scaleX;
        var invScaleY = 1.0f / scaleY;
        var elasticitySq = elasticity * elasticity;

        var minLocalX = model.MinDx - elasticity;
        var maxLocalX = model.MaxDx + elasticity;
        var minLocalY = model.MinDy - elasticity;
        var maxLocalY = model.MaxDy + elasticity;

        ComputeWorldBoundingBox(
            minLocalX,
            maxLocalX,
            minLocalY,
            maxLocalY,
            poseX,
            poseY,
            cos,
            sin,
            scaleX,
            scaleY,
            out var minWorldX,
            out var maxWorldX,
            out var minWorldY,
            out var maxWorldY);

        var runtimeInBoxCount = 0;
        var clutterOutsideSafeRadiusCount = 0;
        var safeClutterRadius = elasticity * ClutterSafeZoneElasticityMultiplier;
        var safeClutterRadiusSq = safeClutterRadius * safeClutterRadius;

        var fOriginX = field.OriginX;
        var fOriginY = field.OriginY;
        var fInvCellSize = field.InvCellSize;
        var fWidth = field.Width;
        var fHeight = field.Height;
        var fStride = field.Stride;
        var fCells = field.Cells;

        for (var i = 0; i < runtimeDipoles.Count; i++)
        {
            var wx = rX[i];
            var wy = rY[i];
            if (wx < minWorldX || wx > maxWorldX || wy < minWorldY || wy > maxWorldY)
            {
                continue;
            }

            var dx = wx - poseX;
            var dy = wy - poseY;
            var mx = ((dx * cos) + (dy * sin)) * invScaleX;
            var my = ((-dx * sin) + (dy * cos)) * invScaleY;

            if (mx < minLocalX || mx > maxLocalX || my < minLocalY || my > maxLocalY)
            {
                continue;
            }

            runtimeInBoxCount++;

            var cX = (int)MathF.Round((mx - fOriginX) * fInvCellSize);
            var cY = (int)MathF.Round((my - fOriginY) * fInvCellSize);
            var matchedAny = false;
            var minDistSqToAnyModelDipole = float.PositiveInfinity;

            var nStartY = Math.Max(0, cY - 1);
            var nEndY = Math.Min(fHeight - 1, cY + 1);
            var nStartX = Math.Max(0, cX - 1);
            var nEndX = Math.Min(fWidth - 1, cX + 1);

            var rnx = (rCos[i] * cos) + (rSin[i] * sin);
            var rny = (-rCos[i] * sin) + (rSin[i] * cos);

            for (var nyIdx = nStartY; nyIdx <= nEndY; nyIdx++)
            {
                var rowBase = nyIdx * fStride;
                for (var nxIdx = nStartX; nxIdx <= nEndX; nxIdx++)
                {
                    var cell = fCells[rowBase + nxIdx];
                    if (!cell.HasNearestDipole)
                    {
                        continue;
                    }

                    var dipIdx = cell.NearestDipoleIndex;
                    var ex = mx - mX[dipIdx];
                    var ey = my - mY[dipIdx];
                    var distSq = (ex * ex) + (ey * ey);
                    if (distSq < minDistSqToAnyModelDipole)
                    {
                        minDistSqToAnyModelDipole = distSq;
                    }

                    if (distSq <= elasticitySq)
                    {
                        var dot = (rnx * cell.NormalX) + (rny * cell.NormalY);
                        var sim = TPolicy.EvaluateScalar(dot);
                        if (sim > 0.2f)
                        {
                            matchedAny = true;
                            // [Math Why]: Project error onto edge normal so sub-pixel tangential sampling difference along straight contours does not penalize Coverage.
                            var normalErr = (ex * cell.NormalX) + (ey * cell.NormalY);
                            var normalErrSq = normalErr * normalErr;
                            var distFactor = 1.0f - (0.5f * Math.Clamp(normalErrSq / elasticitySq, 0.0f, 1.0f));
                            var contribution = mMag[dipIdx] * sim * distFactor;
                            if (contribution > bestModelContribution[dipIdx])
                            {
                                bestModelContribution[dipIdx] = contribution;
                            }
                        }
                    }
                }
            }

            // Rule 3: Count runtime surplus edge as Clutter only when it lies in empty space beyond (Elasticity * 1.5)
            if (!matchedAny && minDistSqToAnyModelDipole > safeClutterRadiusSq)
            {
                clutterOutsideSafeRadiusCount++;
            }
        }

        var matchedDipoleCount = 0;
        var matchedWeightedScoreSum = 0.0f;
        var spanX = MathF.Max(1.0f, model.MaxDx - model.MinDx);
        var spanY = MathF.Max(1.0f, model.MaxDy - model.MinDy);
        var invSectorSpanX = SpatialSectorGridDimension / (spanX + 1e-3f);
        var invSectorSpanY = SpatialSectorGridDimension / (spanY + 1e-3f);

        Span<float> sectorTotalWeight = stackalloc float[SpatialSectorGridCount];
        Span<float> sectorMatchedWeight = stackalloc float[SpatialSectorGridCount];
        sectorTotalWeight.Clear();
        sectorMatchedWeight.Clear();

        for (var m = 0; m < bestModelContribution.Length; m++)
        {
            var gx = Math.Clamp((int)((mX[m] - model.MinDx) * invSectorSpanX), 0, SpatialSectorGridDimension - 1);
            var gy = Math.Clamp((int)((mY[m] - model.MinDy) * invSectorSpanY), 0, SpatialSectorGridDimension - 1);
            var sectorIdx = (gy * SpatialSectorGridDimension) + gx;
            var modelW = mMag[m];
            sectorTotalWeight[sectorIdx] += modelW;

            var c = bestModelContribution[m];
            if (c > 0.0f)
            {
                matchedDipoleCount++;
                matchedWeightedScoreSum += c;
                sectorMatchedWeight[sectorIdx] += c;
            }
        }

        var totalModelWeight = MathF.Max(WeightFloorEpsilon, modelLevel.TotalMagnitudeWeight);
        var coverage = Math.Clamp(matchedWeightedScoreSum / totalModelWeight, 0.0f, 1.0f);

        // Rule 2: Missing Quadrant / Sector nonlinear penalty when a valid model spatial cell has < 20% runtime coverage
        var minValidSectorWeight = totalModelWeight * 0.015f;
        var missingSectorWeightSum = 0.0f;
        for (var s = 0; s < SpatialSectorGridCount; s++)
        {
            var secTotal = sectorTotalWeight[s];
            if (secTotal < minValidSectorWeight)
            {
                continue;
            }

            var secCov = secTotal > WeightFloorEpsilon ? sectorMatchedWeight[s] / secTotal : 0.0f;
            if (secCov < MissingSectorCoverageThreshold)
            {
                var deficitRatio = (MissingSectorCoverageThreshold - secCov) / MissingSectorCoverageThreshold;
                missingSectorWeightSum += secTotal * (deficitRatio * deficitRatio);
            }
        }

        var missingSectorRatio = Math.Clamp(missingSectorWeightSum / totalModelWeight, 0.0f, 1.0f);
        var missingSectorPenalty = Math.Clamp(
            MathF.Pow(missingSectorRatio, 0.72f) * MissingSectorNonlinearPenaltyGain,
            0.0f,
            MaxMissingSectorPenalty);

        var denomDipoleCount = Math.Max(runtimeInBoxCount, Math.Max(1, bestModelContribution.Length));
        var clutter = Math.Clamp((float)clutterOutsideSafeRadiusCount / denomDipoleCount, 0.0f, 1.0f);

        var finalScore = Math.Clamp((coverage - missingSectorPenalty) - (ClutterPenaltyFactor * clutter), 0.0f, 1.0f);
        return new PatMaxResult(
            X: poseX,
            Y: poseY,
            AngleRadians: angle,
            Scale: scale,
            AspectRatio: aspect,
            Score: finalScore,
            Coverage: coverage,
            Clutter: clutter,
            MatchedDipoleCount: matchedDipoleCount,
            IterationCount: iterations);
    }
}
