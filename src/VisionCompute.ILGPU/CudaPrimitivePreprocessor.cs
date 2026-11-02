using System.Diagnostics;
using global::ILGPU;
using global::ILGPU.Runtime;
using global::ILGPU.Runtime.Cuda;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace VisionCompute.ILGPU;

/// <summary>한 검사의 모든 창에서 CUDA 버퍼를 재사용한다. 윤곽/분류는 공통 CPU 경로다.</summary>
public sealed class CudaPrimitivePreprocessor : IPrimitivePreprocessor
{
    private readonly object _gate = new();
    private readonly Context _context;
    private readonly CudaAccelerator _accelerator;
    private readonly AcceleratorStream _stream;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<byte>, ArrayView<byte>, int, int> _median;
    private MemoryBuffer1D<byte, Stride1D.Dense>? _input;
    private MemoryBuffer1D<byte, Stride1D.Dense>? _output;
    private byte[]? _upload;
    private bool _disposed;
    private bool _faulted;
    private PrimitivePreprocessingDiagnostics _diagnostics;

    public PrimitivePreprocessingDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);

    public CudaPrimitivePreprocessor()
    {
        try
        {
            _context = Context.Create(builder => builder.Cuda());
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
                throw new NotSupportedException("도형 검출에 사용할 NVIDIA CUDA 장치가 없습니다. CPU 모드를 선택하세요.");
            }
            _accelerator = device.CreateCudaAccelerator(_context);
            _stream = _accelerator.CreateStream();
            _median = _accelerator.LoadAutoGroupedKernel<Index1D, ArrayView<byte>, ArrayView<byte>, int, int>(CudaPrimitiveKernels.Median);
            _diagnostics = new(_accelerator.Name, 0, 0, 0, 0, 0);
        }
        catch (Exception failure)
        {
            ResourceCleanup.Complete(failure, () => _stream?.Dispose(), () => _accelerator?.Dispose(), () => _context?.Dispose());
            throw;
        }
    }

    public GrayImage Median3x3(GrayImage image, CancellationToken token, Memory<byte> destination = default)
    {
        var count = checked(image.Width * image.Height);
        if (count > PrimitiveShapeDetector.MaximumPixels)
        {
            throw new ArgumentException("CUDA 도형 전처리 창은 최대 2 Mpx입니다.", nameof(image));
        }
        if (!destination.IsEmpty && destination.Length < count)
        {
            throw new ArgumentException("CUDA 도형 출력 버퍼가 입력 영상보다 작습니다.", nameof(destination));
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted)
            {
                throw new InvalidOperationException("이전 CUDA 오류가 발생한 도형 전처리기는 재사용할 수 없습니다.");
            }
            token.ThrowIfCancellationRequested();
            try
            {
                if (_input is null || _input.Length < count)
                {
                    var previousInput = _input;
                    var previousOutput = _output;
                    _input = null;
                    _output = null;
                    ResourceCleanup.Complete(null, () => previousInput?.Dispose(), () => previousOutput?.Dispose());
                    _input = _accelerator.Allocate1D<byte>(count);
                    _output = _accelerator.Allocate1D<byte>(count);
                }
                var timer = Stopwatch.StartNew();
                if (image.Stride == image.Width)
                {
                    // 검사 창은 보통 연속 Gray8이다. 원본을 다시 포장하지 않고 바로 전송한다.
                    _input!.View.SubView(0, count).CopyFromCPU(_stream, image.Pixels.Span[..count]);
                }
                else
                {
                    if (_upload is null || _upload.Length < count)
                    {
                        _upload = new byte[count];
                    }
                    for (var y = 0; y < image.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        image.Pixels.Span.Slice(y * image.Stride, image.Width).CopyTo(_upload.AsSpan(y * image.Width));
                    }
                    _input!.View.SubView(0, count).CopyFromCPU(_stream, _upload.AsSpan(0, count));
                }
                // ILGPU CopyFromCPU/CopyToCPU가 해당 stream의 완료를 기다린다.
                // 같은 stream을 바로 다시 Synchronize할 필요가 없다.
                var uploadMilliseconds = timer.Elapsed.TotalMilliseconds;
                token.ThrowIfCancellationRequested();
                timer.Restart();
                _median(_stream, count, _input.View.BaseView, _output!.View.BaseView, image.Width, image.Height);
                _stream.Synchronize();
                var kernelMilliseconds = timer.Elapsed.TotalMilliseconds;
                token.ThrowIfCancellationRequested();
                timer.Restart();
                var pixels = destination.IsEmpty ? new byte[count].AsMemory() : destination[..count];
                _output.View.SubView(0, count).CopyToCPU(_stream, pixels.Span);
                var previous = Diagnostics;
                Volatile.Write(ref _diagnostics, previous with
                {
                    Calls = previous.Calls + 1,
                    Pixels = previous.Pixels + count,
                    UploadMilliseconds = previous.UploadMilliseconds + uploadMilliseconds,
                    KernelMilliseconds = previous.KernelMilliseconds + kernelMilliseconds,
                    DownloadMilliseconds = previous.DownloadMilliseconds + timer.Elapsed.TotalMilliseconds
                });
                token.ThrowIfCancellationRequested();
                return new(pixels, image.Width, image.Height, image.Width, image.ExcludedPixels);
            }
            catch (Exception failure)
            {
                _faulted = failure is not OperationCanceledException;
                try
                {
                    _stream.Synchronize();
                }
                catch (Exception drainFailure)
                {
                    _faulted = true;
                    throw new AggregateException("CUDA 도형 작업과 완료 대기가 실패했습니다.", failure, drainFailure);
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
            ResourceCleanup.Complete(null, _stream.Synchronize, () => _output?.Dispose(), () => _input?.Dispose(),
                _stream.Dispose, _accelerator.Dispose, _context.Dispose);
        }
    }
}
