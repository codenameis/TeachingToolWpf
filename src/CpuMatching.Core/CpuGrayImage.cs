using VisionKernels;

namespace CpuMatching.Core;

/// <summary>연속 Gray8 영상. 생성 시 복사하며 공개 픽셀은 읽기 전용이다.</summary>
public sealed class CpuGrayImage
{
    private readonly byte[] _pixels;

    public CpuGrayImage(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (width < 4 || height < 4 || (long)width * height > 80_000_000 || pixels.Length != (long)width * height)
        {
            throw new ArgumentException("Gray8 dimensions/buffer are invalid (4px minimum, 80Mpx maximum).");
        }
        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Pixels => _pixels;

    public byte this[int x, int y] => _pixels[(y * Width) + x];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public double Sample(double x, double y)
        => BilinearSampler.SampleClampedGray8(_pixels, Width, Height, x, y);

    public CpuGrayImage Crop(CpuSearchRegion region)
    {
        region.Validate(this);
        if (region.X == 0 && region.Y == 0 && region.Width == Width && region.Height == Height)
        {
            return this;
        }
        var pixels = new byte[checked(region.Width * region.Height)];
        for (var y = 0; y < region.Height; y++)
        {
            _pixels.AsSpan(((region.Y + y) * Width) + region.X, region.Width).CopyTo(pixels.AsSpan(y * region.Width));
        }
        return new CpuGrayImage(region.Width, region.Height, pixels);
    }

    public CpuGrayImage Half()
    {
        var width = Width / 2;
        var height = Height / 2;
        var pixels = new byte[width * height];
        GaussianOctaveReducer.DownsampleBox2x2(_pixels, Width, Height, Width, pixels, width, height);
        return new CpuGrayImage(width, height, pixels);
    }
}
