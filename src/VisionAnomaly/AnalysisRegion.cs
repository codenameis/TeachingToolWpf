namespace VisionAnomaly;

public readonly record struct AnalysisRegion(double X, double Y, double Width, double Height)
{
    public static AnalysisRegion Full => new(0, 0, 1, 1);

    public void Validate()
    {
        if (!double.IsFinite(X + Y + Width + Height) || X < 0 || Y < 0 ||
            Width <= 0 || Height <= 0 || X + Width > 1.000001 || Y + Height > 1.000001)
        {
            throw new ArgumentException("관심 영역은 영상 안의 양수 크기여야 합니다.");
        }
    }
}
