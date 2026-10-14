using VisionImaging.Abstractions;
using VisionImaging.Pyramid;

namespace VisionImaging.Codecs;

/// <summary>코덱과 native 수명을 격리하고 연속 원본 행을 호출자 버퍼에 기록한다.</summary>
public sealed class NativeRasterDecoder(long memoryBudgetBytes = 512L * 1024 * 1024) : IRasterDecoder
{
    public string CacheIdentity => "native-codecs-v1:tiff4.7.2-png1.6.59-jpeg3.2.0-zlib1.3.2";

    public unsafe IRasterReader Open(string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryBudgetBytes, 1024);
        token.ThrowIfCancellationRequested();
        if (!Environment.Is64BitProcess || !BitConverter.IsLittleEndian || NativeMethods.AbiVersion() != 1)
        {
            throw new PlatformNotSupportedException("코덱은 x64 little-endian과 ABI1이 필요합니다.");
        }
        var cancelled = 0;
        using var registration = Register(token, &cancelled);
        var status = NativeMethods.Open(Path.GetFullPath(path), (ulong)memoryBudgetBytes, &cancelled, out var pointer, out var info, out var error);
        Check(status, error, token);
        var handle = new ReaderHandle(pointer);
        try
        {
            token.ThrowIfCancellationRequested();
            if (info.Width <= 0 || info.Height <= 0 || info.Kind is < 0 or > 2)
            {
                throw new InvalidDataException("코덱이 잘못된 영상 규격을 반환했습니다.");
            }
            return new Reader(handle, info);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static unsafe CancellationTokenRegistration Register(CancellationToken token, int* flag) => token.UnsafeRegister(
        static state => Volatile.Write(ref *(int*)(nint)state!, 1), (nint)flag);

    private static void Check(int status, NativeMethods.Error error, CancellationToken token)
    {
        if (status == 0)
        {
            return;
        }
        token.ThrowIfCancellationRequested();
        throw status switch
        {
            2 => new NotSupportedException(error.Message),
            3 => new IOException(error.Message),
            5 => new InvalidOperationException($"코덱 메모리 예산 초과: {error.Message}"),
            _ => new InvalidDataException(error.Message)
        };
    }

    private sealed class Reader(ReaderHandle handle, NativeMethods.Info info) : IRasterReader
    {
        private int _nextRow;
        public int Width => info.Width;
        public int Height => info.Height;
        public PixelKind Kind => (PixelKind)info.Kind;

        public unsafe void ReadRows(int top, int height, Span<byte> destination, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            if (top != _nextRow || height <= 0 || height > Height - top)
            {
                throw new ArgumentOutOfRangeException(nameof(top), "처음부터 연속된 행만 읽을 수 있습니다.");
            }
            var length = checked(Width * (Kind == PixelKind.Gray8 ? 1 : Kind == PixelKind.Gray16 ? 2 : 4) * height);
            if (destination.Length < length)
            {
                throw new ArgumentException("출력 버퍼가 작습니다.", nameof(destination));
            }
            token.ThrowIfCancellationRequested();
            var cancelled = 0;
            using var registration = Register(token, &cancelled);
            fixed (byte* pixels = destination)
            {
                Check(NativeMethods.Read(handle, height, pixels, (ulong)length, &cancelled, out var error), error, token);
            }
            _nextRow += height;
            token.ThrowIfCancellationRequested();
        }

        public void Dispose() => handle.Dispose();
    }
}
