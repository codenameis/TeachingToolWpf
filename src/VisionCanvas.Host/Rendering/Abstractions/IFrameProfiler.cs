namespace VisionCanvas.Rendering.Abstractions;

/// <summary>GPU 타이머·단계별 할당/GC 프로파일·표시 통계를 제공하는 선택적 프로파일러 훅.</summary>
public interface IFrameProfiler
{
    bool IsProfileEnabled { get; }

    double? BeginGpuTiming();

    void EndGpuTiming();

    void RecordScene(
        RenderScene scene,
        long cpuStartTicks,
        long paintStartTicks,
        long submitStartTicks,
        long submitEndTicks,
        long allocatedBytesStart,
        TimeSpan gcPauseStart,
        SpriteMesh mesh,
        LabelCacheStats labels);

    DisplayPresentStatistics? ReadPresentationStatistics();
}
