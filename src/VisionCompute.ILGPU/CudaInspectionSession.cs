using System.Diagnostics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionCompute.ILGPU;

/// <summary>
/// 앱 또는 CLI 세션이 CUDA 장치·커널·작업 버퍼를 소유한다. 검사는 단일 lease만 사용하며
/// 장치 준비 시간을 검사 실행과 별도로 기록한다. 전역 캐시나 원본/결과 캐시는 두지 않는다.
/// </summary>
public sealed class CudaInspectionSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<IImagePreprocessor> _createBackend;
    private IImagePreprocessor? _backend;
    private Task? _preparation;
    private Lease? _activeLease;
    private double _initializationMilliseconds;
    private bool _disposed;
    private bool _faulted;

    public CudaInspectionSession()
        : this(() => new CudaImagePreprocessor())
    {
    }

    internal CudaInspectionSession(Func<IImagePreprocessor> createBackend)
    {
        ArgumentNullException.ThrowIfNull(createBackend);
        _createBackend = createBackend;
    }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _backend is not null && !_disposed && !_faulted;
            }
        }
    }

    /// <summary>첫 장치 생성·커널 컴파일·방향 LUT 업로드의 wall time. 검사별 lease 시간에는 포함하지 않는다.</summary>
    public double InitializationMilliseconds
    {
        get
        {
            lock (_gate)
            {
                return _initializationMilliseconds;
            }
        }
    }

    /// <summary>
    /// 작업 스레드에서 한 번 준비한다. 취소는 대기를 끝내며 이미 시작한 native 초기화를 중단하지 않는다.
    /// 취소 후에도 세션을 해제하면 초기화가 완료되는 즉시 생성 자원을 해제한다.
    /// </summary>
    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task preparation;
        lock (_gate)
        {
            ValidateAvailable();
            if (_backend is not null)
            {
                return Task.CompletedTask;
            }
            preparation = _preparation ??= Task.Run(Initialize);
        }
        return cancellationToken.CanBeCanceled ? preparation.WaitAsync(cancellationToken) : preparation;
    }

    /// <summary>
    /// 검사에 필요한 두 중립 계약을 구현한 lease를 반환한다. 준비 전 호출은 장치 준비를 기다리므로
    /// UI 스레드에서 호출하지 않는다. lease 반환은 장치 소멸이 아니며 버퍼를 다음 검사에서 재사용한다.
    /// </summary>
    public IImagePreprocessor Rent()
    {
        PrepareAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            ValidateAvailable();
            if (_activeLease is not null)
            {
                throw new InvalidOperationException("CUDA 세션은 한 번에 하나의 검사 lease만 허용합니다.");
            }
            var lease = new Lease(this, _backend!);
            _activeLease = lease;
            return lease;
        }
    }

    public void Dispose()
    {
        IImagePreprocessor? backend = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_activeLease is null)
            {
                backend = _backend;
                _backend = null;
            }
        }
        // 초기화 또는 lease가 진행 중이면 해당 소유 경계가 마지막 자원 해제를 담당한다.
        backend?.Dispose();
    }

    private void Initialize()
    {
        var timer = Stopwatch.StartNew();
        IImagePreprocessor? created = null;
        try
        {
            created = _createBackend();
            if (created is not IShapeCandidateScorer)
            {
                throw new InvalidOperationException("CUDA 세션 공급자는 전처리와 후보 점수 계약을 모두 구현해야 합니다.");
            }
            lock (_gate)
            {
                _initializationMilliseconds = timer.Elapsed.TotalMilliseconds;
                ObjectDisposedException.ThrowIf(_disposed, this);
                _backend = created;
                created = null;
            }
        }
        catch (Exception failure)
        {
            lock (_gate)
            {
                _faulted = true;
                _initializationMilliseconds = timer.Elapsed.TotalMilliseconds;
            }
            ResourceCleanup.Complete(failure, () => created?.Dispose());
            throw;
        }
    }

    private void ValidateAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("이전 CUDA 오류로 이 세션을 재사용할 수 없습니다. 세션을 해제하고 다시 준비하십시오.");
        }
    }

    private void MarkFault(Lease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeLease, lease))
            {
                throw new InvalidOperationException("현재 CUDA 세션이 소유하지 않는 검사 lease입니다.");
            }
            _faulted = true;
        }
    }

    private void Return(Lease lease)
    {
        IImagePreprocessor? backend = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_activeLease, lease))
            {
                throw new InvalidOperationException("현재 CUDA 세션이 소유하지 않는 검사 lease를 반환했습니다.");
            }
            _activeLease = null;
            if (_disposed || _faulted)
            {
                backend = _backend;
                _backend = null;
            }
        }
        backend?.Dispose();
    }

    private sealed class Lease : IImagePreprocessor, IShapeCandidateScorer
    {
        private readonly object _gate = new();
        private readonly CudaInspectionSession _owner;
        private readonly IImagePreprocessor _backend;
        private readonly IShapeCandidateScorer _scorer;
        private readonly ImagePreprocessingDiagnostics _preprocessingStart;
        private readonly CandidateScoringDiagnostics _scoringStart;
        private bool _disposed;
        private bool _faulted;

        public Lease(CudaInspectionSession owner, IImagePreprocessor backend)
        {
            _owner = owner;
            _backend = backend;
            _scorer = (IShapeCandidateScorer)backend;
            _preprocessingStart = backend.Diagnostics;
            _scoringStart = _scorer.CandidateDiagnostics;
        }

        public ImagePreprocessingDiagnostics Diagnostics
        {
            get
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var current = _backend.Diagnostics;
                    return current with
                    {
                        Calls = current.Calls - _preprocessingStart.Calls,
                        ProcessedPixels = current.ProcessedPixels - _preprocessingStart.ProcessedPixels,
                        TotalMilliseconds = current.TotalMilliseconds - _preprocessingStart.TotalMilliseconds
                    };
                }
            }
        }

        public CandidateScoringDiagnostics CandidateDiagnostics
        {
            get
            {
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var current = _scorer.CandidateDiagnostics;
                    return current with
                    {
                        Calls = current.Calls - _scoringStart.Calls,
                        ProcessedPixels = current.ProcessedPixels - _scoringStart.ProcessedPixels,
                        TemplateCount = current.TemplateCount - _scoringStart.TemplateCount,
                        TotalMilliseconds = current.TotalMilliseconds - _scoringStart.TotalMilliseconds
                    };
                }
            }
        }

        public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ValidateAvailable();
                try
                {
                    return _backend.Prepare(image, minimumGradient, cancellationToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException and not ArgumentException)
                {
                    _faulted = true;
                    _owner.MarkFault(this);
                    throw;
                }
            }
        }

        public ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                ValidateAvailable();
                try
                {
                    return _scorer.Score(request, cancellationToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException and not ArgumentException)
                {
                    _faulted = true;
                    _owner.MarkFault(this);
                    throw;
                }
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
                _owner.Return(this);
            }
        }

        private void ValidateAvailable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted)
            {
                throw new InvalidOperationException("이전 CUDA 오류로 이 검사 lease를 재사용할 수 없습니다.");
            }
        }
    }
}
