using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal sealed record PoseTemplate(double Angle, double Scale, ImmutableArray<ImmutableArray<QuantizedFeature>> Levels);
