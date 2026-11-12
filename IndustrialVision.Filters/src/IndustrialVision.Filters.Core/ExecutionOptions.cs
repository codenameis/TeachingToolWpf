using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public sealed record ExecutionOptions
{
    public ExecutionMode Mode { get; init; } = ExecutionMode.CpuOnly;
    public FallbackPolicy Fallback { get; init; } = FallbackPolicy.AllowAndReport;
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public long MemoryBudgetBytes { get; init; } = 1024L * 1024 * 1024;
    public long GpuMemoryBudgetBytes { get; init; } = 1024L * 1024 * 1024;
    public bool EnableSimd { get; init; } = true;
    public AccuracyPolicy Accuracy { get; init; } = AccuracyPolicy.ExactSemantics;
    public DiagnosticLevel Diagnostics { get; init; } = DiagnosticLevel.Summary;
    public GpuPlacement GpuPlacement { get; init; } = GpuPlacement.Conservative;

    public void Validate()
    {
        if (MaxDegreeOfParallelism < 1 || MaxDegreeOfParallelism > 1024 || MemoryBudgetBytes <= 0 ||
            GpuMemoryBudgetBytes <= 0 || !Enum.IsDefined(Mode) || !Enum.IsDefined(Fallback) ||
            !Enum.IsDefined(Accuracy) || !Enum.IsDefined(Diagnostics) || !Enum.IsDefined(GpuPlacement))
        {
            throw new ArgumentOutOfRangeException(nameof(ExecutionOptions));
        }
    }
}
