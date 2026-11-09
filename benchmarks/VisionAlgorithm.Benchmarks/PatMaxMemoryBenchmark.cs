// File: benchmarks/VisionAlgorithm.Benchmarks/PatMaxMemoryBenchmark.cs
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.Benchmarks;

/// <summary>
/// [MemoryDiagnoser] equivalent verification harness measuring PatMax Train() and Find() latency and exact GC heap allocations.
/// </summary>
public sealed class PatMaxMemoryBenchmark : IDisposable
{
    private const int TemplateWidth = 64;
    private const int TemplateHeight = 64;
    private const int TemplateStride = 64;
    private const int SceneWidth = 256;
    private const int SceneHeight = 256;
    private const int SceneStride = 256;
    private const byte BackgroundGray = 25;
    private const byte ForegroundGray = 225;

    private readonly byte[] _templateBuffer = new byte[TemplateHeight * TemplateStride];
    private readonly byte[] _sceneBuffer = new byte[SceneHeight * SceneStride];
    private readonly PatMaxTrainParams _trainParams;
    private readonly PatMaxSearchParams _searchParams;
    private readonly PatMaxModel _model;
    private readonly PatMaxEngine _engine = new();

    public PatMaxMemoryBenchmark()
    {
        RenderGeometricTarget(_templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, 32.0f, 32.0f, 0.0f);
        RenderGeometricTarget(_sceneBuffer, SceneWidth, SceneHeight, SceneStride, 134.35f, 118.65f, 0.06f);

        _trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 4,
            fineGranularity: 1,
            edgeThreshold: 14.0f,
            elasticityPixels: 2.5f);

        _searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.65f,
            angleStartRadians: -0.15f,
            angleExtentRadians: 0.30f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 12.0f,
            maxCandidates: 8,
            maxResults: 1);

        _model = PatMaxTrainer.Train(_templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in _trainParams);
    }

    public (double MeanMicroseconds, long AllocatedBytesPerFind, PatMaxResult LastResult) RunZeroAllocationVerification(int warmupIterations = 5, int measureIterations = 20)
    {
        Span<PatMaxResult> results = stackalloc PatMaxResult[2];
        for (var i = 0; i < warmupIterations; i++)
        {
            _engine.Find(_model, _sceneBuffer, SceneWidth, SceneHeight, SceneStride, in _searchParams, results);
        }

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < measureIterations; i++)
        {
            _engine.Find(_model, _sceneBuffer, SceneWidth, SceneHeight, SceneStride, in _searchParams, results);
        }

        sw.Stop();
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var meanUs = (sw.Elapsed.TotalMilliseconds * 1000.0) / measureIterations;
        var bytesPerCall = (allocAfter - allocBefore) / measureIterations;
        return (meanUs, bytesPerCall, results[0]);
    }

    private static void RenderGeometricTarget(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        float angleRadians)
    {
        var (sin, cos) = MathF.SinCos(-angleRadians);
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var wy = (y + 0.5f) - centerY;
            for (var x = 0; x < width; x++)
            {
                var wx = (x + 0.5f) - centerX;
                var lx = (wx * cos) - (wy * sin);
                var ly = (wx * sin) + (wy * cos);

                var covV = SmoothInterval(lx, -14.0f, -4.0f) * SmoothInterval(ly, -16.0f, 16.0f);
                var covH = SmoothInterval(lx, -14.0f, 14.0f) * SmoothInterval(ly, 6.0f, 16.0f);
                var cov = MathF.Max(covV, covH);

                var gray = BackgroundGray + ((ForegroundGray - BackgroundGray) * cov);
                buffer[rowOffset + x] = (byte)MathF.Round(gray);
            }
        }
    }

    private static float SmoothInterval(float coord, float min, float max)
        => Math.Clamp(coord - min + 0.5f, 0.0f, 1.0f) * Math.Clamp(max - coord + 0.5f, 0.0f, 1.0f);

    public void Dispose()
    {
        _engine.Dispose();
        _model.Dispose();
    }
}
