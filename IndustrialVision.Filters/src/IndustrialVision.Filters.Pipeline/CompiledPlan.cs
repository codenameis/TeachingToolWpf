using System.Diagnostics;
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Cpu;

namespace IndustrialVision.Filters.Pipeline;

/// <summary>재사용 가능한 단일 호출자 컨텍스트. Dispose는 GPU 완료 이후에만 허용된다.</summary>
public sealed class CompiledPlan : IDisposable
{
    private readonly CpuProgram? _cpu;
    private readonly IGpuProgram? _gpu;
    private bool _disposed;
    private bool _uploaded;
    private bool _completed;
    private int _busy;
    public ImageDescriptor Input
    {
        get;
    }
    public ImageDescriptor Output
    {
        get;
    }
    public long RecipeVersion
    {
        get;
    }
    public int RequiredHalo
    {
        get;
    }
    public IReadOnlyList<StagePlan> Stages
    {
        get;
    }
    public long HostWorkingBytes
    {
        get;
    }
    public long GpuWorkingBytes => _gpu?.WorkingBytes ?? 0;
    public bool UsesGpu => _gpu is not null;

    internal CompiledPlan(ImageDescriptor input, ImageDescriptor output, long version, int halo,
        StagePlan[] stages, long hostBytes, CpuProgram cpu)
        : this(input, output, version, halo, stages, hostBytes)
    {
        _cpu = cpu;
    }

    internal CompiledPlan(ImageDescriptor input, ImageDescriptor output, long version, int halo,
        StagePlan[] stages, long hostBytes, IGpuProgram gpu)
        : this(input, output, version, halo, stages, hostBytes)
    {
        _gpu = gpu;
    }

    private CompiledPlan(ImageDescriptor input, ImageDescriptor output, long version, int halo, StagePlan[] stages, long hostBytes)
    {
        Input = input;
        Output = output;
        RecipeVersion = version;
        RequiredHalo = halo;
        Stages = Array.AsReadOnly(stages);
        HostWorkingBytes = hostBytes;
    }

    public ExecutionReport Execute(ImageView input, HostImage output, CancellationToken token = default)
    {
        Enter();
        try
        {
            _completed = false;
            _uploaded = false;
            ValidateInput(input);
            ValidateOutput(output);
            if (input.Data.Span.Overlaps(output.Data.Span) || input.Validity.Span.Overlaps(output.Data.Span) ||
                input.Data.Span.Overlaps(output.Validity.Span) || input.Validity.Span.Overlaps(output.Validity.Span))
            {
                throw new ArgumentException("In-place or aliased execution is not supported.");
            }
            token.ThrowIfCancellationRequested();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            long upload = 0;
            long download = 0;
            if (_gpu is not null)
            {
                upload = _gpu.Upload(input);
                _uploaded = true;
                _gpu.Run(token);
                token.ThrowIfCancellationRequested();
                download = _gpu.Download(output);
            }
            else
            {
                _cpu!.Execute(input, output, token);
            }
            token.ThrowIfCancellationRequested();
            _completed = true;
            return new(timer.Elapsed.TotalMilliseconds, upload, download, HostWorkingBytes, GpuWorkingBytes,
                GC.GetTotalAllocatedBytes(true) - allocated, Stages)
            {
                StageMilliseconds = (_gpu?.StageMilliseconds ?? _cpu!.StageMilliseconds).ToArray()
            };
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public long UploadHostInput(ImageView input)
    {
        Enter();
        try
        {
            RequireGpu();
            _uploaded = false;
            _completed = false;
            ValidateInput(input);
            var bytes = _gpu!.Upload(input);
            _uploaded = true;
            return bytes;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>동일한 원본 device 입력으로 재실행하며 완료 동기화 시간을 포함한다.</summary>
    public double ExecuteDevice(CancellationToken token = default)
    {
        Enter();
        try
        {
            RequireGpu();
            _completed = false;
            if (!_uploaded)
            {
                throw new InvalidOperationException("Upload input first.");
            }
            var timer = Stopwatch.StartNew();
            _gpu!.Run(token);
            _completed = true;
            return timer.Elapsed.TotalMilliseconds;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public long CopyOutputToHost(HostImage output)
    {
        Enter();
        try
        {
            RequireGpu();
            ValidateOutput(output);
            if (!_completed)
            {
                throw new InvalidOperationException("No completed device result.");
            }
            return _gpu!.Download(output);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private void RequireGpu()
    {
        if (_gpu is null)
        {
            throw new InvalidOperationException("This plan executes on the CPU.");
        }
    }

    private void ValidateInput(ImageView input)
    {
        if (input.Descriptor != Input)
        {
            throw new ArgumentException("Input descriptor changed; recompile the recipe.");
        }
    }

    private void ValidateOutput(HostImage output)
    {
        if (output.Descriptor != Output)
        {
            throw new ArgumentException("Output descriptor mismatch.");
        }
        _ = output.Data;
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("A compiled plan has a single caller; concurrent use is forbidden.");
        }
        if (_disposed)
        {
            Volatile.Write(ref _busy, 0);
            throw new ObjectDisposedException(nameof(CompiledPlan));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Enter();
        try
        {
            _disposed = true;
            _gpu?.Dispose();
            _cpu?.Dispose();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
