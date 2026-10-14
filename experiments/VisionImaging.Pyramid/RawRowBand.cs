using System.Buffers;

namespace VisionImaging.Pyramid;

/// <summary>축소 worker가 공유하는 불변 행 묶음. 모든 worker 종료 후에만 다음 묶음을 읽는다.</summary>
internal sealed class RawRowBand : IDisposable
{
    private readonly FileStream _file;
    private readonly byte[] _buffer;
    private int _top;
    private int _rows;
    private bool _disposed;
    public int Stride { get; }
    public int BufferBytes => _buffer.Length;

    public RawRowBand(string path, int stride, int height, int capacityRows)
    {
        Stride = stride;
        if (new FileInfo(path).Length != checked((long)stride * height))
        {
            throw new InvalidDataException("원본 캐시 길이가 규격과 다릅니다.");
        }
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        try
        {
            _buffer = ArrayPool<byte>.Shared.Rent(checked(stride * capacityRows));
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public void Load(int top, int rows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _file.Position = checked((long)top * Stride);
        _file.ReadExactly(_buffer.AsSpan(0, checked(rows * Stride)));
        _top = top;
        _rows = rows;
    }

    public ReadOnlySpan<byte> Row(int y)
    {
        if ((uint)(y - _top) >= _rows)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
        return _buffer.AsSpan((y - _top) * Stride, Stride);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _file.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
    }
}
