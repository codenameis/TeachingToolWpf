using System.Diagnostics;
using System.Runtime.InteropServices;
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Pipeline;
using OpenCvSharp;
using VisionCanvas.Core.Editing;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

/// <summary>Single-worker preview adapter. The host owns the optional backend and must outlive this adapter.</summary>
internal sealed class IndustrialRoiFilter(Func<IGpuBackend>? createGpuBackend) : IDisposable
{
    private FilterEngine? _gpuEngine;
    private readonly FilterEngine _cpuEngine = new();
    private CompiledPlan? _plan;
    private HostImage? _output;
    private byte[]? _input;
    private FilterSettings? _settings;
    private ImageDescriptor? _descriptor;
    private long _version;

    public (byte[] Pixels, ExecutionReport Report, double PreparationMilliseconds, bool ReusedPlan) Apply(
        Mat pixels, PixelKind kind, FilterSettings settings, CancellationToken token)
    {
        settings.Validate();
        token.ThrowIfCancellationRequested();
        var preparation = Stopwatch.StartNew();
        using var gray = new Mat();
        var input = pixels;
        if (kind is PixelKind.Bgra32 or PixelKind.Rgba32)
        {
            // Preview luminance uses OpenCV's RGB weights, ignores alpha, and keeps native Gray16 untouched.
            Cv2.CvtColor(pixels, gray, kind == PixelKind.Rgba32 ? ColorConversionCodes.RGBA2GRAY : ColorConversionCodes.BGRA2GRAY);
            input = gray;
        }
        else if (kind is not (PixelKind.Gray8 or PixelKind.Gray16))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var descriptor = new ImageDescriptor(input.Width, input.Height,
            kind == PixelKind.Gray16 ? PixelFormat.Gray16 : PixelFormat.Gray8);
        var reused = _plan is not null && _settings == settings && _descriptor == descriptor;
        if (!reused)
        {
            DisposePlan();
            var engine = settings.Execution == RoiFilterExecution.CpuOnly ? _cpuEngine : GetGpuEngine();
            var options = new ExecutionOptions
            {
                Mode = settings.Execution == RoiFilterExecution.CpuOnly ? ExecutionMode.CpuOnly : ExecutionMode.CpuGpu,
                // Explicit user comparison, including transfer costs; this is not an automatic speed claim.
                GpuPlacement = GpuPlacement.PreferGpu,
                MemoryBudgetBytes = 128L * 1024 * 1024,
                GpuMemoryBudgetBytes = 256L * 1024 * 1024,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 16),
                Diagnostics = DiagnosticLevel.Stages
            };
            try
            {
                _plan = engine.Compile(CreateRecipe(settings, checked(++_version)), descriptor, options);
                _output = new HostImage(_plan.Output);
                _input = new byte[checked((int)descriptor.RequiredBytes)];
                _settings = settings;
                _descriptor = descriptor;
            }
            catch
            {
                DisposePlan();
                throw;
            }
        }
        for (var y = 0; y < descriptor.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(input.Ptr(y), _input!, y * descriptor.StrideBytes, descriptor.StrideBytes);
        }
        preparation.Stop();
        var report = _plan!.Execute(new ImageView(descriptor, _input!), _output!, token);
        var display = new byte[checked(descriptor.Width * descriptor.Height)];
        if (_plan.Output.Format == PixelFormat.Gray8)
        {
            _output!.Data.Span.CopyTo(display);
        }
        else
        {
            var values = MemoryMarshal.Cast<byte, float>(_output!.Data.Span);
            var scale = kind == PixelKind.Gray16 ? 1f / 257 : 1f;
            for (var i = 0; i < display.Length; i++)
            {
                display[i] = float.IsFinite(values[i]) ? (byte)Math.Clamp(MathF.Round(values[i] * scale, MidpointRounding.ToEven), 0, 255) : (byte)0;
            }
        }
        token.ThrowIfCancellationRequested();
        return (display, report, preparation.Elapsed.TotalMilliseconds, reused);
    }

    private FilterEngine GetGpuEngine()
    {
        if (_gpuEngine is null)
        {
            try
            {
                _gpuEngine = new FilterEngine(createGpuBackend?.Invoke());
            }
            catch (GpuUnavailableException ex)
            {
                _gpuEngine = new FilterEngine(unavailableReason: ex.Message);
            }
        }
        return _gpuEngine;
    }

    private static Recipe CreateRecipe(FilterSettings settings, long version) => settings.Kind switch
    {
        RoiFilterKind.Gaussian => new(version, new Gaussian(settings.Radius, settings.Sigma)),
        RoiFilterKind.NativeThreshold => new(version, new Threshold((float)settings.Threshold)),
        RoiFilterKind.GaussianBlackHatThreshold => new(version, new Gaussian(settings.Radius, settings.Sigma),
            new Morphology(MorphologyKind.BlackHat, settings.Radius), new Threshold((float)settings.Threshold)),
        RoiFilterKind.Erosion => new(version, new Morphology(MorphologyKind.Erosion, settings.Radius)),
        RoiFilterKind.Dilation => new(version, new Morphology(MorphologyKind.Dilation, settings.Radius)),
        RoiFilterKind.Opening => new(version, new Morphology(MorphologyKind.Opening, settings.Radius)),
        RoiFilterKind.Closing => new(version, new Morphology(MorphologyKind.Closing, settings.Radius)),
        RoiFilterKind.WhiteTopHat => new(version, new Morphology(MorphologyKind.WhiteTopHat, settings.Radius)),
        RoiFilterKind.BlackHat => new(version, new Morphology(MorphologyKind.BlackHat, settings.Radius)),
        _ => throw new ArgumentOutOfRangeException(nameof(settings))
    };

    private void DisposePlan()
    {
        _plan?.Dispose();
        _plan = null;
        _output?.Dispose();
        _output = null;
        _input = null;
    }

    public void Dispose() => DisposePlan();
}
