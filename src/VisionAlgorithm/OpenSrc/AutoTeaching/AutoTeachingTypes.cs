// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/AutoTeachingTypes.cs
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Sequential 1-based identifier assigned to each unique part master in a <see cref="PartModelPool"/> (<c>#1</c>, <c>#2</c>, <c>#3</c>, ...).
/// </summary>
public readonly record struct PartModelId(int Value)
{
    public bool IsValid => Value > 0;
    public override string ToString() => $"#{Value}";
}

/// <summary>
/// Inspection tool type attached to a child ROI template relative to a parent part model.
/// </summary>
public enum ChildToolKind : byte
{
    SubpixelCaliperWidth = 0,
    GoldenDifferenceAnomaly = 1,
    RleBlobPinInspection = 2,
    FiducialCrosshairCheck = 3
}

/// <summary>
/// Defines a child inspection ROI in the local coordinate frame of its parent <see cref="PartPoolEntry"/>.
/// </summary>
public readonly record struct ChildRoiTemplateSpec(
    string ChildName,
    ChildToolKind ToolKind,
    FixturePose2D LocalOffsetPose,
    float Width,
    float Height);

/// <summary>
/// Instantiated world-space child ROI generated automatically when its parent part is recognized on a new board.
/// </summary>
public readonly record struct InstantiatedChildRoi(
    string ChildName,
    ChildToolKind ToolKind,
    FixturePose2D WorldPose,
    Vector2 WorldCenter,
    float Width,
    float Height);

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

/// <summary>
/// Summary of a part teaching operation in <see cref="PartModelPool.RegisterOrMergeOnTeach"/>.
/// </summary>
public readonly record struct PartTeachRegistrationResult(
    PartModelId AssignedId,
    bool WasMergedIntoExisting,
    float MatchedSimilarity,
    int TotalPoolCount);

/// <summary>
/// Complete output of <see cref="AutoTeachingOrchestrator.RecognizeAndAutoTeach"/> on a new product image.
/// </summary>
public readonly record struct AutoTeachingResult(
    DetectedPartInstance[] Instances,
    Dictionary<PartModelId, int> PartCounts,
    string SummaryText,
    int SharedPyramidLevels);
