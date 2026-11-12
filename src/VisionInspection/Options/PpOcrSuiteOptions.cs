using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionOcr.PpOcr;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 9번 통합 OCR / OCV (PP-OCRv4 ONNX 범용 판독) 전용 옵션 및 범위 상수.
/// </summary>
public sealed record PpOcrSuiteOptions(
    OcrAllowedCharacterSet AllowedCharacters = PpOcrSuiteOptions.DefaultAllowedCharacters,
    float MinConfidence = PpOcrSuiteOptions.DefaultMinConfidence,
    float MinCharacterConfidence = PpOcrSuiteOptions.DefaultMinCharacterConfidence,
    PpOcrLanguage LanguageMode = PpOcrSuiteOptions.DefaultLanguageMode,
    PpOcrPolarityMode Polarity = PpOcrSuiteOptions.DefaultPolarity,
    PpOcrPipelineMode Pipeline = PpOcrSuiteOptions.DefaultPipeline,
    PpOcrSpeedPreset Preset = PpOcrSuiteOptions.DefaultPreset,
    bool IsInteractivePreview = PpOcrSuiteOptions.DefaultIsInteractivePreview)
{
    public const OcrAllowedCharacterSet DefaultAllowedCharacters = PpOcrRunOptions.DefaultAllowedCharacterSet;
    public const float DefaultMinConfidence = PpOcrRunOptions.DefaultMinAcceptConfidence;
    public const float DefaultMinCharacterConfidence = PpOcrRunOptions.DefaultMinCharacterConfidence;
    public const PpOcrLanguage DefaultLanguageMode = PpOcrRunOptions.DefaultLanguage;
    public const PpOcrPolarityMode DefaultPolarity = PpOcrRunOptions.DefaultPolarityMode;
    public const PpOcrPipelineMode DefaultPipeline = PpOcrRunOptions.DefaultPipelineMode;
    public const PpOcrSpeedPreset DefaultPreset = PpOcrSpeedPreset.UniversalAuto;
    public const bool DefaultIsInteractivePreview = PpOcrRunOptions.DefaultFastInteractivePreview;

    public const float MinAcceptConfidenceBound = 0.05f;
    public const float MaxAcceptConfidenceBound = 1.0f;
    public const float MinCharacterConfidenceBound = 0.0f;

    public PpOcrSuiteOptions Validate() => new(
        AllowedCharacters: AllowedCharacters,
        MinConfidence: Math.Clamp(MinConfidence, MinAcceptConfidenceBound, MaxAcceptConfidenceBound),
        MinCharacterConfidence: Math.Clamp(MinCharacterConfidence, MinCharacterConfidenceBound, MaxAcceptConfidenceBound),
        LanguageMode: LanguageMode,
        Polarity: Polarity,
        Pipeline: Pipeline,
        Preset: Preset,
        IsInteractivePreview: IsInteractivePreview);
}
