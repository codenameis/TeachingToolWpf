using VisionOcr.PpOcr;

namespace TeachingToolWpf.ViewModels;

/// <summary>UI option item for <see cref="PpOcrPipelineMode"/>.</summary>
public sealed record PpOcrPipelineOption(PpOcrPipelineMode Pipeline, string Name);
