using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public sealed record ExecutionReport(double Milliseconds, long UploadBytes, long DownloadBytes,
    long HostWorkingBytes, long GpuWorkingBytes, long ManagedAllocatedBytes, IReadOnlyList<StagePlan> Stages)
{
    public IReadOnlyList<double> StageMilliseconds { get; init; } = [];
}
