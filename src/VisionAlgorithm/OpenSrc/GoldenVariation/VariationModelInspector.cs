// File: src/VisionAlgorithm/OpenSrc/GoldenVariation/VariationModelInspector.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.GoldenVariation;

/// <summary>
/// Summary result of inspecting a target ROI against a trained <see cref="VariationModel"/>.
/// </summary>
public readonly record struct VariationInspectionResult(
    bool IsDefectFree,
    int DefectPixelCount,
    float MaxExcessDifference,
    float MaxNormalizedZScore);

/// <summary>
/// Performs SIMD-accelerated statistical golden-template comparison:
/// $|I(x,y) - \mu(x,y)| > T_{\text{adaptive}}(x,y)$, producing a binary defect mask (`255` = defect, `0` = normal)
/// and continuous excess residual map.
/// </summary>
public static class VariationModelInspector
{
    // [Perf Why]: AVX2 processes 8 float pixels per register.
    private const int Avx2LaneCount = 8;

    // [Math Why]: Mask clear-bit 0x7FFFFFFF zeroes the IEEE-754 sign bit to compute |x| in a single bitwise AND.
    private const uint AbsBitMask = 0x7FFF_FFFFu;

    // [Math Why]: Floor on threshold denominator avoids division by zero when computing normalized anomaly Z-scores.
    private const float MinimumThresholdDenominator = 1e-3f;

    /// <summary>
    /// Warps the target ROI from <paramref name="sourceView"/> at <paramref name="targetPose"/> and evaluates
    /// pixel-wise statistical deviation against <paramref name="model"/>.
    /// </summary>
    public static VariationInspectionResult Inspect(
        ReadOnlyImageView2D<byte> sourceView,
        FixturePose2D targetPose,
        VariationModel model,
        int minimumDefectAreaPixels,
        InspectionWorkspace workspace,
        Span<byte> binaryDefectMaskOut)
    {
        sourceView.EnsureValid();
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workspace);
        targetPose.EnsureValid();

        int pixelCount = checked(model.Width * model.Height);
        if (binaryDefectMaskOut.Length < pixelCount)
        {
            throw new ArgumentException("binaryDefectMaskOut length must be >= model.Width * model.Height.", nameof(binaryDefectMaskOut));
        }

        Span<float> warped = workspace.GetFloatBufferA(pixelCount);
        SimdAffineWarper.WarpRectifiedRoi(
            sourceView,
            targetPose,
            model.Width,
            model.Height,
            warped);

