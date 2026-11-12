using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>기준점에서의 정수 픽셀 오프셋과 unsigned 8방향 bin(0..7).</summary>
public readonly record struct CandidateScoreFeature(int X, int Y, int Bin);
