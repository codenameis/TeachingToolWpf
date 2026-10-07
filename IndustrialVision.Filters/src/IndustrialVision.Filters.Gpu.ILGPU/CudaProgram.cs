using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Gpu;

internal sealed class CudaProgram : IGpuProgram
{
    private readonly CudaBackend _backend;
    private readonly Recipe _recipe;
    private readonly ImageDescriptor _descriptor;
    private readonly AcceleratorStream _stream;
    private readonly MemoryBuffer1D<float, Stride1D.Dense>[] _planes;
    private readonly MemoryBuffer1D<float, Stride1D.Dense>?[] _maps;
    private readonly MemoryBuffer1D<float, Stride1D.Dense>?[] _dark;
    private readonly MemoryBuffer1D<float, Stride1D.Dense>?[] _kernels;
    private readonly PooledBuffer<float> _transfer;
    private readonly double[] _stageMilliseconds;
    private MemoryBuffer1D<float, Stride1D.Dense>? _result;
    private bool _disposed;
    private bool _registered;
    public long WorkingBytes
    {
        get;
    }
    public ReadOnlyMemory<double> StageMilliseconds => _stageMilliseconds;

    public static long EstimateBytes(Recipe recipe, ImageDescriptor input) => checked((input.PixelCount * 5 * sizeof(float)) +
        RecipeValidation.CalibrationBytes(recipe) + recipe.Filters.OfType<Gaussian>().Sum(g => (long)((g.Radius * 2) + 1) * 4));

