using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct QuantizedFeature(int X, int Y, int Bin);
