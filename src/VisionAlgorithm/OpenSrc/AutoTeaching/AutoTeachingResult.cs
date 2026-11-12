using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Complete output of <see cref="AutoTeachingOrchestrator.RecognizeAndAutoTeach"/> on a new product image.
/// </summary>
public readonly record struct AutoTeachingResult(
    DetectedPartInstance[] Instances,
    Dictionary<PartModelId, int> PartCounts,
    string SummaryText,
    int SharedPyramidLevels);
