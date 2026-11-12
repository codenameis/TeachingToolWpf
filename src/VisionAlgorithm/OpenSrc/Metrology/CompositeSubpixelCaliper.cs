// File: src/VisionAlgorithm/OpenSrc/Metrology/CompositeSubpixelCaliper.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Multi-criteria 1D Subpixel Caliper engine supporting <see cref="CaliperSelectionMode.FirstEdge"/>,
/// <see cref="CaliperSelectionMode.LastEdge"/>, <see cref="CaliperSelectionMode.StrongestContrast"/>, and
/// continuous Gaussian affinity <see cref="CaliperSelectionMode.CompositeScore"/> (Cognex <c>CogCaliperTool</c> /
/// HALCON <c>measure_pairs</c> parity).
/// </summary>
public static class CompositeSubpixelCaliper
{
    private const int MinimumProfileLength = 8;
    private const int MaxStackAllocEdgeCandidates = 128;
    private const float UnitScaleTolerance = 1e-3f;
    private const float HalfScale = 0.5f;
    private const float MinimumEdgeSeparationPixels = 1.0f;
    private const float MinWeightSumFloor = 1e-6f;

    /// <summary>
    /// Measures a single 1D subpixel edge selected according to <see cref="CompositeCaliperSpec.SelectionMode"/>.
    /// Returns a <see cref="CaliperEdgeHit"/> with <c>GradientAmplitude &gt; 0</c> when found, or <c>default</c> when no edge satisfies the criteria.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static CaliperEdgeHit MeasureSingleEdge(
        ReadOnlyImageView2D<byte> imageView,
        FixturePose2D caliperPose,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity desiredPolarity,
        in CompositeCaliperSpec spec,
        InspectionWorkspace workspace)
    {
        imageView.EnsureValid();
        caliperPose.EnsureValid();
        spec.EnsureInitialized();
        ArgumentNullException.ThrowIfNull(workspace);

        CaliperMeasurementSpec baseSpec = spec.ToBaseMeasurementSpec();
        int profileLength = Math.Max(MinimumProfileLength, (int)MathF.Ceiling(scanLengthPixels));
        Span<float> profile = workspace.GetFloatBufferA(profileLength, clear: true);
        ProjectRectifiedProfile(
            imageView,
            caliperPose,
            profileLength,
            projectionWidthPixels,
            spec.ProjectionStripCount,
            workspace,
            profile);

        Span<float> response = workspace.GetFloatBufferB(profileLength, clear: true);
        ComputeGaussianFirstDerivative(profile, baseSpec, workspace, response);

        int kernelRadius = baseSpec.ComputeKernelRadius();
        int startIdx = kernelRadius + 1;
        int endIdx = profileLength - kernelRadius - 2;
        if (endIdx < startIdx)
        {
            return default;
        }

        bool found = false;
        float bestMetric = float.NegativeInfinity;
        CaliperEdgeHit bestHit = default;

        for (int i = startIdx; i <= endIdx; i++)
        {
            if (!TryEvaluateLocalExtremum(
                response,
                i,
                desiredPolarity,
                spec.MinimumContrast,
                caliperPose,
                profileLength,
                out CaliperEdgeHit candidate))
            {
                continue;
            }

            float metric = EvaluateSingleCandidateScore(candidate, in spec, profileLength);
            if (!found || metric > bestMetric)
            {
                bestMetric = metric;
                bestHit = candidate;
                found = true;
            }
        }

        return found ? bestHit : default;
    }

    /// <summary>
    /// Measures an ordered subpixel edge pair $(e_1, e_2)$ with $u_2 &gt; u_1 + 1$ selected according to
    /// <see cref="CompositeCaliperSpec.SelectionMode"/> (Strongest, First pair, Last pair, or Composite Gaussian affinity).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static CaliperPairResult MeasureEdgePair(
        ReadOnlyImageView2D<byte> imageView,
        FixturePose2D caliperPose,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity firstPolarity,
        CaliperEdgePolarity secondPolarity,
        in CompositeCaliperSpec spec,
        InspectionWorkspace workspace)
    {
        imageView.EnsureValid();
        caliperPose.EnsureValid();
        spec.EnsureInitialized();
        ArgumentNullException.ThrowIfNull(workspace);

        CaliperMeasurementSpec baseSpec = spec.ToBaseMeasurementSpec();
        int profileLength = Math.Max(MinimumProfileLength, (int)MathF.Ceiling(scanLengthPixels));
        Span<float> profile = workspace.GetFloatBufferA(profileLength, clear: true);
        ProjectRectifiedProfile(
            imageView,
            caliperPose,
            profileLength,
            projectionWidthPixels,
            spec.ProjectionStripCount,
            workspace,
            profile);

        Span<float> response = workspace.GetFloatBufferB(profileLength, clear: true);
        ComputeGaussianFirstDerivative(profile, baseSpec, workspace, response);

        int kernelRadius = baseSpec.ComputeKernelRadius();
        int startIdx = kernelRadius + 1;
        int endIdx = profileLength - kernelRadius - 2;
        if (endIdx < startIdx)
        {
            return default;
        }

        int maxPossibleExtrema = Math.Max(1, ((endIdx - startIdx) / 2) + 2);
        Span<CaliperEdgeHit> firstCandidates = maxPossibleExtrema <= MaxStackAllocEdgeCandidates
            ? stackalloc CaliperEdgeHit[maxPossibleExtrema]
            : new CaliperEdgeHit[maxPossibleExtrema];
        Span<CaliperEdgeHit> secondCandidates = maxPossibleExtrema <= MaxStackAllocEdgeCandidates
            ? stackalloc CaliperEdgeHit[maxPossibleExtrema]
            : new CaliperEdgeHit[maxPossibleExtrema];

        int firstCount = 0;
        int secondCount = 0;

        for (int i = startIdx; i <= endIdx; i++)
        {
            if (TryEvaluateLocalExtremum(response, i, firstPolarity, spec.MinimumContrast, caliperPose, profileLength, out CaliperEdgeHit c1))
            {
                firstCandidates[firstCount++] = c1;
            }

            if (TryEvaluateLocalExtremum(response, i, secondPolarity, spec.MinimumContrast, caliperPose, profileLength, out CaliperEdgeHit c2))
            {
                secondCandidates[secondCount++] = c2;
            }
        }

        if (firstCount == 0 || secondCount == 0)
        {
            return default;
        }

        bool foundPair = false;
        float bestScore = float.NegativeInfinity;
        CaliperEdgeHit bestFirst = default;
        CaliperEdgeHit bestSecond = default;

        for (int f = 0; f < firstCount; f++)
        {
            CaliperEdgeHit e1 = firstCandidates[f];
            for (int s = 0; s < secondCount; s++)
            {
                CaliperEdgeHit e2 = secondCandidates[s];
                float spanU = e2.SubpixelPosition - e1.SubpixelPosition;
                if (spanU <= MinimumEdgeSeparationPixels)
                {
                    continue;
                }

                float pairScore = EvaluatePairCandidateScore(e1, e2, spanU * caliperPose.Scale, in spec, profileLength);
                if (!foundPair || pairScore > bestScore)
                {
                    bestScore = pairScore;
                    bestFirst = e1;
                    bestSecond = e2;
                    foundPair = true;
                }
            }
        }

        if (!foundPair)
        {
            return default;
        }

        float pairWidth = (bestSecond.SubpixelPosition - bestFirst.SubpixelPosition) * caliperPose.Scale;
        Vector2 midpoint = (bestFirst.WorldPoint + bestSecond.WorldPoint) * HalfScale;
        return new CaliperPairResult(
            IsFound: true,
            FirstEdge: bestFirst,
            SecondEdge: bestSecond,
            Value: new SubpixelWidthMeasurement(pairWidth, midpoint));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EvaluateSingleCandidateScore(
        CaliperEdgeHit candidate,
        in CompositeCaliperSpec spec,
        int profileLength)
    {
        float localU = (candidate.SubpixelPosition + HalfScale) - (profileLength * HalfScale);
        switch (spec.SelectionMode)
        {
            case CaliperSelectionMode.FirstEdge:
                return -candidate.SubpixelPosition;

            case CaliperSelectionMode.LastEdge:
                return candidate.SubpixelPosition;

            case CaliperSelectionMode.CompositeScore:
            {
                // [Math Why]: Continuous multi-criteria score S = (w_c * S_c + w_p * S_p) / (w_c + w_p)
                // where S_c = min(1, |G| / G_sat) and S_p = exp(-0.5 * ((u - u_0) / sigma_p)^2).
                float contrastAffinity = Math.Clamp(candidate.GradientAmplitude / spec.ContrastSaturation, 0.0f, 1.0f);
                float weightSum = spec.ContrastWeight;
                float weightedScore = spec.ContrastWeight * contrastAffinity;

                if (float.IsFinite(spec.ExpectedPosition) && spec.PositionWeight > 0.0f)
                {
                    float zPos = (localU - spec.ExpectedPosition) / spec.PositionToleranceSigma;
                    float posAffinity = MathF.Exp(-HalfScale * zPos * zPos);
                    weightedScore += spec.PositionWeight * posAffinity;
                    weightSum += spec.PositionWeight;
                }

                return weightSum > MinWeightSumFloor ? weightedScore / weightSum : contrastAffinity;
            }

            case CaliperSelectionMode.StrongestContrast:
            default:
                return candidate.GradientAmplitude;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EvaluatePairCandidateScore(
        CaliperEdgeHit e1,
        CaliperEdgeHit e2,
        float spanWorldPixels,
        in CompositeCaliperSpec spec,
        int profileLength)
    {
        float midProfileU = (e1.SubpixelPosition + e2.SubpixelPosition) * HalfScale;
        float midLocalU = (midProfileU + HalfScale) - (profileLength * HalfScale);
        float meanAmplitude = (e1.GradientAmplitude + e2.GradientAmplitude) * HalfScale;

        switch (spec.SelectionMode)
        {
            case CaliperSelectionMode.FirstEdge:
                // Prefer the earliest first edge; tie-break by tightest adjacent second edge
                return (-e1.SubpixelPosition * profileLength) - e2.SubpixelPosition;

            case CaliperSelectionMode.LastEdge:
                // Prefer the latest second edge; tie-break by latest preceding first edge
                return (e2.SubpixelPosition * profileLength) + e1.SubpixelPosition;

            case CaliperSelectionMode.CompositeScore:
            {
                // [Math Why]: S(e1, e2) = (w_c * S_c + w_p * S_p + w_s * S_s) / (w_c + w_p + w_s)
                // with S_p = exp(-0.5 * ((u_mid - u_0)/sigma_p)^2) and S_s = exp(-0.5 * ((w - w_0)/sigma_w)^2).
                float contrastAffinity = Math.Clamp(meanAmplitude / spec.ContrastSaturation, 0.0f, 1.0f);
                float weightSum = spec.ContrastWeight;
                float weightedScore = spec.ContrastWeight * contrastAffinity;

                if (float.IsFinite(spec.ExpectedPosition) && spec.PositionWeight > 0.0f)
                {
                    float zPos = (midLocalU - spec.ExpectedPosition) / spec.PositionToleranceSigma;
                    float posAffinity = MathF.Exp(-HalfScale * zPos * zPos);
                    weightedScore += spec.PositionWeight * posAffinity;
                    weightSum += spec.PositionWeight;
                }

                if (float.IsFinite(spec.ExpectedSpan) && spec.SpanWeight > 0.0f)
                {
                    float zSpan = (spanWorldPixels - spec.ExpectedSpan) / spec.SpanToleranceSigma;
                    float spanAffinity = MathF.Exp(-HalfScale * zSpan * zSpan);
                    weightedScore += spec.SpanWeight * spanAffinity;
                    weightSum += spec.SpanWeight;
                }

                return weightSum > MinWeightSumFloor ? weightedScore / weightSum : contrastAffinity;
            }

            case CaliperSelectionMode.StrongestContrast:
            default:
                return e1.GradientAmplitude + e2.GradientAmplitude;
        }
    }

    private static void ProjectRectifiedProfile(
        ReadOnlyImageView2D<byte> imageView,
        FixturePose2D caliperPose,
        int profileLength,
        float projectionWidthPixels,
        int stripCount,
        InspectionWorkspace workspace,
        Span<float> profileOut)
    {
        int roiHeight = Math.Max(1, stripCount);
        float verticalScale = roiHeight > 1 ? MathF.Max(1.0f, projectionWidthPixels) / roiHeight : 1.0f;
        Span<float> patch = workspace.GetFloatBufferC(profileLength * roiHeight);

        if (MathF.Abs(verticalScale - 1.0f) <= UnitScaleTolerance)
        {
            SimdAffineWarper.WarpRectifiedRoi(imageView, caliperPose, profileLength, roiHeight, patch);
        }
        else
        {
            int fullHeight = Math.Max(1, (int)MathF.Round(projectionWidthPixels));
            Span<float> fullPatch = workspace.GetFloatBufferC(profileLength * fullHeight);
            SimdAffineWarper.WarpRectifiedRoi(imageView, caliperPose, profileLength, fullHeight, fullPatch);
            roiHeight = fullHeight;
            patch = fullPatch;
        }

        profileOut.Clear();
        float invStrips = 1.0f / roiHeight;
        for (int r = 0; r < roiHeight; r++)
        {
            ReadOnlySpan<float> row = patch.Slice(r * profileLength, profileLength);
            for (int u = 0; u < profileLength; u++)
            {
                profileOut[u] += row[u] * invStrips;
            }
        }
    }

    private static void ComputeGaussianFirstDerivative(
        ReadOnlySpan<float> profile,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace,
        Span<float> responseOut)
    {
        int radius = spec.ComputeKernelRadius();
        int kernelLength = (2 * radius) + 1;
        Span<float> kernel = workspace.GetFloatBufferD(kernelLength);
        GaussianDerivative1DKernel.ConvolveNormalized(profile, spec.GaussianSigma, radius, kernel, responseOut);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryEvaluateLocalExtremum(
        ReadOnlySpan<float> response,
        int index,
        CaliperEdgePolarity desiredPolarity,
        float minimumContrast,
        FixturePose2D caliperPose,
        int profileLength,
        out CaliperEdgeHit hit)
    {
        hit = default;
        float g0 = response[index];
        float absG0 = MathF.Abs(g0);
        if (absG0 < minimumContrast)
        {
            return false;
        }

        CaliperEdgePolarity actualPolarity = g0 >= 0.0f
            ? CaliperEdgePolarity.DarkToLight
            : CaliperEdgePolarity.LightToDark;

        if (desiredPolarity != CaliperEdgePolarity.Any && actualPolarity != desiredPolarity)
        {
            return false;
        }

        float gm = response[index - 1];
        float gp = response[index + 1];

        bool isLocalMax = g0 > 0.0f && g0 >= gm && g0 > gp;
        bool isLocalMin = g0 < 0.0f && g0 <= gm && g0 < gp;
        if (!isLocalMax && !isLocalMin)
        {
            return false;
        }

        float delta = SubpixelInterpolator.InterpolateParabolicPeak(gm, g0, gp, out float rawPeak);
        float interpolatedPeak = MathF.Abs(rawPeak);

        float subpixelU = index + delta;
        float localX = (subpixelU + HalfScale) - (profileLength * HalfScale);
        Vector2 worldPoint = caliperPose.LocalToWorld(localX, 0.0f);

        hit = new CaliperEdgeHit(subpixelU, worldPoint, interpolatedPeak, actualPolarity);
        return true;
    }
}
