using CpuMatching.Core;

namespace CpuMatching.Cuda;

/// <summary>앱/CLI가 소유한다. CPU 사용 중에는 CUDA를 만들지 않고, 검사 종료 뒤에만 자원을 해제한다.</summary>
public sealed class CudaTemplateScoreSession : IDisposable
{
    private readonly object _gate = new();
    private CudaTemplateScoreBackend? _backend;
    private Lease? _active;
    private bool _disposed;
    private bool _faulted;

    public ICoarseScoreAccelerator Rent()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null || _faulted)
            {
                throw new InvalidOperationException("CUDA 검색이 이미 진행 중이거나 장치 오류가 발생했습니다.");
            }
            var firstSearch = _backend is null;
            _backend ??= new CudaTemplateScoreBackend();
            _backend.BeginSearch(firstSearch);
            return _active = new Lease(this, _backend.Diagnostics);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_active is null)
            {
                DisposeBackend();
            }
        }
    }

    private void Execute(Lease lease, Action<CudaTemplateScoreBackend> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!ReferenceEquals(lease, _active), lease);
            if (_faulted)
            {
                throw new InvalidOperationException("이전 CUDA 오류로 실행기를 재사용할 수 없습니다.");
            }
            try
            {
                operation(_backend!);
                lease.Diagnostics = _backend!.Diagnostics;
            }
            catch (Exception failure)
            {
                _faulted = failure is not OperationCanceledException and not ArgumentException;
                // 전송 도중 취소되면 GPU 맵이 일부만 바뀌었을 수 있으므로 다음 호출에서 다시 올린다.
                _backend!.EndSearch();
                try
                {
                    _backend!.Synchronize();
                }
                catch (Exception drainFailure)
                {
                    _faulted = true;
                    throw new AggregateException("CUDA 오류 후 작업 완료 확인도 실패했습니다.", failure, drainFailure);
                }
                throw;
            }
        }
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(lease, _active))
            {
                return;
            }
            _active = null;
            _backend!.EndSearch();
            if (_disposed || _faulted)
            {
                DisposeBackend();
            }
        }
    }

    private void DisposeBackend()
    {
        var backend = _backend;
        _backend = null;
        backend?.Dispose();
    }

    private sealed class Lease(CudaTemplateScoreSession owner, CoarseScoreDiagnostics diagnostics) : ICoarseScoreAccelerator
    {
        public CoarseScoreDiagnostics Diagnostics { get; set; } = diagnostics;

        public void ScoreDirections(float[][] maps, DirectionalSample[] points, int width, int height,
            DirectionalSampling sampling, double[] output, CancellationToken token) =>
            owner.Execute(this, backend => backend.ScoreDirections(maps, points, width, height, sampling, output, token));

        public void ScoreCorrelation(GrayImage image, CorrelationSample[] points, double templateEnergy,
            double[] output, CancellationToken token) =>
            owner.Execute(this, backend => backend.ScoreCorrelation(image, points, templateEnergy, output, token));

        public void Dispose() => owner.Release(this);
    }
}
