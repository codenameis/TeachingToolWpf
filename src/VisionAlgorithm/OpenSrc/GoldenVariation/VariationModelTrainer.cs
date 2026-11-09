// File: src/VisionAlgorithm/OpenSrc/GoldenVariation/VariationModelTrainer.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.GoldenVariation;

/// <summary>
/// Training specification for building a statistical <see cref="VariationModel"/> from normal reference samples.
/// </summary>
public readonly record struct VariationTrainingSpec
{
    public const float DefaultAbsoluteMinimumThreshold = 14f;
    public const float DefaultSigmaMultiplier = 3.5f;
    public const float DefaultEdgeJitterTolerancePixels = 0.65f;

    private readonly bool _isInitialized;

    public float AbsoluteMinimumThreshold { get; }
    public float SigmaMultiplier { get; }
    public float EdgeJitterTolerancePixels { get; }
    public bool IsValid => _isInitialized && AbsoluteMinimumThreshold > 0f && SigmaMultiplier > 0f;

    public VariationTrainingSpec(
        float absoluteMinimumThreshold = DefaultAbsoluteMinimumThreshold,
        float sigmaMultiplier = DefaultSigmaMultiplier,
        float edgeJitterTolerancePixels = DefaultEdgeJitterTolerancePixels)
    {
        if (!float.IsFinite(absoluteMinimumThreshold) || absoluteMinimumThreshold <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteMinimumThreshold), "AbsoluteMinimumThreshold must be positive.");
        }

        if (!float.IsFinite(sigmaMultiplier) || sigmaMultiplier <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sigmaMultiplier), "SigmaMultiplier must be positive.");
        }

        if (!float.IsFinite(edgeJitterTolerancePixels) || edgeJitterTolerancePixels < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(edgeJitterTolerancePixels), "EdgeJitterTolerancePixels must be non-negative.");
        }

        AbsoluteMinimumThreshold = absoluteMinimumThreshold;
        SigmaMultiplier = sigmaMultiplier;
        EdgeJitterTolerancePixels = edgeJitterTolerancePixels;
        _isInitialized = true;
    }

    public static VariationTrainingSpec Default => new(
        DefaultAbsoluteMinimumThreshold,
        DefaultSigmaMultiplier,
        DefaultEdgeJitterTolerancePixels);

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("VariationTrainingSpec was not initialized via its constructor.");
        }
    }
}

/// <summary>
/// Represents a single normal training sample aligned by its <see cref="FixturePose2D"/>.
/// </summary>
public readonly record struct VariationTrainingSample(
    byte[] SourceImage,
    int SourceWidth,
    int SourceHeight,
    int SourceStride,
    FixturePose2D FixturePose);

/// <summary>
/// Trains a statistical <see cref="VariationModel"/> by warping $N$ normal samples into a canonical fixture frame,
/// computing Welford's numerically stable per-pixel mean $\mu(x,y)$ and unbiased sample standard deviation $\sigma(x,y)$,
/// and augmenting the threshold envelope with a pure C# $3 \times 3$ Sobel gradient magnitude
/// to absorb subpixel edge jitter.
/// </summary>
public static class VariationModelTrainer
{
    // [Math Why]: 3x3 Sobel operator normalizes central difference sum of weights (1 + 2 + 1) * 2 = 8.0 to yield true dI/dx, dI/dy per pixel.
    private const float SobelNormalizationFactor = 0.125f;

