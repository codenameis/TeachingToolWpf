using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed record FilterExecutionReport(
    double Milliseconds,
    long UploadBytes,
    long DownloadBytes,
    long HostWorkingBytes,
    long GpuWorkingBytes,
    long ManagedAllocatedBytes,
    IReadOnlyList<FilterStageSummary> Stages)
{
    public IReadOnlyList<double> StageMilliseconds { get; init; } = [];
}
