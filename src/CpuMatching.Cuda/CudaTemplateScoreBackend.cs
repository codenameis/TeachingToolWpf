using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using CpuMatching.Core;
using global::ILGPU;
using global::ILGPU.Algorithms;
using global::ILGPU.Runtime;
using global::ILGPU.Runtime.Cuda;
using Grid = CpuMatching.Cuda.CudaTemplateScoreGrid;

namespace CpuMatching.Cuda;

/// <summary>Session의 gate 안에서만 사용한다. 맵은 한 검색에서 한 번 전송하고 버퍼는 다음 검색에 재사용한다.</summary>
internal sealed class CudaTemplateScoreBackend : IDisposable
{
    private const int SamplesPerLaunch = 8_388_608;
    private readonly Context _context;
    private readonly CudaAccelerator _accelerator;
    private readonly AcceleratorStream _stream;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<DirectionalSample>, ArrayView<double>, Grid, int> _directions;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<CorrelationSample>, ArrayView<double>, Grid, double> _correlation;
    private MemoryBuffer1D<float, Stride1D.Dense>? _maps;
    private MemoryBuffer1D<byte, Stride1D.Dense>? _image;
    private MemoryBuffer1D<DirectionalSample, Stride1D.Dense>? _directionsPoints;
    private MemoryBuffer1D<CorrelationSample, Stride1D.Dense>? _correlationPoints;
    private MemoryBuffer1D<double, Stride1D.Dense>? _scores;
    private object? _uploadedSource;

    public CoarseScoreDiagnostics Diagnostics
    {
        get; private set;
    }

