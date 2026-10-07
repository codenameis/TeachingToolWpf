using System.Diagnostics;
using System.Text.Json;
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Examples;
using IndustrialVision.Filters.Pipeline;
#if ENABLE_GPU
using IndustrialVision.Filters.Gpu;
#endif

if (args.Contains("--gpu-smoke"))
{
#if ENABLE_GPU
    Console.WriteLine(JsonSerializer.Serialize(CudaSmoke.Run()));
#else
    Console.WriteLine("SKIP: this build has no GPU assembly reference. Build with -p:EnableGpu=true.");
#endif
    return;
}

using var input = LoadInput(args);
var recipe = SyntheticExample.Recipe(input.Descriptor);
var startup = Stopwatch.StartNew();
using var cpu = new FilterEngine().Compile(recipe, input.Descriptor, new());
using var cpuOutput = new HostImage(cpu.Output);
var coldCpu = cpu.Execute(input.View, cpuOutput);
Console.WriteLine($"CPU compile + first execute: {startup.Elapsed.TotalMilliseconds:F3} ms");
var cpuReport = cpu.Execute(input.View, cpuOutput);
Console.WriteLine($"CpuOnly: {JsonSerializer.Serialize(cpuReport)}");

#if ENABLE_GPU
using var gpu = args.Contains("--cpu-only") || CudaSmoke.Run().Status != "PASS" ? null : new CudaBackend();
var engine = new FilterEngine(gpu);
#else
var engine = new FilterEngine(unavailableReason: "CPU-only sample build has no ILGPU dependency.");
#endif
using var accelerated = engine.Compile(recipe, input.Descriptor, new() { Mode = ExecutionMode.CpuGpu });
using var gpuOutput = new HostImage(accelerated.Output);
accelerated.Execute(input.View, gpuOutput);
var gpuReport = accelerated.Execute(input.View, gpuOutput);
var equal = cpuOutput.Data.Span.SequenceEqual(gpuOutput.Data.Span) && cpuOutput.Validity.Span.SequenceEqual(gpuOutput.Validity.Span);
Console.WriteLine($"CpuGpu: {JsonSerializer.Serialize(gpuReport)}");
Console.WriteLine($"Mask bit-exact comparison: {(equal ? "PASS" : "FAIL")}; checksum={SyntheticExample.Checksum(gpuOutput)}");
if (!equal)
{
    Environment.ExitCode = 1;
}

static HostImage LoadInput(string[] arguments)
{
    if (arguments.Length >= 2 && arguments[0] == "--pgm")
    {
        using var source = FileTileSource.OpenPgm(arguments[1]);
        var image = new HostImage(source.Descriptor);
        source.Read(new(0, 0, image.Descriptor.Width, image.Descriptor.Height), image, default);
        return image;
    }
    if (arguments.Length >= 7 && arguments[0] == "--raw")
    {
        var descriptor = new ImageDescriptor(int.Parse(arguments[2]), int.Parse(arguments[3]), PixelFormat.Gray16, validBits: int.Parse(arguments[4]));
        using var source = new FileTileSource(arguments[1], descriptor, bigEndian: arguments[5] == "be", leftAligned: arguments[6] == "left");
        var image = new HostImage(descriptor);
        source.Read(new(0, 0, descriptor.Width, descriptor.Height), image, default);
        return image;
    }
    return SyntheticExample.Create(256, 256, PixelFormat.Gray16);
}
