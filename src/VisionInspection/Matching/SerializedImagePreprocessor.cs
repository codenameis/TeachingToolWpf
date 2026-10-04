using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>검사 실행이 공급자 하나를 소유하며 병렬 CPU 작업에서도 장치 호출과 해제를 직렬화한다.</summary>
internal sealed class SerializedImagePreprocessor(IImagePreprocessor inner) : IImagePreprocessor
{
    private readonly object _gate = new();
    private bool _disposed;

    public ImagePreprocessingDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return inner.Diagnostics;
            }
        }
    }

    public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Prepare(image, minimumGradient, cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            inner.Dispose();
        }
    }
}
