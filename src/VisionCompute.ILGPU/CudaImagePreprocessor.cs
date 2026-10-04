using System.Diagnostics;
using global::ILGPU;
using global::ILGPU.Algorithms;
using global::ILGPU.Runtime;
using global::ILGPU.Runtime.Cuda;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionCompute.ILGPU;

/// <summary>
/// CUDA 평활화와 Sobel 미분을 GPU에서 연속 계산한다. CPU accelerator로 대체하지 않으며
/// 정확한 정규화·NMS·방향 응답맵·후보 검색·점수는 VisionAlgorithm의 공통 CPU 경로를 유지한다.
/// </summary>
public sealed class CudaImagePreprocessor : IImagePreprocessor
{
    public const int MaximumPixels = 8_388_608;
    private readonly object _gate = new();
    private readonly Context _context;
    private readonly CudaAccelerator _accelerator;
    private readonly AcceleratorStream _stream;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<float>, int, int> _horizontal;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, int, int> _vertical;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int> _sobel;
    private CudaGradientBuffers? _buffers;
    private ImagePreprocessingDiagnostics _diagnostics;
    private bool _disposed;
    private bool _faulted;

    public ImagePreprocessingDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);

    public CudaImagePreprocessor()
    {
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
                throw new NotSupportedException("ILGPU CUDA 전처리를 실행할 NVIDIA CUDA 장치를 찾지 못했습니다.");
            }
            _accelerator = device.CreateCudaAccelerator(_context);
            _stream = _accelerator.CreateStream();
            _horizontal = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<float>, int, int>(CudaGradientKernels.Horizontal);
            _vertical = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, int, int>(CudaGradientKernels.Vertical);
            _sobel = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                int, int>(CudaGradientKernels.Sobel);
            _diagnostics = new("ILGPU CUDA: smoothing + Sobel; CPU normalization", _accelerator.Name, 0, 0, 0);
        }
        catch (Exception failure)
        {
            ResourceCleanup.Complete(failure,
                () => _stream?.Dispose(), () => _accelerator?.Dispose(), () => _context?.Dispose());
            throw;
        }
    }

    public GradientPlanes Prepare(GrayImage image, float minimumGradient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if ((long)image.Width * image.Height > MaximumPixels)
        {
            throw new ArgumentException("CUDA 전처리 영상이 8 Mpx 작업 예산을 초과했습니다.", nameof(image));
        }
        if (!float.IsFinite(minimumGradient) || minimumGradient <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGradient));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted)
            {
                throw new InvalidOperationException("이전 CUDA 오류로 전처리 실행기를 재사용할 수 없습니다.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var count = checked(image.Width * image.Height);
            try
            {
                if (_buffers is null || _buffers.Capacity < count)
                {
                    // 해제가 실패해도 부분 해제된 버퍼를 다음 호출에서 재사용하지 않는다.
                    var previousBuffers = _buffers;
                    _buffers = null;
                    previousBuffers?.Dispose();
                    _buffers = new(_accelerator, count);
                }
                _buffers.Upload(image, _stream, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _horizontal(_stream, count, _buffers.Input.View.BaseView, _buffers.Horizontal.View.BaseView, image.Width, image.Height);
                _vertical(_stream, count, _buffers.Horizontal.View.BaseView, _buffers.Smooth.View.BaseView, image.Width, image.Height);
                _sobel(_stream, count, _buffers.Smooth.View.BaseView, _buffers.X.View.BaseView, _buffers.Y.View.BaseView,
                    image.Width, image.Height);
                _stream.Synchronize();
                var result = _buffers.Download(count, _stream, minimumGradient, cancellationToken);
                var previous = Diagnostics;
                Volatile.Write(ref _diagnostics, previous with
                {
                    Calls = previous.Calls + 1,
                    ProcessedPixels = previous.ProcessedPixels + count,
                    TotalMilliseconds = previous.TotalMilliseconds + timer.Elapsed.TotalMilliseconds
                });
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception failure)
            {
                _faulted = failure is not OperationCanceledException;
                // 마지막 성공 제출이 있다면 버퍼를 돌려주기 전에 완료/장치 오류를 확인한다.
                try
                {
                    _stream.Synchronize();
                }
                catch (Exception drainFailure)
                {
                    _faulted = true;
                    throw new AggregateException("CUDA 전처리 실패 후 stream 완료 확인도 실패했습니다.", failure, drainFailure);
                }
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
            var buffers = _buffers;
            _buffers = null;
            ResourceCleanup.Complete(null, _stream.Synchronize,
                () => buffers?.Dispose(), _stream.Dispose, _accelerator.Dispose, _context.Dispose);
        }
    }
}
