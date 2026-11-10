// File: src/VisionAlgorithm/OpenSrc/Common/IVisionToolResult.cs
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Universal zero-boxing contract implemented by all <c>OpenSrc</c> vision algorithm result value types.
/// Standardizes execution status, pass/fail polarity, matched model/template provenance, SPC database export,
/// and 100% deterministic <c>VisionCanvas</c> overlay replay.
/// </summary>
public interface IVisionToolResult
{
    /// <summary>Gets the standardized 20-byte result header (Category, Status, Verdict, MatchedModelIndex/Id, QualityScore, PrimaryMetric).</summary>
    VisionResultHeader Header { get; }

    /// <summary>Gets the world-space coordinate pose produced or inspected by the tool.</summary>
    FixturePose2D WorldPose { get; }

    /// <summary>Gets the representative world-space reference point.</summary>
    Vector2 ReferencePointWorld => new(WorldPose.CenterX, WorldPose.CenterY);

    /// <summary>Gets a value indicating whether the target feature or measurement succeeded.</summary>
    bool IsFound => Header.IsFound;

    /// <summary>Gets a value indicating whether the inspection passed (normalized polarity: <see langword="true"/> = OK / defect-free).</summary>
    bool IsPass => Header.IsPass;

    /// <summary>Exports a normalized flat SPC record for database persistence and statistical process control.</summary>
    VisionSpcRecord ToSpcRecord(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN);

    /// <summary>Exports the parametric geometry payload required to replay the exact visual overlay on <c>VisionCanvas</c>.</summary>
    VisionReplayPayload ToReplayPayload();

    /// <summary>Exports the combined SPC + <c>VisionCanvas</c> replay envelope.</summary>
    VisionInspectionEnvelope ToInspectionEnvelope(
        int nodeId = 0,
        Guid roiId = default,
        double specLsl = double.NaN,
        double specUsl = double.NaN) =>
        new(ToSpcRecord(nodeId, roiId, specLsl, specUsl), ToReplayPayload());
}
