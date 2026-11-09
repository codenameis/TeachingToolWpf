namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 학습된 ROI 영역을 회전 보정(Rectified)하여 추출한 8비트 그레이스케일 미리보기 패치 값 객체.
/// </summary>
public readonly record struct RectifiedPatternPatch
{
    private RectifiedPatternPatch(byte[] pixels, int width, int height, bool isInitialized)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        IsInitialized = isInitialized;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsInitialized { get; }
    public bool IsEmpty => !IsInitialized || Pixels is null || Width <= 0 || Height <= 0;

    public static RectifiedPatternPatch Empty => default;

    public static RectifiedPatternPatch Create(byte[] pixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length < checked(width * height))
        {
            throw new ArgumentException("Pixel buffer length is smaller than width * height.", nameof(pixels));
        }

        return new RectifiedPatternPatch(pixels, width, height, isInitialized: true);
    }
}
