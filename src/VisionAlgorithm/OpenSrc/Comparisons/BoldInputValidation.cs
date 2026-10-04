using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.OpenSrc.Comparisons;

internal static class BoldInputValidation
{
    public static void ValidateInputSize(GrayImage image, int maximumPixels)
    {
        if ((long)image.Width * image.Height > maximumPixels)
        {
            throw new ArgumentException($"BOLD 비교 경로 입력은 {maximumPixels:N0} pixel 이하여야 합니다.", nameof(image));
        }
    }

    public static void ValidateRegion(GrayImage image, TrainingRegion region)
    {
        if ((!region.Mask.IsEmpty && region.Mask.Length != checked(image.Width * image.Height)) ||
            !double.IsFinite(region.ReferencePoint.X) || !double.IsFinite(region.ReferencePoint.Y) ||
            !double.IsFinite(region.ReferenceAngle) || region.ReferencePoint.X < 0 || region.ReferencePoint.Y < 0 ||
            region.ReferencePoint.X > image.Width || region.ReferencePoint.Y > image.Height)
        {
            throw new ArgumentException("학습 마스크, 기준점 또는 각도가 잘못되었습니다.", nameof(region));
        }
    }
}
