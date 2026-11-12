using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// 1D Subpixel Caliper engine using analytical 1st-derivative Gaussian convolution and 3-point parabolic extremum interpolation.
/// </summary>
public static class SubpixelCaliper1D
{
    // [Math Why]: Minimum profile length (8 px) guarantees sufficient support for Gaussian derivative kernel + 3-point parabolic interpolation.
    private const int MinimumProfileLength = 8;

    // [Perf Why]: Up to 128 candidate edge extrema per polarity fit comfortably on the thread stack without heap allocation.
    private const int MaxStackAllocEdgeCandidates = 128;

    // [Math Why]: Tolerance for checking whether vertical projection strip spacing equals unit scale (1.0 px).
    private const float UnitScaleTolerance = 1e-3f;

    /// <summary>
    /// Extracts a 1D averaged intensity profile from an 8-bit <see cref="ReadOnlyImageView2D{T}"/> along a fixture-aligned scan box and locates the strongest edge.
    /// </summary>
    public static bool TryMeasureSingleEdge(
        ReadOnlyImageView2D<byte> imageView,
        FixturePose2D caliperPose,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity desiredPolarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace,
        out CaliperEdgeHit bestEdge)
    {
        imageView.EnsureValid();
        caliperPose.EnsureValid();
        spec.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);

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
        ComputeGaussianFirstDerivative(profile, spec, workspace, response);