    /// <summary>
    /// Trains a <see cref="VariationModel"/> from a list of normal samples and their fixture poses.
    /// </summary>
    public static VariationModel Train(
        ReadOnlySpan<VariationTrainingSample> samples,
        int roiWidth,
        int roiHeight,
        VariationTrainingSpec spec,
        InspectionWorkspace workspace)
    {
        spec.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiWidth, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(roiHeight, 2);
        if (samples.IsEmpty)
        {
            throw new ArgumentException("At least one normal training sample is required.", nameof(samples));
        }

        int pixelCount = checked(roiWidth * roiHeight);
        float[] mean = new float[pixelCount];
        float[] m2 = new float[pixelCount];
        Span<float> warped = workspace.GetFloatBufferA(pixelCount);

        int count = 0;
        for (int s = 0; s < samples.Length; s++)
        {
            VariationTrainingSample sample = samples[s];
            SimdAffineWarper.WarpRectifiedRoi(
                sample.SourceImage,
                sample.SourceWidth,
                sample.SourceHeight,
                sample.SourceStride,
                sample.FixturePose,
                roiWidth,
                roiHeight,
                warped);

            count++;
            float invCount = 1f / count;

            // Welford's online mean and M2 variance accumulation
            for (int i = 0; i < pixelCount; i++)
            {
                float x = warped[i];
                float delta = x - mean[i];
                mean[i] += delta * invCount;
                float delta2 = x - mean[i];
                m2[i] += delta * delta2;
            }
        }

        float[] stdDev = new float[pixelCount];
        float invDoF = count > 1 ? 1f / (count - 1) : 0f;
        for (int i = 0; i < pixelCount; i++)
        {
            stdDev[i] = count > 1 ? MathF.Sqrt(MathF.Max(0f, m2[i] * invDoF)) : 0f;
        }

        float[] edgeGrad = new float[pixelCount];
        ComputeSobelGradientMagnitude(
            mean,
            roiWidth,
            roiHeight,
            edgeGrad);

        float[] adaptiveThreshold = new float[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            // [Math Why]: A subpixel fixture pose jitter delta_p creates an intensity variation bounded by ||grad(mu)|| * delta_p.
            // Adding EdgeJitterTolerancePixels * ||grad(mu)|| prevents false alarms along high-contrast edges.
            float statisticalBound = (spec.SigmaMultiplier * stdDev[i]) + (spec.EdgeJitterTolerancePixels * edgeGrad[i]);
            adaptiveThreshold[i] = MathF.Max(spec.AbsoluteMinimumThreshold, statisticalBound);
        }

        return new VariationModel(
            roiWidth,
            roiHeight,
            count,
            spec.AbsoluteMinimumThreshold,
            spec.SigmaMultiplier,
            spec.EdgeJitterTolerancePixels,
            mean,
            stdDev,
            edgeGrad,
            adaptiveThreshold);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ComputeSobelGradientMagnitude(
        ReadOnlySpan<float> meanImage,
        int width,
        int height,
        Span<float> gradientMagnitudeOut)
    {
        for (int y = 0; y < height; y++)
        {
            int yPrev = Reflect101Index(y - 1, height) * width;
            int yCurr = y * width;
            int yNext = Reflect101Index(y + 1, height) * width;

            for (int x = 0; x < width; x++)
            {
                int xPrev = Reflect101Index(x - 1, width);
                int xNext = Reflect101Index(x + 1, width);

                float p00 = meanImage[yPrev + xPrev];
                float p01 = meanImage[yPrev + x];
                float p02 = meanImage[yPrev + xNext];

                float p10 = meanImage[yCurr + xPrev];
                float p12 = meanImage[yCurr + xNext];

                float p20 = meanImage[yNext + xPrev];
                float p21 = meanImage[yNext + x];
                float p22 = meanImage[yNext + xNext];

                float gx = ((p02 + (2f * p12) + p22) - (p00 + (2f * p10) + p20)) * SobelNormalizationFactor;
                float gy = ((p20 + (2f * p21) + p22) - (p00 + (2f * p01) + p02)) * SobelNormalizationFactor;

                gradientMagnitudeOut[yCurr + x] = MathF.Sqrt((gx * gx) + (gy * gy));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Reflect101Index(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        if (index < 0)
        {
            index = -index;
        }

        if (index >= length)
        {
            index = (2 * length) - 2 - index;
        }

        return Math.Clamp(index, 0, length - 1);
    }
}
