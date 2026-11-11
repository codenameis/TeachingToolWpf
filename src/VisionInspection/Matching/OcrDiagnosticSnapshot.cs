using System.Collections.Immutable;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// OCR 파라미터 튜닝을 위한 실시간 전처리·이진화·문자 분할 진단 스냅샷.
/// </summary>
public sealed record OcrDiagnosticSnapshot(
    int PreviewWidth,
    int PreviewHeight,
    ImmutableArray<byte> EnhancedGrayPixels,
    ImmutableArray<byte> BinarizedRgbaPixels,
    string RecognizedText,
    ImmutableArray<string> Lines,
    ImmutableArray<RecognizedCharBox> Characters,
    float MeanConfidence,
    float MinConfidence,
    float AcceptThreshold,
    float ForegroundContrastOffset,
    bool DarkOnLight,
    string PreprocessingBadge);
