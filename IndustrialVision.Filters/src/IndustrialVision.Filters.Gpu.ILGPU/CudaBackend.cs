using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;
using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Gpu;

public sealed class CudaBackend : IGpuBackend, IDisposable
{
    private readonly Context _context;
    private readonly CudaAccelerator _accelerator;
    private int _programs;
    private bool _disposed;
    private readonly object _gate = new();

    internal Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, KernelSettings> Correct
    {
        get;
    }
    internal Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, KernelSettings> Convolve
    {
        get;
    }
    internal Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, KernelSettings> Extrema
    {
        get;
    }
    internal Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, KernelSettings> Subtract
    {
        get;
    }
    internal Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, KernelSettings> Threshold
    {
        get;
    }

    public CudaBackend()
    {
        _context = Context.Create(builder => builder.Cuda().Math(MathMode.Default));
        try
        {
            var devices = _context.GetCudaDevices();
            if (devices.Count == 0)
            {
                throw new GpuUnavailableException("No CUDA device is available.");
            }
            _accelerator = devices[0].CreateCudaAccelerator(_context);
            Correct = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, KernelSettings>(CudaKernels.Correct);
            Convolve = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, KernelSettings>(CudaKernels.Convolve);
            Extrema = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, KernelSettings>(CudaKernels.Extrema);
            Subtract = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, KernelSettings>(CudaKernels.Subtract);
            Threshold = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, KernelSettings>(CudaKernels.Threshold);
        }
        catch
        {
            _accelerator?.Dispose();
            _context.Dispose();
            throw;
        }
    }

    public BackendCapabilities GetCapabilities() => new("ILGPU 1.5.3 CUDA", _accelerator.Name,
        _accelerator.MemorySize, [nameof(FlatField), nameof(Gaussian), nameof(Morphology), nameof(Threshold)]);

    public string? GetUnsupportedReason(Recipe recipe, ImageDescriptor input, ExecutionOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gpuBytes = CudaProgram.EstimateBytes(recipe, input);
        return gpuBytes > Math.Min(options.GpuMemoryBudgetBytes, _accelerator.MemorySize)
            ? $"GPU workspace {gpuBytes} bytes exceeds configured/device budget; use smaller tiles or CPU."
            : null;
    }

    public IGpuProgram Compile(Recipe recipe, ImageDescriptor input, ExecutionOptions options)
    {
        RecipeValidation.Validate(recipe, input, options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var reason = GetUnsupportedReason(recipe, input, options);
            if (reason is not null)
            {
                throw new MemoryBudgetException(reason);
            }
            CudaProgram program;
            try
            {
                program = new CudaProgram(this, _accelerator, recipe, input, options);
            }
            catch (CudaException failure) when (failure.Error == nameof(CudaError.CUDA_ERROR_OUT_OF_MEMORY))
            {
                throw new GpuUnavailableException("CUDA allocation failed: device memory is exhausted by current workloads.", failure);
            }
            _programs++;
            return program;
        }
    }

    internal void ReleaseProgram()
    {
        lock (_gate)
        {
            _programs--;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_programs != 0)
            {
                throw new InvalidOperationException("Dispose compiled plans before their CUDA backend.");
            }
            _disposed = true;
            _accelerator.Dispose();
            _context.Dispose();
        }
    }
}