        return TryFindStrongestEdgeInResponse(
            response,
            spec.ComputeKernelRadius(),
            caliperPose,
            profileLength,
            desiredPolarity,
            spec.MinimumContrast,
            out bestEdge);
    }

    /// <summary>
    /// Extracts a 1D averaged intensity profile from an 8-bit image along a fixture-aligned scan box and locates the strongest edge.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMeasureSingleEdge(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        FixturePose2D caliperPose,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity desiredPolarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace,
        out CaliperEdgeHit bestEdge)
        => TryMeasureSingleEdge(
            new ReadOnlyImageView2D<byte>(image, width, height, stride),
            caliperPose,
            scanLengthPixels,
            projectionWidthPixels,
            desiredPolarity,
            spec,
            workspace,
            out bestEdge);

    /// <summary>
    /// Measures the subpixel distance (width) between a first edge (<paramref name="firstPolarity"/>) and a second edge (<paramref name="secondPolarity"/>)
    /// by selecting the valid ordered pair $(e_1, e_2)$ with $u_{\text{second}} > u_{\text{first}} + 1$ that maximizes combined gradient amplitude.
    /// </summary>
    public static CaliperPairResult MeasureEdgePairWidth(
        ReadOnlyImageView2D<byte> imageView,
        FixturePose2D caliperPose,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity firstPolarity,
        CaliperEdgePolarity secondPolarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace)
    {
        imageView.EnsureValid();
        caliperPose.EnsureValid();
        spec.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);

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
        ComputeGaussianFirstDerivative(profile, spec, workspace, response);

        int kernelRadius = spec.ComputeKernelRadius();
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
            if (TryEvaluateLocalExtremum(response, i, firstPolarity, spec.MinimumContrast, caliperPose, profileLength, out CaliperEdgeHit candidateFirst))
            {
                firstCandidates[firstCount++] = candidateFirst;
            }

            if (TryEvaluateLocalExtremum(response, i, secondPolarity, spec.MinimumContrast, caliperPose, profileLength, out CaliperEdgeHit candidateSecond))
            {
                secondCandidates[secondCount++] = candidateSecond;
            }
        }

        if (firstCount == 0 || secondCount == 0)
        {
            return default;
        }

        bool foundPair = false;
        float bestCombinedAmplitude = 0f;
        CaliperEdgeHit bestFirst = default;
        CaliperEdgeHit bestSecond = default;

        // Evaluate all valid ordered edge pairs (e1, e2) with u_second > u_first + 1 and select maximum combined gradient amplitude.
        for (int f = 0; f < firstCount; f++)
        {
            CaliperEdgeHit e1 = firstCandidates[f];
            for (int s = 0; s < secondCount; s++)
            {
                CaliperEdgeHit e2 = secondCandidates[s];
                if (e2.SubpixelPosition <= e1.SubpixelPosition + 1f)
                {
                    continue;
                }

                float combinedAmplitude = e1.GradientAmplitude + e2.GradientAmplitude;
                if (!foundPair || combinedAmplitude > bestCombinedAmplitude)
                {
                    bestCombinedAmplitude = combinedAmplitude;
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
        Vector2 midpoint = (bestFirst.WorldPoint + bestSecond.WorldPoint) * 0.5f;
        return new CaliperPairResult(
            IsFound: true,
            FirstEdge: bestFirst,
            SecondEdge: bestSecond,
            Value: new SubpixelWidthMeasurement(pairWidth, midpoint));
    }

    /// <summary>
    /// Measures the subpixel distance (width) between a first edge (<paramref name="firstPolarity"/>) and a second edge (<paramref name="secondPolarity"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CaliperPairResult MeasureEdgePairWidth(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        FixturePose2D caliperPose,
        float scanLengthPixels,
        float projectionWidthPixels,
        CaliperEdgePolarity firstPolarity,
        CaliperEdgePolarity secondPolarity,
        CaliperMeasurementSpec spec,
        InspectionWorkspace workspace)
        => MeasureEdgePairWidth(
            new ReadOnlyImageView2D<byte>(image, width, height, stride),
            caliperPose,
            scanLengthPixels,
            projectionWidthPixels,
            firstPolarity,
            secondPolarity,
            spec,
            workspace);

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
        float verticalScale = roiHeight > 1 ? Math.Max(1f, projectionWidthPixels) / roiHeight : 1f;
        Span<float> patch = workspace.GetFloatBufferC(profileLength * roiHeight);

        // If projectionWidthPixels == roiHeight, SimdAffineWarper can directly extract the strip block.
        if (MathF.Abs(verticalScale - 1f) <= UnitScaleTolerance)
        {
            SimdAffineWarper.WarpRectifiedRoi(
                imageView,
                caliperPose,
                profileLength,
                roiHeight,
                patch);
        }
        else
        {
            int fullHeight = Math.Max(1, (int)MathF.Round(projectionWidthPixels));
            Span<float> fullPatch = workspace.GetFloatBufferC(profileLength * fullHeight);
            SimdAffineWarper.WarpRectifiedRoi(
                imageView,
                caliperPose,
                profileLength,
                fullHeight,
                fullPatch);
            roiHeight = fullHeight;
            patch = fullPatch;
        }

        profileOut.Clear();
        float invStrips = 1f / roiHeight;
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

    private static bool TryFindStrongestEdgeInResponse(
        ReadOnlySpan<float> response,
        int kernelRadius,
        FixturePose2D caliperPose,
        int profileLength,
        CaliperEdgePolarity desiredPolarity,
        float minimumContrast,
        out CaliperEdgeHit bestEdge)
    {
        bestEdge = default;
        bool found = false;
        int startIdx = kernelRadius + 1;
        int endIdx = profileLength - kernelRadius - 2;

        for (int i = startIdx; i <= endIdx; i++)
        {
            if (TryEvaluateLocalExtremum(response, i, desiredPolarity, minimumContrast, caliperPose, profileLength, out CaliperEdgeHit hit))
            {
                if (!found || hit.GradientAmplitude > bestEdge.GradientAmplitude)
                {
                    bestEdge = hit;
                    found = true;
                }
            }
        }

        return found;
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

        CaliperEdgePolarity actualPolarity = g0 >= 0f
            ? CaliperEdgePolarity.DarkToLight
            : CaliperEdgePolarity.LightToDark;

        if (desiredPolarity != CaliperEdgePolarity.Any && actualPolarity != desiredPolarity)
        {
            return false;
        }

        float gm = response[index - 1];
        float gp = response[index + 1];

        bool isLocalMax = g0 > 0f && g0 >= gm && g0 > gp;
        bool isLocalMin = g0 < 0f && g0 <= gm && g0 < gp;
        if (!isLocalMax && !isLocalMin)
        {
            return false;
        }

        // [Math Why]: 3-point parabolic interpolation around extremum (gm, g0, gp) via shared SubpixelInterpolator.
        float delta = SubpixelInterpolator.InterpolateParabolicPeak(gm, g0, gp, out float rawPeak);
        float interpolatedPeak = MathF.Abs(rawPeak);

        float subpixelU = index + delta;
        // Convert profile index u in [0, profileLength - 1] to local fixture X coordinate:
        // localX = (u + 0.5) - (profileLength * 0.5).
        float localX = (subpixelU + 0.5f) - (profileLength * 0.5f);
        Vector2 worldPoint = caliperPose.LocalToWorld(localX, 0f);

        hit = new CaliperEdgeHit(subpixelU, worldPoint, interpolatedPeak, actualPolarity);
        return true;
    }
}
