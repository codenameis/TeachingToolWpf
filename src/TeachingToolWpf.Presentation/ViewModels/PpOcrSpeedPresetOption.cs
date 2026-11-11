using VisionOcr.PpOcr;

namespace TeachingToolWpf.ViewModels;

/// <summary>UI option item for <see cref="PpOcrSpeedPreset"/>.</summary>
public sealed record PpOcrSpeedPresetOption(PpOcrSpeedPreset Preset, string Name);
