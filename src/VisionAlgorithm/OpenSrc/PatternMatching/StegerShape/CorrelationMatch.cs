using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record CorrelationMatch(ImagePoint ReferencePoint, double Score);
