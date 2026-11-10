// File: src/VisionAlgorithm/OpenSrc/BlobRle/BinaryPixelCountInspector.cs
using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Zero-allocation intensity histogram and binary pixel area counter over a fixture-aligned ROI.
/// Computes foreground pixel count within <c>[MinGrayThreshold, MaxGrayThreshold]</c>, area occupancy percentage,
/// center of mass in world coordinates, and grayscale mean/standard deviation in a single pass.
/// </summary>
public static class BinaryPixelCountInspector
{
    private const float PercentMultiplier = 100.0f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static BinaryPixelCountResult Inspect(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D inputFixture,
        in BinaryPixelCountSpec spec,
        VisionWorkspace workspace)
    {
        image.EnsureValid();
        spec.EnsureInitialized();
        ArgumentNullException.ThrowIfNull(workspace);

        FixturePose2D baseFixture = inputFixture.IsValid ? inputFixture : FixturePose2D.Identity;
        FixturePose2D roiWorldPose = baseFixture.Compose(spec.LocalRoiOffset);

        int roiW = spec.RoiWidth;
        int roiH = spec.RoiHeight;
        int totalPixels = checked(roiW * roiH);

        Span<byte> patchSpan = workspace.GetByteBufferA(totalPixels);
        Span<float> floatScratch = workspace.GetFloatBufferA(totalPixels);
        SimdAffineWarper.WarpAffineBilinear(image, in roiWorldPose, roiW, roiH, patchSpan, floatScratch);

        byte minThresh = spec.MinGrayThreshold;
        byte maxThresh = spec.MaxGrayThreshold;

        int validCount = 0;
        long sumGray = 0;
        long sumGraySq = 0;
        double sumValidX = 0.0;
        double sumValidY = 0.0;
        byte minGray = byte.MaxValue;
        byte maxGray = byte.MinValue;

        float halfW = (roiW - 1) * 0.5f;
        float halfH = (roiH - 1) * 0.5f;

        for (int r = 0; r < roiH; r++)
        {
            ReadOnlySpan<byte> row = patchSpan.Slice(r * roiW, roiW);
            float localY = r - halfH;
            for (int c = 0; c < roiW; c++)
            {
                byte g = row[c];
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

                if (g >= minThresh && g <= maxThresh)
                {
                    validCount++;
                    sumValidX += c - halfW;
                    sumValidY += localY;
                }
            }
        }

        float invTotal = 1.0f / totalPixels;
        float areaRatioPercent = (validCount * PercentMultiplier) * invTotal;
        float meanGray = sumGray * invTotal;
        float variance = MathF.Max(0.0f, (sumGraySq * invTotal) - (meanGray * meanGray));
        float stdDevGray = MathF.Sqrt(variance);

        Vector2 comWorld;
        if (validCount > 0)
        {
            float localX = (float)(sumValidX / validCount);
            float localY = (float)(sumValidY / validCount);
            comWorld = roiWorldPose.LocalToWorld(localX, localY);
        }
        else
        {
            comWorld = new Vector2(roiWorldPose.CenterX, roiWorldPose.CenterY);
        }

        bool isPass = areaRatioPercent >= spec.MinPassAreaPercent && areaRatioPercent <= spec.MaxPassAreaPercent;
        float qualityScore = Math.Clamp(areaRatioPercent / PercentMultiplier, 0.0f, 1.0f);

        VisionResultHeader header = VisionResultHeader.SucceededJudge(
            category: VisionToolCategory.BinaryPixelCount,
            isPass: isPass,
            qualityScore: qualityScore,
            primaryMetric: areaRatioPercent,
            matchedModelIndex: spec.ConditionIndex,
            matchedModelId: spec.ConditionId);

        return new BinaryPixelCountResult(
            Header: header,
            RoiWorldPose: roiWorldPose,
            CenterOfMassWorld: comWorld,
            ValidPixelCount: validCount,
            TotalPixelCount: totalPixels,
            AreaRatioPercent: areaRatioPercent,
            MeanGrayValue: meanGray,
            StdDevGrayValue: stdDevGray,
            MinObservedGray: minGray,
            MaxObservedGray: maxGray,
            RoiWidth: roiW,
            RoiHeight: roiH);
    }
}
