using System.IO;
using System.IO.MemoryMappedFiles;

namespace VisionImaging.Pyramid;

/// <summary>단일 소유 타일 reader의 제한 매핑 창. 반환 행은 다음 Row 호출 전까지 유효하다.</summary>
internal sealed unsafe class RawLevel : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly long _length;
    private MemoryMappedViewAccessor? _view;
    private byte* _pixels;
    private long _start;
    private long _end;
    private bool _disposed;
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public int Stride
    {
        get;
    }

    public RawLevel(string path, int width, int height, int bytesPerPixel)
    {
        Width = width;
        Height = height;
        Stride = checked(width * bytesPerPixel);
        _length = checked((long)Stride * height);
        if (new FileInfo(path).Length != _length)
        {
            throw new InvalidDataException("캐시의 픽셀 길이가 규격과 다릅니다.");
        }
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, _length, MemoryMappedFileAccess.Read);
    }

    private void Map(long offset)
    {
        ReleaseView();
        // Windows의64KiB 매핑 정렬과 Linux의 page 정렬을 모두 만족한다.
        var start = offset / 65536 * 65536;
        var length = Math.Min(_length - start, Math.Max(8L * 1024 * 1024, (long)Stride + 65536));
        var view = _file.CreateViewAccessor(start, length, MemoryMappedFileAccess.Read);
        try
        {
            byte* pixels = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pixels);
            _pixels = pixels + view.PointerOffset;
            _view = view;
            _start = start;
            _end = start + length;
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    public ReadOnlySpan<byte> Row(int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)y >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
        var offset = (long)y * Stride;
        if (_view is null || offset < _start || offset + Stride > _end)
        {
            Map(offset);
        }
        return new ReadOnlySpan<byte>(_pixels + offset - _start, Stride);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReleaseView();
        _file.Dispose();
    }

    private void ReleaseView()
    {
        if (_view is null)
        {
            return;
        }
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pixels = null;
        _view.Dispose();
        _view = null;
    }
}
