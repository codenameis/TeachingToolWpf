namespace VisionCanvas.Rendering.Abstractions;

/// <summary>선택적 렌더 프로파일러에 전달되는 ROI 단계 통계.</summary>
public readonly record struct RoiRenderStats(
    long InstanceCount,
    long UploadedBytes,
    long CacheBytes,
    double PackMilliseconds,
    double CreateMilliseconds,
    double UploadMilliseconds,
    bool HasPendingDetails,
    int DeferredBlocks);
