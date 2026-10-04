using System.Buffers;
using global::ILGPU;
using global::ILGPU.Runtime;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionCompute.ILGPU;

/// <summary>한 worker가 재사용하는 GPU 중간 영상과 마지막 gradient 평면의 소유자.</summary>
internal sealed class CudaGradientBuffers : IDisposable
{
    private byte[]? _upload;
    private bool _disposed;
    public int Capacity
    {
        get;
    }
    public MemoryBuffer1D<byte, Stride1D.Dense> Input
    {
        get;
    }
    public MemoryBuffer1D<float, Stride1D.Dense> Horizontal
    {
        get;
    }
    public MemoryBuffer1D<float, Stride1D.Dense> Smooth
    {
        get;
    }
    public MemoryBuffer1D<float, Stride1D.Dense> X
    {
        get;
    }
    public MemoryBuffer1D<float, Stride1D.Dense> Y
    {
        get;
    }

    public CudaGradientBuffers(Accelerator accelerator, int capacity)
    {
        Capacity = capacity;
        try
        {
            _upload = ArrayPool<byte>.Shared.Rent(capacity);
            Input = accelerator.Allocate1D<byte>(capacity);
            Horizontal = accelerator.Allocate1D<float>(capacity);
            Smooth = accelerator.Allocate1D<float>(capacity);
            X = accelerator.Allocate1D<float>(capacity);
            Y = accelerator.Allocate1D<float>(capacity);
        }
        catch (Exception failure)
        {
            ResourceCleanup.Complete(failure, Dispose);
            throw;
        }
    }

    public void Upload(GrayImage image, AcceleratorStream stream, CancellationToken token)
    {
        var pixels = image.Pixels.Span;
        var count = checked(image.Width * image.Height);
        for (var y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            pixels.Slice(y * image.Stride, image.Width).CopyTo(_upload.AsSpan(y * image.Width, image.Width));
        }
        token.ThrowIfCancellationRequested();
        Input.View.SubView(0, count).CopyFromCPU(stream, _upload.AsSpan(0, count));
    }

    public GradientPlanes Download(int count, AcceleratorStream stream, float minimumGradient, CancellationToken token)
    {
        var x = new float[count];
        var y = new float[count];
        X.View.SubView(0, count).CopyToCPU(stream, x.AsSpan());
        Y.View.SubView(0, count).CopyToCPU(stream, y.AsSpan());
        return GradientPlanes.FromSobelDerivatives(x, y, minimumGradient, token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var upload = _upload;
        _upload = null;
        ResourceCleanup.Complete(null,
            () => Y?.Dispose(), () => X?.Dispose(), () => Smooth?.Dispose(),
            () => Horizontal?.Dispose(), () => Input?.Dispose(), () => ReturnUpload(upload));
    }

    private static void ReturnUpload(byte[]? upload)
    {
        if (upload is not null)
        {
            ArrayPool<byte>.Shared.Return(upload);
        }
    }
}
