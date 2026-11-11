using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.ViewModels;

/// <summary>UI option item for <see cref="OcvPolaritySelection"/>.</summary>
public sealed record OcvPolarityOption(OcvPolaritySelection Polarity, string Name);
