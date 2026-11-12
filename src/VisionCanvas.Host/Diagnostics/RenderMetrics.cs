namespace VisionCanvas.Diagnostics;

/// <summary>렌더 프레임 성능·지연·메모리 측정치 스냅샷.</summary>
public sealed record RenderMetrics(
    double CpuMilliseconds,
    double? GpuMilliseconds,
    double PresentMilliseconds,
    double FrameIntervalMilliseconds,
    double? SourceToSubmitMilliseconds,
    long Dropped,
    long FrameNumber,
    long PrivateBytes,
    long TextureBytes);
