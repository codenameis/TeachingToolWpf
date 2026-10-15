namespace CpuMatching.Core;

/// <summary>연속 Gray8 영상. 생성 시 복사하며 공개 픽셀은 읽기 전용이다.</summary>
public sealed class GrayImage
{
    private readonly byte[] _pixels;

    public GrayImage(int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (width < 4 || height < 4 || (long)width * height > 80_000_000 || pixels.Length != (long)width * height)
        {
            throw new ArgumentException("Gray8 dimensions/buffer are invalid (4px minimum, 80Mpx maximum).");
        }
        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
    }

    public int Width
    {
        get;
    }

    public int Height
    {
        get;
    }

    public ReadOnlyMemory<byte> Pixels => _pixels;

    public byte this[int x, int y] => _pixels[(y * Width) + x];

    public double Sample(double x, double y)
    {
        if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1)
        {
            return 0;
        }
        var ix = (int)x;
        var iy = (int)y;
        var nx = Math.Min(ix + 1, Width - 1);
        var ny = Math.Min(iy + 1, Height - 1);
        var dx = x - ix;
        var dy = y - iy;
        return ((1 - dy) * (((1 - dx) * this[ix, iy]) + (dx * this[nx, iy]))) +
            (dy * (((1 - dx) * this[ix, ny]) + (dx * this[nx, ny])));
    }

    public GrayImage Crop(SearchRegion region)
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
        return new GrayImage(region.Width, region.Height, pixels);
    }

    public GrayImage Half()
    {
        var width = Width / 2;
        var height = Height / 2;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)((this[x * 2, y * 2] + this[(x * 2) + 1, y * 2] +
                    this[x * 2, (y * 2) + 1] + this[(x * 2) + 1, (y * 2) + 1] + 2) / 4);
            }
        }
        return new GrayImage(width, height, pixels);
    }
}
