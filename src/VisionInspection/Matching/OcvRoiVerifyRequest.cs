using System.Collections.Immutable;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Per-ROI verification input carrying geometry, font slot identifier, and document-persisted expected text / field mask.
/// </summary>
public sealed record OcvRoiVerifyRequest(
    Guid RoiId,
    string Name,
    PointD Center,
    double Width,
    double Height,
    double AngleRadians,
    ImmutableArray<PointD> Outline,
    string ExpectedText,
    string FormatMask = "",
    string FontId = "default");
