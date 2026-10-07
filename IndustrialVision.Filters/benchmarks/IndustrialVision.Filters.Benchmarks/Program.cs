using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Examples;
using IndustrialVision.Filters.Gpu;
using IndustrialVision.Filters.Pipeline;

var suite = args.FirstOrDefault() ?? "Smoke";
var outputDirectory = args.Length > 1 ? args[1] : "results";
Directory.CreateDirectory(outputDirectory);
var sizes = suite switch
{
    "Smoke" => new[] { 256, 1024 },
    "Standard" => [256, 1024, 4096],
    "Large" => [8192],
    _ => throw new ArgumentException("Suite must be Smoke, Standard or Large.")
};
var iterations = suite == "Large" ? 10 : 30;
var startup = Stopwatch.StartNew();
var smoke = args.Contains("--cpu-only") ? new SmokeResult("SKIP: CPU requested", "none", "CUDA", 0, 0) : CudaSmoke.Run();
using var gpu = smoke.Status == "PASS" ? new CudaBackend() : null;
var coldBackendMs = startup.Elapsed.TotalMilliseconds;
var results = new List<Measurement>();
foreach (var size in sizes)
{
    foreach (var format in Enum.GetValues<PixelFormat>())
    {
        using var input = SyntheticExample.Create(size, size, format);
        var pipeline = SyntheticExample.Recipe(input.Descriptor);
        var recipes = suite == "Large"
            ? new[] { ("Flat-Gaussian-BlackHat-Threshold", pipeline) }
            : new[]
            {
                ("Flat-Gaussian-BlackHat-Threshold", pipeline),
                ("FlatField", new Recipe(1, pipeline.Filters[0])),
                ("Gaussian5", new Recipe(1, new Gaussian(2, 1.1))),
                ("BlackHat7", new Recipe(1, new Morphology(MorphologyKind.BlackHat, 3))),
                ("Threshold", new Recipe(1, new Threshold(format == PixelFormat.Gray8 ? 128 : 32768)))
            };
        foreach (var (name, recipe) in recipes)
        {
            using var reference = new HostImage(input.Descriptor.WithFormat(recipe.Filters[^1] is Threshold ? PixelFormat.Gray8 : PixelFormat.Float32));
            foreach (var mode in Enum.GetValues<ExecutionMode>())
            {
                var options = new ExecutionOptions
                {
                    Mode = mode,
                    GpuPlacement = GpuPlacement.PreferGpu,
                    MemoryBudgetBytes = 3L * 1024 * 1024 * 1024,
                    GpuMemoryBudgetBytes = 3L * 1024 * 1024 * 1024
                };
                startup.Restart();
                using var plan = new FilterEngine(gpu).Compile(recipe, input.Descriptor, options);
                var compileMs = startup.Elapsed.TotalMilliseconds;
                using var output = new HostImage(plan.Output);
                var cold = plan.Execute(input.View, output);
                for (var i = 0; i < 3; i++)
                {
                    plan.Execute(input.View, output);
                }
                var times = new double[iterations];
                var allocations = new long[iterations];
                ExecutionReport report = cold;
                for (var i = 0; i < iterations; i++)
                {
                    var beforeAllocation = GC.GetTotalAllocatedBytes(true);
                    report = plan.Execute(input.View, output);
                    times[i] = report.Milliseconds;
                    allocations[i] = GC.GetTotalAllocatedBytes(true) - beforeAllocation;
                }
                var checksum = SyntheticExample.Checksum(output);
                var error = 0.0;
                if (mode == ExecutionMode.CpuOnly)
                {
                    output.Data.Span.CopyTo(reference.Data.Span);
                    output.Validity.Span.CopyTo(reference.Validity.Span);
                }
                else
                {
                    error = Compare(reference, output);
                }
                var measurement = Measure(name, size, format, mode.ToString(), "host-to-host", times,
                    compileMs, cold.Milliseconds, report, allocations, checksum, error);
                results.Add(measurement);
                Console.WriteLine($"{size} {format} {name} {mode}: p50={measurement.P50Ms:F3} p95={measurement.P95Ms:F3} ms; {plan.Stages[0].Device}");
                if (plan.UsesGpu)
                {
                    plan.UploadHostInput(input.View);
                    for (var i = 0; i < iterations; i++)
                    {
                        times[i] = plan.ExecuteDevice();
                    }
                    plan.CopyOutputToHost(output);
                    if (SyntheticExample.Checksum(output) != checksum)
                    {
                        throw new InvalidOperationException("Resident execution changed the output.");
                    }
                    results.Add(Measure(name, size, format, mode.ToString(), "device-resident-synchronized", times,
                        compileMs, cold.Milliseconds, report with
                        {
                            UploadBytes = 0,
                            DownloadBytes = 0
                        }, [], checksum, error));
                }
            }
            File.WriteAllText(Path.Combine(outputDirectory, $"{suite}.json"), JsonSerializer.Serialize(new
            {
                Suite = suite,
                Framework = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Processors = Environment.ProcessorCount,
                Device = smoke,
                ColdBackendMs = coldBackendMs,
                Samples = iterations,
                Warmups = 3,
                BinarySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(FilterEngine).Assembly.Location))),
                TieredCompilation = false,
                AllocationScope = "Process-wide managed bytes; concurrent runtime activity may be included.",
                WorkingMemoryScope = "Owned array payloads; excludes driver/JIT/pool retained/process overhead and borrowed input.",
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
var lines = new List<string> { "filter,size,format,mode,scope,samples,p50_ms,p95_ms,mpix_s,upload_bytes,download_bytes,host_bytes,gpu_bytes,allocated_p50,checksum,max_abs_error" };
foreach (var r in results)
{
    lines.Add(FormattableString.Invariant($"{r.Filter},{r.Size},{r.Format},{r.Mode},{r.Scope},{r.Samples},{r.P50Ms:F6},{r.P95Ms:F6},{r.MPixPerSecond:F3},{r.UploadBytes},{r.DownloadBytes},{r.HostBytes},{r.GpuBytes},{r.AllocatedP50},{r.Checksum},{r.MaximumAbsoluteError}"));
}
File.WriteAllLines(Path.Combine(outputDirectory, $"{suite}.csv"), lines);

static Measurement Measure(string filter, int size, PixelFormat format, string mode, string scope,
    double[] times, double compile, double cold, ExecutionReport report, long[] allocations, ulong checksum, double error)
{
    var sorted = times.Order().ToArray();
    var sortedAllocations = allocations.Order().ToArray();
    var p50 = sorted[(int)Math.Ceiling(sorted.Length * 0.5) - 1];
    var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
    return new(filter, size, format.ToString(), mode, scope, sorted.Length, compile, cold, p50, p95,
        size * (double)size / (p50 * 1000), report.UploadBytes, report.DownloadBytes, report.HostWorkingBytes,
        report.GpuWorkingBytes, sortedAllocations.Length == 0 ? null : sortedAllocations[sortedAllocations.Length / 2],
        checksum, error, report.Stages, times.ToArray());
}

static double Compare(HostImage expected, HostImage actual)
{
    if (!expected.Validity.Span.SequenceEqual(actual.Validity.Span))
    {
        throw new InvalidOperationException("CPU/GPU validity mismatch.");
    }
    if (expected.Descriptor.Format == PixelFormat.Gray8)
    {
        if (!expected.Data.Span.SequenceEqual(actual.Data.Span))
        {
            throw new InvalidOperationException("CPU/GPU mask is not bit-exact.");
        }
        return 0;
    }
    double maximum = 0;
    for (var i = 0; i < expected.Descriptor.PixelCount; i++)
    {
        var a = BinaryPrimitives.ReadSingleLittleEndian(expected.Data.Span.Slice(i * 4, 4));
        var b = BinaryPrimitives.ReadSingleLittleEndian(actual.Data.Span.Slice(i * 4, 4));
        var error = Math.Abs((double)a - b);
        if (!double.IsFinite(error) || error > 0.06 + (Math.Abs(a) * 0.000001))
        {
            throw new InvalidOperationException($"CPU/GPU numeric error at {i}: {a} vs {b}.");
        }
        maximum = Math.Max(maximum, error);
    }
    return maximum;
}

internal sealed record Measurement(string Filter, int Size, string Format, string Mode, string Scope, int Samples,
    double CompileMs, double ColdExecutionMs, double P50Ms, double P95Ms, double MPixPerSecond,
    long UploadBytes, long DownloadBytes, long HostBytes, long GpuBytes, long? AllocatedP50,
    ulong Checksum, double MaximumAbsoluteError, IReadOnlyList<StagePlan> Stages, double[] RawMilliseconds);
