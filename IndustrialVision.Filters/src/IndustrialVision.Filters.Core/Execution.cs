using System.Buffers;
using System.Runtime.CompilerServices;

namespace IndustrialVision.Filters.Core;

public enum ExecutionMode
{
    CpuOnly, CpuGpu
}
public enum FallbackPolicy
{
    AllowAndReport, Throw
}
public enum AccuracyPolicy
{
    ExactSemantics
}
public enum DiagnosticLevel
{
    Summary, Stages
}
public enum GpuPlacement
{
    Conservative, PreferGpu
}

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

public sealed record StagePlan(string Filter, string Device, string Reason);
public sealed record BackendCapabilities(string Backend, string Device, long MemoryBytes, IReadOnlyList<string> Filters);
public sealed record ExecutionReport(double Milliseconds, long UploadBytes, long DownloadBytes,
    long HostWorkingBytes, long GpuWorkingBytes, long ManagedAllocatedBytes, IReadOnlyList<StagePlan> Stages)
{
    public IReadOnlyList<double> StageMilliseconds { get; init; } = [];
}

/// <summary>GPU 구현만 참조하는 중립 팩토리. 지원 불가 사유만 반환하며 구현 오류는 전파한다.</summary>
public interface IGpuBackend
{
    BackendCapabilities GetCapabilities();
    string? GetUnsupportedReason(Recipe recipe, ImageDescriptor input, ExecutionOptions options);
    IGpuProgram Compile(Recipe recipe, ImageDescriptor input, ExecutionOptions options);
}

/// <summary>호스트 plan이 소유. 모든 메서드는 동기 완료하며 단일 호출자만 사용한다.</summary>
public interface IGpuProgram : IDisposable
{
    long WorkingBytes
    {
        get;
    }
    ReadOnlyMemory<double> StageMilliseconds
    {
        get;
    }
    long Upload(ImageView input);
    void Run(CancellationToken cancellationToken);
    long Download(HostImage output);
}

public sealed class MemoryBudgetException(string message) : InvalidOperationException(message);
public sealed class GpuUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>실제 대여 용량을 계상하며 plan 종료 시 반환한다.</summary>
public sealed class PooledBuffer<T> : IDisposable where T : unmanaged
{
    private T[]? _array;
    public T[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer<T>));
    public int Length
    {
        get;
    }
    public long Bytes => (long)Array.Length * Unsafe.SizeOf<T>();

    public static long EstimateBytes(int length)
    {
        if (length <= 0 || length > 1 << 29)
        {
            throw new MemoryBudgetException("Use tile execution for this image size.");
        }
        var capacity = 16;
        while (capacity < length)
        {
            capacity = checked(capacity * 2);
        }
        return (long)capacity * Unsafe.SizeOf<T>();
    }

    public PooledBuffer(int length)
    {
        Length = length;
        _array = ArrayPool<T>.Shared.Rent(length);
    }

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }
}
