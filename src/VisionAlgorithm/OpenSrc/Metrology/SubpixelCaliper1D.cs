// File: src/VisionAlgorithm/OpenSrc/Metrology/SubpixelCaliper1D.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Specifies the intensity transition polarity along the caliper scan direction.
/// </summary>
public enum CaliperEdgePolarity
{
    DarkToLight = 0,
    LightToDark = 1,
    Any = 2
}

/// <summary>
/// Configuration parameters for 1D subpixel caliper measurement.
/// </summary>
public readonly record struct CaliperMeasurementSpec
{
    // [Math Why]: Minimum Gaussian derivative sigma avoids aliasing on discrete 1px grids.
    private const float MinimumGaussianSigma = 0.5f;

    // [Math Why]: Truncating the 1D Gaussian derivative filter at 3.5 sigma captures >99.95% of kernel energy.
    private const float KernelRadiusSigmaMultiplier = 3.5f;

    // [Math Why]: Default sigma = 1.1 px balances sub-pixel step-edge localization against sensor shot noise.
    private const float DefaultGaussianSigma = 1.1f;

    // [Math Why]: Default minimum gradient amplitude (12 DN) rejects background sensor floor fluctuations.
    private const float DefaultMinimumContrast = 12f;

    // [Perf Why]: Default 9 orthogonal projection strips attenuate 2D speckle noise by 3x (sqrt(9)).
    private const int DefaultProjectionStripCount = 9;

    private readonly bool _isInitialized;

    public float GaussianSigma { get; }
    public float MinimumContrast { get; }
    public int ProjectionStripCount { get; }
    public bool IsValid => _isInitialized && GaussianSigma >= MinimumGaussianSigma && ProjectionStripCount >= 1;

    public CaliperMeasurementSpec(
        float gaussianSigma = DefaultGaussianSigma,
        float minimumContrast = DefaultMinimumContrast,
        int projectionStripCount = DefaultProjectionStripCount)
    {
        if (!float.IsFinite(gaussianSigma) || gaussianSigma < MinimumGaussianSigma)
        {
            throw new ArgumentOutOfRangeException(nameof(gaussianSigma), $"GaussianSigma must be >= {MinimumGaussianSigma}.");
        }

        if (!float.IsFinite(minimumContrast) || minimumContrast <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumContrast), "MinimumContrast must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(projectionStripCount, 1);

        GaussianSigma = gaussianSigma;
        MinimumContrast = minimumContrast;
        ProjectionStripCount = projectionStripCount;
        _isInitialized = true;
    }

    public static CaliperMeasurementSpec Default => new(DefaultGaussianSigma, DefaultMinimumContrast, DefaultProjectionStripCount);

    public int ComputeKernelRadius()
        => Math.Max(2, (int)MathF.Ceiling(GaussianSigma * KernelRadiusSigmaMultiplier));

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("CaliperMeasurementSpec was not initialized via its constructor.");
        }
    }
}

/// <summary>
/// Represents a single subpixel edge transition detected along a 1D caliper profile.
/// </summary>
public readonly record struct CaliperEdgeHit(
    float SubpixelPosition,
    Vector2 WorldPoint,
    float GradientAmplitude,
    CaliperEdgePolarity Polarity);

/// <summary>
/// Represents the subpixel width and world-space midpoint of a measured edge pair.
/// </summary>
public readonly record struct SubpixelWidthMeasurement(
    float WidthPixels,
    Vector2 MidpointWorld);

/// <summary>
/// Represents the result of a 1D edge-pair width measurement.
/// </summary>
public readonly record struct CaliperPairResult(
    bool IsFound,
    CaliperEdgeHit FirstEdge,
    CaliperEdgeHit SecondEdge,
    SubpixelWidthMeasurement Value)
{
    public float SubpixelWidth => Value.WidthPixels;
    public Vector2 MidpointWorld => Value.MidpointWorld;
}

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

        float sigma = spec.GaussianSigma;
        float invTwoSigmaSq = 1f / (2f * sigma * sigma);
        float positiveIntegral = 0f;

        // [Math Why]: Analytical 1st derivative of Gaussian g'(x) = -x / sigma^2 * exp(-x^2 / (2*sigma^2)).
        // For discrete convolution (profile * k)[u] = sum_{m=-R}^{R} profile[u + m] * weight(m) to yield the exact
        // step-edge amplitude (I_right - I_left) for a unit step, we normalize the positive half-weights to sum to 1.0.
        for (int m = -radius; m <= radius; m++)
        {
            float w = m * MathF.Exp(-(m * m) * invTwoSigmaSq);
            kernel[m + radius] = w;
            if (m > 0)
            {
                positiveIntegral += w;
            }
        }

        float normFactor = positiveIntegral > 0f ? 1f / positiveIntegral : 1f;
        for (int k = 0; k < kernelLength; k++)
        {
            kernel[k] *= normFactor;
        }

        int n = profile.Length;
        responseOut.Clear();
        for (int u = radius; u < n - radius; u++)
        {
            float acc = 0f;
            for (int m = -radius; m <= radius; m++)
            {
                acc += profile[u + m] * kernel[m + radius];
            }

            responseOut[u] = acc;
        }
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
