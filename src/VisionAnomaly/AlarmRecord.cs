namespace VisionAnomaly;

public sealed record AlarmRecord(Guid Id, string CameraName, Guid ModelId, long Sequence,
    DateTimeOffset OccurredAt, double MediaSeconds, AnomalyKind Kind, float Ratio,
    IReadOnlyList<AnomalyArea> Areas, string EvidenceFile, ReviewStatus Review = ReviewStatus.Pending);
