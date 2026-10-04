namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>학습과 원본 검색에 공통인 2×2 반올림 면적 평균. 표시 LOD와 독립적이다.</summary>
public static class InspectionPyramid
{
    public static GrayImage Reduce(GrayImage image, int factor, CancellationToken cancellationToken = default)
    {
        if (factor is < 1 or > 32 || (factor & (factor - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }
        while (factor > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image = GradientImage.Reduce(image);
            factor /= 2;
        }
        return image;
    }
}
