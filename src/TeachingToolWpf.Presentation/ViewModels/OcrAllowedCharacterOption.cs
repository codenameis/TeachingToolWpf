using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace TeachingToolWpf.ViewModels;

/// <summary>UI option item for <see cref="OcrAllowedCharacterSet"/>.</summary>
public sealed record OcrAllowedCharacterOption(OcrAllowedCharacterSet Kind, string Name);
