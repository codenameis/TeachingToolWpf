// File: src/VisionAlgorithm/OpenSrc/Common/ReplaySubRegion.cs
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Compact parametric sub-region (28 bytes) used inside <see cref="VisionReplayPayload"/> to replay
/// per-character OCR segmentation boxes (with decoded character and pass/fail color), multi-blob bounding boxes,
/// or localized defect clusters on <c>VisionCanvas</c> without re-running the inspection algorithm.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct ReplaySubRegion(
    float CenterX,
    float CenterY,
    float Width,
    float Height,
    float AngleRadians,
    float ScoreOrMetric,
    char TagChar,
    bool IsPass);
