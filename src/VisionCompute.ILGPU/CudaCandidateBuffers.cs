using global::ILGPU;
using global::ILGPU.Runtime;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionCompute.ILGPU;

internal sealed class CudaCandidateBuffers : IDisposable
{
    private bool _disposed;

    public MemoryBuffer1D<byte, Stride1D.Dense> Input
    {
        get;
    }
    public MemoryBuffer1D<byte, Stride1D.Dense> Horizontal
    {
        get;
    }
    public MemoryBuffer1D<byte, Stride1D.Dense> Responses
    {
        get;
    }
    public MemoryBuffer1D<CandidateScoreFeature, Stride1D.Dense> Features
    {
        get;
    }
    public MemoryBuffer1D<int, Stride1D.Dense> Offsets
    {
        get;
    }
    public MemoryBuffer1D<ushort, Stride1D.Dense> Scores
    {
        get;
    }

    public CudaCandidateBuffers(Accelerator accelerator, int pixels, int features, int templates, int scores)
    {
        try
        {
            Input = accelerator.Allocate1D<byte>(pixels);
            Horizontal = accelerator.Allocate1D<byte>(pixels);
            Responses = accelerator.Allocate1D<byte>(checked(pixels * 8));
            Features = accelerator.Allocate1D<CandidateScoreFeature>(features);
            Offsets = accelerator.Allocate1D<int>(templates + 1);
            Scores = accelerator.Allocate1D<ushort>(scores);
        }
        catch (Exception failure)
        {
            ResourceCleanup.Complete(failure, Dispose);
            throw;
        }
    }

    public bool Fits(int pixels, int features, int templates, int scores)
        => Input.Length >= pixels && Features.Length >= features && Offsets.Length > templates && Scores.Length >= scores;

    public void Upload(ShapeCandidateScoreRequest request, AcceleratorStream stream, CancellationToken token)
    {
        var features = new CandidateScoreFeature[request.Templates.Sum(template => template.Length)];
        var offsets = new int[request.Templates.Length + 1];
        for (var template = 0; template < request.Templates.Length; template++)
        {
            token.ThrowIfCancellationRequested();
            var start = offsets[template];
            request.Templates[template].AsSpan().CopyTo(features.AsSpan(start));
            offsets[template + 1] = start + request.Templates[template].Length;
        }
        token.ThrowIfCancellationRequested();
        Input.View.SubView(0, request.OrientationBits.Length).CopyFromCPU(stream, request.OrientationBits.Span);
        Features.View.SubView(0, features.Length).CopyFromCPU(stream, features.AsSpan());
        Offsets.View.SubView(0, offsets.Length).CopyFromCPU(stream, offsets.AsSpan());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ResourceCleanup.Complete(null, () => Scores?.Dispose(), () => Offsets?.Dispose(),
            () => Features?.Dispose(), () => Responses?.Dispose(), () => Horizontal?.Dispose(), () => Input?.Dispose());
    }
}
