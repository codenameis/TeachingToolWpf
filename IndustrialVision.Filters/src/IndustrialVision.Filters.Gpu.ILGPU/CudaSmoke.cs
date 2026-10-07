using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace IndustrialVision.Filters.Gpu;

public sealed record SmokeResult(string Status, string Device, string Backend, long MemoryBytes, int ComparedValues);

public static class CudaSmoke
{
    public static SmokeResult Run()
    {
        using var context = Context.Create(builder => builder.Cuda().Math(MathMode.Default));
        var devices = context.GetCudaDevices();
        if (devices.Count == 0)
        {
            return new("SKIP: no CUDA device", "none", "CUDA", 0, 0);
        }
        using var accelerator = devices[0].CreateCudaAccelerator(context);
        using var stream = accelerator.CreateStream();
        using var gray = accelerator.Allocate1D<ushort>(4096);
        using var real = accelerator.Allocate1D<float>(4096);
        using var output = accelerator.Allocate1D<float>(4096);
        var input16 = new ushort[4096];
        var input32 = new float[4096];
        for (var i = 0; i < input16.Length; i++)
        {
            input16[i] = (ushort)(65535 - i);
            input32[i] = i * 0.25f;
        }
        var kernel = accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<ushort>, ArrayView<float>, ArrayView<float>>(Add);
        gray.View.CopyFromCPU(stream, input16);
        real.View.CopyFromCPU(stream, input32);
        kernel(stream, input16.Length, gray.View.BaseView, real.View.BaseView, output.View.BaseView);
        stream.Synchronize();
        var actual = new float[4096];
        output.View.CopyToCPU(stream, actual);
        stream.Synchronize();
        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i] != input16[i] + input32[i])
            {
                throw new InvalidOperationException($"CUDA smoke mismatch at {i}.");
            }
        }
        return new("PASS", accelerator.Name, "CUDA", accelerator.MemorySize, actual.Length);
    }

    private static void Add(Index1D i, ArrayView<ushort> gray, ArrayView<float> real, ArrayView<float> output)
    {
        output[i] = gray[i] + real[i];
    }
}
