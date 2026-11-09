// File: src/TeachingToolWpf.Presentation/Models/StructuredInspectionLogEntry.cs
using System;
using VisionGeometry;

namespace TeachingToolWpf.Models;

/// <summary>ROI 단위 또는 전체 영상 단위 검사 실행의 구조화된 로그 레코드.</summary>
public sealed record StructuredInspectionLogEntry(
    Guid EntryId,
    DateTimeOffset Timestamp,
    Guid? RoiId,
    string RoiName,
    InspectionToolKind Tool,
    string AlgorithmBadge,
    InspectionVerdict Verdict,
    string VerdictBadge,
    string VerdictAccent,
    string ScoreSummary,
    string PoseSummary,
    double ElapsedMilliseconds,
    BoundsD? TargetBounds)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string ElapsedText => $"{ElapsedMilliseconds:F1} ms";

    public string OneLineSummary =>
        $"[{TimestampText}] [{VerdictBadge}] {RoiName} · {AlgorithmBadge} · {ScoreSummary} · {PoseSummary} ({ElapsedText})";
}
