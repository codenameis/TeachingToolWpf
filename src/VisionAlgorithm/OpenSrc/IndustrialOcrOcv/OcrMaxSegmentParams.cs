namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Parameters for <see cref="OcrMaxSegmenter"/> controlling rectified line dimensions, morphological dot bridging,
/// and pitch-guided split/merge bounds.
/// </summary>
public readonly record struct OcrMaxSegmentParams
{
    public const int DefaultLineWidth = 360;
    public const int DefaultLineHeight = 40;
    public const int DefaultMinCharWidth = 6;
    public const int DefaultMaxCharWidth = 30;
    public const int DefaultExpectedPitch = 20;
    public const int DefaultDotClosingRadiusX = 2;
    public const int DefaultDotClosingRadiusY = 2;
    public const float DefaultForegroundContrastOffset = 18.0f;

    private readonly bool _isInitialized;

    public PrintMethodType PrintMethod { get; }
    public int RectifiedWidth { get; }
    public int RectifiedHeight { get; }
    public int MinCharWidth { get; }
    public int MaxCharWidth { get; }
    public int ExpectedPitch { get; }
    public int DotClosingRadiusX { get; }
    public int DotClosingRadiusY { get; }
    public float ForegroundContrastOffset { get; }
    public bool DarkOnLight { get; }
    public bool IsValid => _isInitialized && RectifiedWidth > 0 && RectifiedHeight > 0;

    public OcrMaxSegmentParams(
        PrintMethodType printMethod = PrintMethodType.ContinuousLaser,
        int rectifiedWidth = DefaultLineWidth,
        int rectifiedHeight = DefaultLineHeight,
        int minCharWidth = DefaultMinCharWidth,
        int maxCharWidth = DefaultMaxCharWidth,
        int expectedPitch = DefaultExpectedPitch,
        int dotClosingRadiusX = DefaultDotClosingRadiusX,
        int dotClosingRadiusY = DefaultDotClosingRadiusY,
        float foregroundContrastOffset = DefaultForegroundContrastOffset,
        bool darkOnLight = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rectifiedWidth, 8);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rectifiedHeight, 8);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minCharWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharWidth, minCharWidth);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedPitch, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(dotClosingRadiusX);
        ArgumentOutOfRangeException.ThrowIfNegative(dotClosingRadiusY);

        PrintMethod = printMethod;
        RectifiedWidth = rectifiedWidth;
        RectifiedHeight = rectifiedHeight;
        MinCharWidth = minCharWidth;
        MaxCharWidth = maxCharWidth;
        ExpectedPitch = expectedPitch;
        DotClosingRadiusX = dotClosingRadiusX;
        DotClosingRadiusY = dotClosingRadiusY;
        ForegroundContrastOffset = MathF.Max(2.0f, foregroundContrastOffset);
        DarkOnLight = darkOnLight;
        _isInitialized = true;
    }

    public static OcrMaxSegmentParams DefaultLaser => new(PrintMethodType.ContinuousLaser);

    public static OcrMaxSegmentParams DefaultDotMatrix => new(
        PrintMethodType.InkjetDotMatrix,
        DefaultLineWidth,
        DefaultLineHeight,
        DefaultMinCharWidth,
        DefaultMaxCharWidth,
        DefaultExpectedPitch,
        dotClosingRadiusX: 2,
        dotClosingRadiusY: 2,
        foregroundContrastOffset: 16.0f,
        darkOnLight: true);
}