        return EvaluateRectifiedPatch(
            warped,
            model,
            minimumDefectAreaPixels,
            binaryDefectMaskOut);
    }

    /// <summary>
    /// Warps the target ROI from <paramref name="sourceImage"/> at <paramref name="targetPose"/> and evaluates
    /// pixel-wise statistical deviation against <paramref name="model"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VariationInspectionResult Inspect(
        ReadOnlySpan<byte> sourceImage,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        FixturePose2D targetPose,
        VariationModel model,
        int minimumDefectAreaPixels,
        InspectionWorkspace workspace,
        Span<byte> binaryDefectMaskOut)
        => Inspect(
            new ReadOnlyImageView2D<byte>(sourceImage, sourceWidth, sourceHeight, sourceStride),
            targetPose,
            model,
            minimumDefectAreaPixels,
            workspace,
            binaryDefectMaskOut);

    /// <summary>
    /// Evaluates an already-warped floating-point ROI buffer against <paramref name="model"/>.
    /// </summary>
    public static VariationInspectionResult EvaluateRectifiedPatch(
        ReadOnlySpan<float> rectifiedRoi,
        VariationModel model,
        int minimumDefectAreaPixels,
        Span<byte> binaryDefectMaskOut)
    {
        ArgumentNullException.ThrowIfNull(model);
        int pixelCount = checked(model.Width * model.Height);
        if (rectifiedRoi.Length < pixelCount || binaryDefectMaskOut.Length < pixelCount)
        {
            throw new ArgumentException("Input and output buffers must have length >= model.Width * model.Height.");
        }

        ReadOnlySpan<float> mean = model.MeanImage;
        ReadOnlySpan<float> threshold = model.AdaptiveThresholdImage;

        int defectPixels = 0;
        float maxExcess = 0f;
        float maxRatio = 0f;

        int i = 0;
        if (Avx2.IsSupported && pixelCount >= Avx2LaneCount)
        {
            Vector256<float> vAbsMask = Vector256.Create(AbsBitMask).AsSingle();
            Vector256<float> vMinDenom = Vector256.Create(MinimumThresholdDenominator);
            Vector256<float> vMaxExcess = Vector256<float>.Zero;
            Vector256<float> vMaxRatio = Vector256<float>.Zero;

            ref float roiRef = ref MemoryMarshal.GetReference(rectifiedRoi);
            ref float meanRef = ref MemoryMarshal.GetReference(mean);
            ref float threshRef = ref MemoryMarshal.GetReference(threshold);
            ref byte maskRef = ref MemoryMarshal.GetReference(binaryDefectMaskOut);

            int simdLimit = pixelCount - (pixelCount % Avx2LaneCount);
            for (; i < simdLimit; i += Avx2LaneCount)
            {
                Vector256<float> vImg = Unsafe.ReadUnaligned<Vector256<float>>(
                    ref Unsafe.As<float, byte>(ref Unsafe.Add(ref roiRef, i)));
                Vector256<float> vMean = Unsafe.ReadUnaligned<Vector256<float>>(
                    ref Unsafe.As<float, byte>(ref Unsafe.Add(ref meanRef, i)));
                Vector256<float> vThresh = Unsafe.ReadUnaligned<Vector256<float>>(
                    ref Unsafe.As<float, byte>(ref Unsafe.Add(ref threshRef, i)));

                Vector256<float> vDiff = Avx.And(Avx.Subtract(vImg, vMean), vAbsMask);
                Vector256<float> vExcess = Avx.Subtract(vDiff, vThresh);
                Vector256<float> vRatio = Avx.Divide(vDiff, Avx.Max(vThresh, vMinDenom));

                vMaxExcess = Avx.Max(vMaxExcess, vExcess);
                vMaxRatio = Avx.Max(vMaxRatio, vRatio);

                Vector256<float> vCmp = Avx.Compare(vDiff, vThresh, FloatComparisonMode.OrderedGreaterThanNonSignaling);
                int bitMask = Avx.MoveMask(vCmp);
                if (bitMask == 0)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref maskRef, i), 0UL);
                }
                else
                {
                    for (int lane = 0; lane < Avx2LaneCount; lane++)
                    {
                        bool isDefect = ((bitMask >> lane) & 1) != 0;
                        Unsafe.Add(ref maskRef, i + lane) = isDefect ? byte.MaxValue : (byte)0;
                        if (isDefect)
                        {
                            defectPixels++;
                        }
                    }
                }
            }

            Span<float> excessLanes = stackalloc float[Avx2LaneCount];
            Span<float> ratioLanes = stackalloc float[Avx2LaneCount];
            vMaxExcess.CopyTo(excessLanes);
            vMaxRatio.CopyTo(ratioLanes);
            for (int lane = 0; lane < Avx2LaneCount; lane++)
            {
                if (excessLanes[lane] > maxExcess)
                {
                    maxExcess = excessLanes[lane];
                }

                if (ratioLanes[lane] > maxRatio)
                {
                    maxRatio = ratioLanes[lane];
                }
            }
        }

        for (; i < pixelCount; i++)
        {
            float diff = MathF.Abs(rectifiedRoi[i] - mean[i]);
            float thresh = MathF.Max(threshold[i], MinimumThresholdDenominator);
            float excess = diff - thresh;
            float ratio = diff / thresh;

            if (excess > maxExcess)
            {
                maxExcess = excess;
            }

            if (ratio > maxRatio)
            {
                maxRatio = ratio;
            }

            if (diff > thresh)
            {
                binaryDefectMaskOut[i] = byte.MaxValue;
                defectPixels++;
            }
            else
            {
                binaryDefectMaskOut[i] = 0;
            }
        }

        bool isDefectFree = defectPixels < Math.Max(1, minimumDefectAreaPixels);
        return new VariationInspectionResult(
            IsDefectFree: isDefectFree,
            DefectPixelCount: defectPixels,
            MaxExcessDifference: MathF.Max(0f, maxExcess),
            MaxNormalizedZScore: maxRatio);
    }
}
