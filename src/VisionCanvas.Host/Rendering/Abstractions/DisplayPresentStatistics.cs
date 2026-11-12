namespace VisionCanvas.Rendering.Abstractions;

/// <summary>스왑체인 표시 통계 스냅샷.</summary>
public readonly record struct DisplayPresentStatistics(
    uint PresentCount,
    uint PresentRefreshCount,
    uint SyncRefreshCount,
    long SyncQpcTime);
