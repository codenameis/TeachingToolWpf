using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionInspection.Cli;

public sealed record ImageSummary(string Path, int Width, int Height, string Version);
