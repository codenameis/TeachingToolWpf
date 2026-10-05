using System.Diagnostics;
using global::ILGPU;
using global::ILGPU.Runtime;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionCompute.ILGPU;

/// <summary>소유자의 단일 stream/gate 안에서 방향 응답과 전체 템플릿 점수를 연속 제출한다.</summary>
internal sealed class CudaCandidateScoring : IDisposable
{
    private readonly Accelerator _accelerator;
    private readonly AcceleratorStream _stream;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<byte>, int, int, int> _horizontal;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>, int, int, int> _respond;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<CandidateScoreFeature>, ArrayView<int>,
        ArrayView<ushort>, int, int, int, int, int, int> _accumulate;
    private MemoryBuffer1D<byte, Stride1D.Dense>? _lookup;
    private CudaCandidateBuffers? _buffers;
    private CandidateScoringDiagnostics _diagnostics;

    public CandidateScoringDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);

    public CudaCandidateScoring(Accelerator accelerator, AcceleratorStream stream)
    {
        _accelerator = accelerator;
        _stream = stream;
        try
        {
            _horizontal = accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<byte>, int, int, int>(
                CudaCandidateKernels.SpreadHorizontal);
            _respond = accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<byte>, ArrayView<byte>,
                int, int, int>(CudaCandidateKernels.SpreadVerticalAndRespond);
            _accumulate = accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<CandidateScoreFeature>, ArrayView<int>,
                ArrayView<ushort>, int, int, int, int, int, int>(CudaCandidateKernels.Accumulate);
            _lookup = accelerator.Allocate1D<byte>(8 * 256);
            _lookup.View.CopyFromCPU(stream, BuildLookup().AsSpan());
            stream.Synchronize();
            _diagnostics = new("ILGPU CUDA: orientation spread + batched template scores", accelerator.Name, 0, 0, 0, 0);
        }
        catch (Exception failure)
        {
            ResourceCleanup.Complete(failure, stream.Synchronize, Dispose);
            throw;
        }
    }

    public ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var pixelCount = checked(request.Width * request.Height);
        var scoreCount = checked(request.GridWidth * request.GridHeight * request.Templates.Length);
        var featureCount = request.Templates.Sum(template => template.Length);
        if (_buffers is null || !_buffers.Fits(pixelCount, featureCount, request.Templates.Length, scoreCount))
        {
            var previous = _buffers;
            _buffers = null;
            previous?.Dispose();
            _buffers = new(_accelerator, pixelCount, featureCount, request.Templates.Length, scoreCount);
        }
        _buffers.Upload(request, _stream, token);
        token.ThrowIfCancellationRequested();
        _horizontal(_stream, pixelCount, _buffers.Input.View.BaseView, _buffers.Horizontal.View.BaseView,
            request.Width, request.Height, request.Radius);
        _respond(_stream, pixelCount, _buffers.Horizontal.View.BaseView, _buffers.Responses.View.BaseView,
            _lookup!.View.BaseView, request.Width, request.Height, request.Radius);
        _accumulate(_stream, scoreCount, _buffers.Responses.View.BaseView, _buffers.Features.View.BaseView,
            _buffers.Offsets.View.BaseView, _buffers.Scores.View.BaseView,
            request.Width, request.Height, request.Step, request.GridWidth, request.GridHeight, request.Templates.Length);
        var scores = new ushort[scoreCount];
        _buffers.Scores.View.SubView(0, scoreCount).CopyToCPU(_stream, scores.AsSpan());
        _stream.Synchronize();
        token.ThrowIfCancellationRequested();
        var previousDiagnostics = Diagnostics;
        Volatile.Write(ref _diagnostics, previousDiagnostics with
        {
            Calls = previousDiagnostics.Calls + 1,
            ProcessedPixels = previousDiagnostics.ProcessedPixels + pixelCount,
            TemplateCount = previousDiagnostics.TemplateCount + request.Templates.Length,
            TotalMilliseconds = previousDiagnostics.TotalMilliseconds + timer.Elapsed.TotalMilliseconds
        });
        return new(request.GridWidth, request.GridHeight, request.Templates.Length, scores);
    }

    public void Dispose()
    {
        var buffers = _buffers;
        _buffers = null;
        var lookup = _lookup;
        _lookup = null;
        ResourceCleanup.Complete(null, () => buffers?.Dispose(), () => lookup?.Dispose());
    }

    private static byte[] BuildLookup()
    {
        var result = new byte[8 * 256];
        for (var bin = 0; bin < 8; bin++)
        {
            for (var bits = 1; bits < 256; bits++)
            {
                var response = 0d;
                for (var other = 0; other < 8; other++)
                {
                    if ((bits & (1 << other)) != 0)
                    {
                        response = Math.Max(response, Math.Abs(Math.Cos((bin - other) * Math.PI / 8)));
                    }
                }
                result[(bin * 256) + bits] = (byte)Math.Round(response * 100);
            }
        }
        return result;
    }
}
