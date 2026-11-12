namespace VisionCanvas.Rendering.Abstractions;

/// <summary>선택적 렌더 프로파일러에 전달되는 라벨 캐시 통계.</summary>
public readonly record struct LabelCacheStats(
    bool HasPending,
    int Created,
    int RetainedCount,
    long RetainedBytes,
    long Evicted);
