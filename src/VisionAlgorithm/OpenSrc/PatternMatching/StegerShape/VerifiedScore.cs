namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Verification outcome for a single candidate pose, recording alignment score, supported feature fraction, and quadrant coverage count.
/// </summary>
internal readonly record struct VerifiedScore(double Score, double Support, int Quadrants);
