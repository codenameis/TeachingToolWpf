// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/OcrOcvTypes.cs
namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Industrial character printing modality controlling RLE morphological dot-bridging prior to vertical pitch segmentation.
/// </summary>
public enum PrintMethodType : byte
{
    ContinuousLaser = 0,
    InkjetDotMatrix = 1
}

/// <summary>
/// Bitmask flags describing pharmaceutical/industrial OCR &amp; OCV verification failure modes.
/// </summary>
[Flags]
public enum OcvDefectFlags : uint
{
    None = 0,
    LowAcceptanceScore = 1u << 0,
    ConfusableAmbiguity = 1u << 1,
    MissingStrokeOrDot = 1u << 2,
    InkSmear = 1u << 3,
    LowContrast = 1u << 4,
    InvalidCalendarDate = 1u << 5,
    FieldingSyntaxMismatch = 1u << 6,
    SegmentationCountMismatch = 1u << 7
}

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

/// <summary>
/// Parameters for <see cref="OcrMaxFieldingMatcher"/> and <see cref="HalconProjectionOcvInspector"/>.
/// </summary>
public readonly record struct HalconOcvParams
{
    public const float DefaultAcceptThreshold = 0.68f;
    public const float DefaultConfusionMarginThreshold = 0.08f;
    public const float DefaultMinContrast = 28.0f;
    public const float DefaultMinProjectionCorrelation = 0.65f;
    public const float DefaultMaxQuadrantDropRatio = 0.30f;
    public const float DefaultMaxMarginSmearRatio = 0.24f;

    private readonly bool _isInitialized;

    public float AcceptThreshold { get; }
    public float ConfusionMarginThreshold { get; }
    public float MinContrast { get; }
    public float MinProjectionCorrelation { get; }
    public float MaxQuadrantDropRatio { get; }
    public float MaxMarginSmearRatio { get; }
    public bool AdaptGray { get; }
    public bool AdaptPos { get; }
    public bool VerifyCalendarDate { get; }
    public bool IsValid => _isInitialized;

    public HalconOcvParams(
        float acceptThreshold = DefaultAcceptThreshold,
        float confusionMarginThreshold = DefaultConfusionMarginThreshold,
        float minContrast = DefaultMinContrast,
        float minProjectionCorrelation = DefaultMinProjectionCorrelation,
        float maxQuadrantDropRatio = DefaultMaxQuadrantDropRatio,
        float maxMarginSmearRatio = DefaultMaxMarginSmearRatio,
        bool adaptGray = true,
        bool adaptPos = true,
        bool verifyCalendarDate = true)
    {
        AcceptThreshold = Math.Clamp(acceptThreshold, 0.1f, 0.99f);
        ConfusionMarginThreshold = Math.Clamp(confusionMarginThreshold, 0.0f, 0.8f);
        MinContrast = MathF.Max(1.0f, minContrast);
        MinProjectionCorrelation = Math.Clamp(minProjectionCorrelation, 0.1f, 0.99f);
        MaxQuadrantDropRatio = Math.Clamp(maxQuadrantDropRatio, 0.05f, 0.95f);
        MaxMarginSmearRatio = Math.Clamp(maxMarginSmearRatio, 0.05f, 0.95f);
        AdaptGray = adaptGray;
        AdaptPos = adaptPos;
        VerifyCalendarDate = verifyCalendarDate;
        _isInitialized = true;
    }

    public static HalconOcvParams Default => new();
}

/// <summary>
/// Defines position-specific syntax fielding rules (e.g., <c>"EXP 9999.99.99"</c> where <c>'9'</c> = digit,
/// <c>'A'</c> = uppercase letter, <c>'*'</c> = any character, and other symbols are literal constraints).
/// </summary>
public readonly record struct FieldingMaskRule
{
    public string MaskPattern { get; }
    public bool HasMask => !string.IsNullOrEmpty(MaskPattern);

    public FieldingMaskRule(string maskPattern)
    {
        MaskPattern = maskPattern ?? string.Empty;
    }

    public static FieldingMaskRule ExpiryDatePattern => new("EXP 9999.99.99");

    public bool IsAllowedAt(int charIndexInTokenStream, char candidateChar)
    {
        if (string.IsNullOrEmpty(MaskPattern))
        {
            return true;
        }

        // Compare against non-space tokens in the fielding mask
        int nonSpaceIdx = 0;
        for (int i = 0; i < MaskPattern.Length; i++)
        {
            char maskCh = MaskPattern[i];
            if (maskCh == ' ')
            {
                continue;
            }

            if (nonSpaceIdx == charIndexInTokenStream)
            {
                return maskCh switch
                {
                    '9' => candidateChar is >= '0' and <= '9',
                    'A' => candidateChar is >= 'A' and <= 'Z',
                    '*' => true,
                    _ => candidateChar == maskCh
                };
            }

            nonSpaceIdx++;
        }

        return true;
    }
}

/// <summary>
/// Bounding box of a segmented character inside the rectified text-line strip.
/// </summary>
public readonly record struct SegmentedCharBox(
    int Index,
    int StartCol,
    int EndCol,
    int TopRow,
    int BottomRow,
    bool PrecededBySpace,
    float InterCharBridgeDensity)
{
    public int Width => (EndCol - StartCol) + 1;
    public int Height => (BottomRow - TopRow) + 1;
}

/// <summary>
/// Per-character OCR classification and OCV verification result.
/// </summary>
public readonly record struct CharVerifyResult(
    int CharIndex,
    char DecodedChar,
    char RunnerUpChar,
    float PrimaryScore,
    float RunnerUpScore,
    float ConfusionMargin,
    float OcvQualityScore,
    float ContrastRange,
    float WorstQuadrantRetention,
    float SubpixelShiftX,
    float SubpixelShiftY,
    OcvDefectFlags DefectFlags,
    bool IsPass);

/// <summary>
/// Complete text-line OCR/OCV inspection result including decoded string, per-character diagnostics, and overall pass/fail verdict.
/// </summary>
public readonly record struct OcrOcvLineResult(
    string DecodedText,
    bool IsPass,
    OcvDefectFlags DefectFlags,
    float MinCharScore,
    float MinConfusionMargin,
    float MinOcvQualityScore,
    float MinContrast,
    CharVerifyResult[] CharacterResults);