    public CudaProgram(CudaBackend backend, Accelerator accelerator, Recipe recipe, ImageDescriptor descriptor, ExecutionOptions options)
    {
        _backend = backend;
        _recipe = recipe;
        _descriptor = descriptor;
        _stageMilliseconds = options.Diagnostics == DiagnosticLevel.Stages ? new double[recipe.Filters.Count] : [];
        var count = checked((int)descriptor.PixelCount);
        if (PooledBuffer<float>.EstimateBytes(count) > options.MemoryBudgetBytes)
        {
            throw new MemoryBudgetException("GPU host staging buffer exceeds host budget.");
        }
        _planes = new MemoryBuffer1D<float, Stride1D.Dense>[5];
        _maps = new MemoryBuffer1D<float, Stride1D.Dense>?[recipe.Filters.Count];
        _dark = new MemoryBuffer1D<float, Stride1D.Dense>?[recipe.Filters.Count];
        _kernels = new MemoryBuffer1D<float, Stride1D.Dense>?[recipe.Filters.Count];
        _stream = null!;
        _transfer = null!;
        try
        {
            _stream = accelerator.CreateStream();
            _transfer = new(count);
            if (_transfer.Bytes > options.MemoryBudgetBytes)
            {
                throw new MemoryBudgetException("Actual rented GPU staging exceeds host budget.");
            }
            for (var i = 0; i < _planes.Length; i++)
            {
                _planes[i] = accelerator.Allocate1D<float>(count);
            }
            for (var i = 0; i < recipe.Filters.Count; i++)
            {
                if (recipe.Filters[i] is FlatField flat)
                {
                    _maps[i] = accelerator.Allocate1D<float>(count);
                    _maps[i]!.View.CopyFromCPU(_stream, flat.Map.Values);
                    if (flat.Dark is not null)
                    {
                        _dark[i] = accelerator.Allocate1D<float>(count);
                        _dark[i]!.View.CopyFromCPU(_stream, flat.Dark.Values);
                    }
                }
                if (recipe.Filters[i] is Gaussian gaussian)
                {
                    var kernel = gaussian.CreateKernel();
                    _kernels[i] = accelerator.Allocate1D<float>(kernel.Length);
                    _kernels[i]!.View.CopyFromCPU(_stream, kernel);
                }
            }
            _stream.Synchronize();
            WorkingBytes = EstimateBytes(recipe, descriptor);
            _registered = true;
        }
        catch (Exception failure)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }
            throw;
        }
    }

    public long Upload(ImageView input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _result = null;
        input.CopyToFloat(_transfer.Array);
        _planes[0].View.CopyFromCPU(_stream, _transfer.Array.AsSpan(0, _transfer.Length));
        _stream.Synchronize();
        return (long)_transfer.Length * sizeof(float);
    }

    public void Run(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _result = null;
        // 원본 GPU 입력은 반복 device-resident 실행에서 보존한다.
        var current = _planes[0];
        var next = _planes[1];
        var alternate = _planes[2];
        var temporary = _planes[3];
        var auxiliary = _planes[4];
        var count = checked((int)_descriptor.PixelCount);
        try
        {
            for (var i = 0; i < _recipe.Filters.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = _stageMilliseconds.Length > 0 ? Stopwatch.GetTimestamp() : 0;
                var settings = new KernelSettings { Width = _descriptor.Width, Height = _descriptor.Height };
                switch (_recipe.Filters[i])
                {
                    case FlatField flat:
                        settings.Kind = (int)flat.Kind;
                        settings.Scale = flat.Scale;
                        settings.Minimum = flat.MinimumDenominator;
                        settings.HasDark = flat.Dark is null ? 0 : 1;
                        _backend.Correct(_stream, count, current.View.BaseView, next.View.BaseView,
                            _maps[i]!.View.BaseView, (_dark[i] ?? _maps[i])!.View.BaseView, settings);
                        break;
                    case Gaussian gaussian:
                        settings.Radius = gaussian.Radius;
                        _backend.Convolve(_stream, count, current.View.BaseView, temporary.View.BaseView, _kernels[i]!.View.BaseView, settings);
                        settings.Vertical = 1;
                        _backend.Convolve(_stream, count, temporary.View.BaseView, next.View.BaseView, _kernels[i]!.View.BaseView, settings);
                        break;
                    case Morphology morphology:
                        settings.Radius = morphology.Radius;
                        settings.Kind = morphology.Kind is MorphologyKind.Dilation or MorphologyKind.Closing or MorphologyKind.BlackHat ? 1 : 0;
                        _backend.Extrema(_stream, count, current.View.BaseView, temporary.View.BaseView, settings);
                        settings.Vertical = 1;
                        var single = morphology.Kind is MorphologyKind.Erosion or MorphologyKind.Dilation;
                        _backend.Extrema(_stream, count, temporary.View.BaseView, (single ? next : auxiliary).View.BaseView, settings);
                        if (!single)
                        {
                            settings.Vertical = 0;
                            settings.Kind = 1 - settings.Kind;
                            _backend.Extrema(_stream, count, auxiliary.View.BaseView, temporary.View.BaseView, settings);
                            settings.Vertical = 1;
                            _backend.Extrema(_stream, count, temporary.View.BaseView, next.View.BaseView, settings);
                            if (morphology.Kind is MorphologyKind.WhiteTopHat or MorphologyKind.BlackHat)
                            {
                                settings.Kind = morphology.Kind == MorphologyKind.BlackHat ? 1 : 0;
                                _backend.Subtract(_stream, count, current.View.BaseView, next.View.BaseView, settings);
                            }
                        }
                        break;
                    case Threshold threshold:
                        settings.Level = threshold.Level;
                        _backend.Threshold(_stream, count, current.View.BaseView, next.View.BaseView, settings);
                        break;
                    default:
                        throw new NotSupportedException("Unsupported CUDA stage.");
                }
                current = next;
                (next, alternate) = (alternate, next);
                if (_stageMilliseconds.Length > 0)
                {
                    _stream.Synchronize();
                    _stageMilliseconds[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            }
        }
        finally
        {
            // 취소/실패 뒤에도 제출된 명령이 버퍼 수명을 벗어나지 않는다.
            _stream.Synchronize();
        }
        cancellationToken.ThrowIfCancellationRequested();
        _result = current;
    }

    public long Download(HostImage output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_result is null)
        {
            throw new InvalidOperationException("No completed device result.");
        }
        _result.View.CopyToCPU(_stream, _transfer.Array.AsSpan(0, _transfer.Length));
        _stream.Synchronize();
        output.WriteFloat(_transfer.Array.AsSpan(0, _transfer.Length));
        return (long)_transfer.Length * sizeof(float);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var failures = new List<Exception>();
        Release(() => _stream?.Synchronize());
        foreach (var buffer in _kernels.Concat(_dark).Concat(_maps).Concat(_planes))
        {
            Release(() => buffer?.Dispose());
        }
        Release(() => _transfer?.Dispose());
        Release(() => _stream?.Dispose());
        if (_registered)
        {
            _backend.ReleaseProgram();
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("CUDA cleanup failed; context must not be reused.", failures);
        }

        void Release(Action action)
        {
            try
            {
                action();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
    }
}
