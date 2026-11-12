using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Represents a single part instance recognized and verified by <see cref="MultiPartPoolRecognizer"/>,
/// together with its automatically placed child inspection ROIs.
/// </summary>
public readonly record struct DetectedPartInstance(
    PartModelId PartId,
    string PartName,
    FixturePose2D WorldPose,
    float GeometricScore,
    float AppearanceCosineScore,
    float MahalanobisDistance,
    InstantiatedChildRoi[] ChildRois);
