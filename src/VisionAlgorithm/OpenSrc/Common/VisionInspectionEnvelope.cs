// File: src/VisionAlgorithm/OpenSrc/Common/VisionInspectionEnvelope.cs
namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Unified inspection envelope combining the flat SQL/SPC analytics record (<see cref="VisionSpcRecord"/>)
/// and the parametric <c>VisionCanvas</c> graphical replay packet (<see cref="VisionReplayPayload"/>).
/// </summary>
public readonly record struct VisionInspectionEnvelope(
    VisionSpcRecord Spc,
    VisionReplayPayload Replay)
{
    public bool IsFound => Spc.IsFound;
    public bool IsPass => Spc.IsPass;
    public VisionToolCategory Category => Spc.Category;
}
