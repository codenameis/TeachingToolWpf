// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Preprocessing/SimdAffineRoiExtractor.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Preprocessing;

/// <summary>
/// 1-pass inverse-mapped bilinear affine ROI extractor driven by <see cref="RoiFixturePose"/>.
/// Delegates to the shared <see cref="SimdAffineWarper"/> kernel so all vision modules share one exact pixel-center warper.
/// </summary>
public sealed class SimdAffineRoiExtractor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Extract(
        ReadOnlyImageView2D<byte> sourceView,
        in RoiFixturePose pose,
        Span<float> destinationRoi,
        int roiWidth,
        int roiHeight)
    {
        sourceView.EnsureValid();
        Extract(
            sourceView.Pixels,
            sourceView.Width,
            sourceView.Height,
            sourceView.Stride,
            in pose,
            destinationRoi,
            roiWidth,
            roiHeight);
    }

    public void Extract(
        ReadOnlySpan<byte> sourceImage,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        in RoiFixturePose pose,
        Span<float> destinationRoi,
        int roiWidth,
        int roiHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        pose.EnsureInitialized();

        var commonPose = FixturePose2D.CreateAnisotropic(
            pose.CenterX,
            pose.CenterY,
            pose.AngleRad,
            pose.ScaleX,
            pose.ScaleY);

        SimdAffineWarper.WarpAffineBilinear(
            sourceImage,
            sourceWidth,
            sourceHeight,
            sourceStride,
            commonPose,
            roiWidth,
            roiHeight,
            destinationRoi);
    }
}
