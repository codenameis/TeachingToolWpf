using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>검사 실행이 공급자 하나를 소유하며 병렬 CPU 작업에서도 장치 호출과 해제를 직렬화한다.</summary>
internal sealed class SerializedImagePreprocessor(IImagePreprocessor inner) : IImagePreprocessor, IShapeCandidateScorer
{
    private readonly object _gate = new();
    private bool _disposed;

    public IShapeCandidateScorer? CandidateScorer => inner is IShapeCandidateScorer ? this : null;

    public CandidateScoringDiagnostics CandidateDiagnostics
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return GetCandidateScorer().CandidateDiagnostics;
            }
        }
    }

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

    public ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return GetCandidateScorer().Score(request, cancellationToken);
        }
    }

    private IShapeCandidateScorer GetCandidateScorer()
        => inner as IShapeCandidateScorer ?? throw new InvalidOperationException("등록된 GPU 공급자는 후보 점수 계산을 제공하지 않습니다.");

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