    public CudaTemplateScoreBackend()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            _context = Context.Create(builder => builder.Cuda().EnableAlgorithms().Math(MathMode.Default));
            CudaDevice? device = null;
            foreach (var candidate in _context.GetCudaDevices())
            {
                if (device is null || candidate.MemorySize > device.MemorySize)
                {
                    device = candidate;
                }
            }
            if (device is null)
            {
                throw new NotSupportedException("CPU+CUDA 매칭을 실행할 NVIDIA CUDA 장치를 찾지 못했습니다. CPU 모드를 선택하세요.");
            }
            _accelerator = device.CreateCudaAccelerator(_context);
            _stream = _accelerator.CreateStream();
            _directions = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<DirectionalSample>, ArrayView<double>, Grid, int>(CudaTemplateScoreKernels.Directions);
            _correlation = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<CorrelationSample>, ArrayView<double>, Grid, double>(CudaTemplateScoreKernels.Correlation);
            Diagnostics = new(_accelerator.Name, timer.Elapsed.TotalMilliseconds, 0, 0, 0, 0, 0);
        }
        catch (Exception failure)
        {
            Cleanup(failure, () => _stream?.Dispose(), () => _accelerator?.Dispose(), () => _context?.Dispose());
            throw;
        }
    }

    public void BeginSearch(bool firstSearch)
    {
        _uploadedSource = null;
        Diagnostics = new(Diagnostics.Device, firstSearch ? Diagnostics.InitializationMilliseconds : 0, 0, 0, 0, 0, 0);
    }

    public void EndSearch() => _uploadedSource = null;

    public void ScoreDirections(float[][] maps, DirectionalSample[] points, int width, int height,
        DirectionalSampling sampling, double[] output, CancellationToken token)
    {
        ValidateGrid(width, height, output, token);
        if (maps.Length != 16 || maps.Any(map => map.Length != output.Length) || points.Length > 512 || !Enum.IsDefined(sampling) ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.Bin is < 0 or >= 16 || point.Next is < 0 or >= 16 ||
                !double.IsFinite(point.Fraction) || point.Fraction is < 0 or > 1 ||
                (sampling == DirectionalSampling.RoundedRow && (point.X != Math.Round(point.X) || point.Y != Math.Round(point.Y)))))
        {
            throw new ArgumentException("Invalid directional score grid.");
        }
        var timer = Stopwatch.StartNew();
        Ensure(ref _maps, checked(output.Length * 16));
        Ensure(ref _scores, output.Length);
        Ensure(ref _directionsPoints, points.Length);
        if (!ReferenceEquals(_uploadedSource, maps))
        {
            for (var bin = 0; bin < maps.Length; bin++)
            {
                token.ThrowIfCancellationRequested();
                _maps!.View.SubView(bin * output.Length, output.Length).CopyFromCPU(_stream, maps[bin].AsSpan());
            }
            _uploadedSource = maps;
        }
        _directionsPoints!.View.SubView(0, points.Length).CopyFromCPU(_stream, points.AsSpan());
        _stream.Synchronize();
        var uploadMs = timer.Elapsed.TotalMilliseconds;
        var left = 0;
        var right = width;
        if (sampling == DirectionalSampling.RoundedRow)
        {
            foreach (var point in points)
            {
                left = Math.Max(left, 1 - (int)point.X);
                right = Math.Min(right, width - 2 - (int)point.X);
            }
        }
        var vectorEnd = left + (Math.Max(0, right - left) / Vector<float>.Count * Vector<float>.Count);
        timer.Restart();
        var chunk = Math.Max(1, SamplesPerLaunch / Math.Max(1, points.Length));
        for (var offset = 0; offset < output.Length; offset += chunk)
        {
            token.ThrowIfCancellationRequested();
            _directions(_stream, Math.Min(chunk, output.Length - offset), _maps!.View.BaseView,
                _directionsPoints.View.SubView(0, points.Length).BaseView, _scores!.View.BaseView,
                new(width, height, offset, left, vectorEnd), sampling == DirectionalSampling.RoundedRow ? 1 : 0);
            _stream.Synchronize();
        }
        Complete(output, uploadMs, timer.Elapsed.TotalMilliseconds, token);
    }

    public void ScoreCorrelation(GrayImage image, CorrelationSample[] points, double templateEnergy, double[] output, CancellationToken token)
    {
        ValidateGrid(image.Width, image.Height, output, token);
        if (points.Length is < 1 or > 262_144 || !double.IsFinite(templateEnergy) || templateEnergy < 0 ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Value)))
        {
            throw new ArgumentException("Invalid correlation score grid.");
        }
        var timer = Stopwatch.StartNew();
        Ensure(ref _image, output.Length);
        Ensure(ref _scores, output.Length);
        Ensure(ref _correlationPoints, points.Length);
        if (!ReferenceEquals(_uploadedSource, image))
        {
            _image!.View.SubView(0, output.Length).CopyFromCPU(_stream, image.Pixels.Span);
            _uploadedSource = image;
        }
        _correlationPoints!.View.SubView(0, points.Length).CopyFromCPU(_stream, points.AsSpan());
        _stream.Synchronize();
        var uploadMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var chunk = Math.Max(1, SamplesPerLaunch / points.Length);
        for (var offset = 0; offset < output.Length; offset += chunk)
        {
            token.ThrowIfCancellationRequested();
            _correlation(_stream, Math.Min(chunk, output.Length - offset), _image!.View.BaseView,
                _correlationPoints.View.SubView(0, points.Length).BaseView, _scores!.View.BaseView,
                new(image.Width, image.Height, offset, 0, 0), templateEnergy);
            _stream.Synchronize();
        }
        Complete(output, uploadMs, timer.Elapsed.TotalMilliseconds, token);
    }

    public void Synchronize() => _stream.Synchronize();

    public void Dispose() => Cleanup(null,
        _stream.Synchronize, () => _scores?.Dispose(), () => _correlationPoints?.Dispose(),
        () => _directionsPoints?.Dispose(), () => _image?.Dispose(), () => _maps?.Dispose(),
        _stream.Dispose, _accelerator.Dispose, _context.Dispose);

    private void Complete(double[] output, double uploadMs, double computeMs, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        _scores!.View.SubView(0, output.Length).CopyToCPU(_stream, output.AsSpan());
        _stream.Synchronize();
        Diagnostics = Diagnostics with
        {
            Calls = Diagnostics.Calls + 1,
            Evaluations = Diagnostics.Evaluations + output.Length,
            UploadMilliseconds = Diagnostics.UploadMilliseconds + uploadMs,
            ComputeMilliseconds = Diagnostics.ComputeMilliseconds + computeMs,
            DownloadMilliseconds = Diagnostics.DownloadMilliseconds + timer.Elapsed.TotalMilliseconds
        };
        token.ThrowIfCancellationRequested();
    }

    private void Ensure<T>(ref MemoryBuffer1D<T, Stride1D.Dense>? buffer, int count) where T : unmanaged
    {
        if (buffer is not null && buffer.Length >= Math.Max(1, count))
        {
            return;
        }
        var previous = buffer;
        buffer = null;
        previous?.Dispose();
        buffer = _accelerator.Allocate1D<T>(Math.Max(1, count));
    }

    private static void ValidateGrid(int width, int height, double[] output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (width < 4 || height < 4 || (long)width * height > PyramidSearch.MaximumSearchPixels || output.Length != (long)width * height)
        {
            throw new ArgumentException("CUDA matching grid must be <=8192x8192 pixels with a matching output buffer.");
        }
    }

    private static void Cleanup(Exception? originalFailure, params Action[] actions)
    {
        List<Exception> failures = originalFailure is null ? [] : [originalFailure];
        foreach (var action in actions)
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
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("CUDA 자원 정리 중 오류가 발생했습니다.", failures);
        }
    }
}
