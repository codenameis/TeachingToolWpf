// File: src/VisionAlgorithm/OpenSrc/LightweightOcr/LightweightOcrTypes.cs
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// Parameters controlling <see cref="LightweightOcrEngine"/> strip normalization, sliding-column stride,
/// illumination detrending, and CTC sequence decoding.
/// </summary>
public readonly record struct LightweightOcrParams
{
    public const int CanonicalStripHeight = 32;
    public const int DefaultColumnStride = 2;
    public const int DefaultBeamWidth = 4;
    public const float DefaultMinConfidence = 0.60f;

    private readonly bool _isInitialized;

    public int StripWidth { get; }
    public int StripHeight { get; }
    public int ColumnStride { get; }
    public int BeamWidth { get; }
    public float MinConfidence { get; }
    public bool DarkOnLight { get; }
    public bool UsePrefixBeamSearch { get; }
    public bool DetrendIllumination { get; }
    public bool IsValid => _isInitialized && StripWidth > 0 && StripHeight > 0;

    public LightweightOcrParams(
        int stripWidth = 512,
        int stripHeight = CanonicalStripHeight,
        int columnStride = DefaultColumnStride,
        int beamWidth = DefaultBeamWidth,
        float minConfidence = DefaultMinConfidence,
        bool darkOnLight = true,
        bool usePrefixBeamSearch = true,
        bool detrendIllumination = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stripWidth, 16);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stripHeight, 8);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(columnStride, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(beamWidth, 0);

        StripWidth = stripWidth;
        StripHeight = stripHeight;
        ColumnStride = columnStride;
        BeamWidth = beamWidth;
        MinConfidence = Math.Clamp(minConfidence, 0.1f, 0.99f);
        DarkOnLight = darkOnLight;
        UsePrefixBeamSearch = usePrefixBeamSearch;
        DetrendIllumination = detrendIllumination;
        _isInitialized = true;
    }

    public static LightweightOcrParams Default => new();
}

/// <summary>
/// Represents a detected text-line bounding region and its oriented <see cref="FixturePose2D"/> extracted by <see cref="FastRleTextLineDetector"/>.
/// </summary>
public readonly record struct TextLineBox(
    int LineIndex,
    FixturePose2D Pose,
    int WidthPixels,
    int HeightPixels,
    int MinCol,
    int MinRow,
    int MaxCol,
    int MaxRow,
    long ForegroundAreaPixels);

/// <summary>
/// Result of 1-pass CTC sequence recognition on a single text line.
/// </summary>
public readonly record struct LightweightOcrResult(
    string Text,
    float Confidence,
    int TimeStepCount,
    FixturePose2D LinePose,
    bool IsPass);
