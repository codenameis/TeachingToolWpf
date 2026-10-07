using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Cpu;

namespace IndustrialVision.Filters.Pipeline;

/// <summary>호스트가 선택 GPU 구현을 주입한다. CPU 배포 그래프에는 GPU 패키지가 없다.</summary>
public sealed class FilterEngine(IGpuBackend? gpu = null, string unavailableReason = "No GPU backend was supplied by the host.")
{
    public IReadOnlyList<BackendCapabilities> GetCapabilities()
    {
        var cpu = new BackendCapabilities(".NET CPU", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, [nameof(FlatField), nameof(Gaussian), nameof(Morphology), nameof(Threshold)]);
        return gpu is null ? [cpu] : [cpu, gpu.GetCapabilities()];
    }

    public CompiledPlan Compile(Recipe recipe, ImageDescriptor input, ExecutionOptions options)
    {
        var output = RecipeValidation.Validate(recipe, input, options);
        var count = checked((int)input.PixelCount);
        var sharedBytes = checked(RecipeValidation.CalibrationBytes(recipe) + output.RequiredBytes + output.PixelCount);
        var reason = "CpuOnly was requested.";
        var useGpu = false;
        if (options.Mode == ExecutionMode.CpuGpu)
        {
            reason = gpu is null ? unavailableReason : gpu.GetUnsupportedReason(recipe, input, options) ?? "";
            if (reason.Length == 0 && PooledBuffer<float>.EstimateBytes(count) + sharedBytes > options.MemoryBudgetBytes)
            {
                reason = "GPU host staging + calibration + output exceed host memory budget.";
            }
            useGpu = reason.Length == 0;
            if (!useGpu && options.Fallback == FallbackPolicy.Throw)
            {
                throw new NotSupportedException(reason);
            }
            if (useGpu && options.GpuPlacement == GpuPlacement.Conservative && recipe.Filters.Count == 1)
            {
                useGpu = false;
                reason = "Conservative placement: one host-input stage stays on CPU; no measured end-to-end crossover model is installed. PreferGpu enables an explicit CUDA comparison.";
            }
        }
        var internalOptions = options with
        {
            MemoryBudgetBytes = options.MemoryBudgetBytes - sharedBytes
        };
        if (internalOptions.MemoryBudgetBytes <= 0)
        {
            throw new MemoryBudgetException("Calibration and output exceed host budget; use tiles.");
        }
        var hostBytes = checked(sharedBytes + (useGpu ? PooledBuffer<float>.EstimateBytes(count) : CpuProgram.EstimateBytes(recipe, input, options)));
        if (hostBytes > options.MemoryBudgetBytes)
        {
            throw new MemoryBudgetException($"Host workspace {hostBytes} exceeds budget {options.MemoryBudgetBytes}; use tiles.");
        }
        var stages = recipe.Filters.Select(f => new StagePlan(f.GetType().Name,
            useGpu ? gpu!.GetCapabilities().Device : "CPU",
            useGpu ? "Requested CUDA; one continuous resident segment. No performance crossover model is asserted." : reason)).ToArray();
        if (useGpu)
        {
            try
            {
                return new(input, output, recipe.Version, RecipeValidation.RequiredHalo(recipe), stages, hostBytes, gpu!.Compile(recipe, input, internalOptions));
            }
            catch (GpuUnavailableException failure) when (options.Fallback == FallbackPolicy.AllowAndReport)
            {
                return new FilterEngine(unavailableReason: failure.Message).Compile(recipe, input, options);
            }
        }
        return new(input, output, recipe.Version, RecipeValidation.RequiredHalo(recipe), stages, hostBytes, new CpuProgram(recipe, input, internalOptions));
    }
}
