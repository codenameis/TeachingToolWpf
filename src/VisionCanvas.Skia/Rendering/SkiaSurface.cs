using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Skia.Rendering;

/// <summary>Skia가 완성한 불투명 프레임을 HWND로 복사한다. GDI는 도형이나 텍스트를 그리지 않는다.</summary>
internal sealed class SkiaSurface(nint window) : IRenderSurface, IDisposable
{
    private const uint SourceCopy = 0x00CC0020;

    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;

    public int FrameIndex => 0;

    public int Width => _bitmap?.Width ?? 0;

    public int Height => _bitmap?.Height ?? 0;

    public SKCanvas Begin(int width, int height)
    {
        if (width != Width || height != Height)
        {
            _canvas?.Dispose();
            _bitmap?.Dispose();
            _bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _canvas = new(_bitmap);
        }
        _canvas!.ResetMatrix();
        _canvas.Clear(new SKColor(24, 28, 36));
        return _canvas;
    }

    public void Present()
    {
        _canvas!.Flush();
        var context = GetDC(window);
        if (context == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var header = new BitmapHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(),
                Width = Width,
                Height = -Height,
                Planes = 1,
                BitCount = 32
            };
            var copied = StretchDIBits(context, 0, 0, Width, Height, 0, 0, Width, Height,
                _bitmap!.GetPixels(), ref header, 0, SourceCopy);
            if (copied == -1)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            ReleaseDC(window, context);
        }
    }

    public void Save(string path)
    {
        using var image = SKImage.FromBitmap(_bitmap!);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }

    // CPU 래스터는 동기적으로 끝나므로 이전 프레임의 자원을 즉시 반환할 수 있다.
    public void Retire(IDisposable resource) => resource.Dispose();

    public void Dispose()
    {
        _canvas?.Dispose();
        _bitmap?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int PixelsPerMeterX;
        public int PixelsPerMeterY;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint context);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(nint context, int x, int y, int width, int height,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight, nint pixels,
        ref BitmapHeader header, uint colorUse, uint operation);
}
