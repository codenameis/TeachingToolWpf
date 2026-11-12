using System.Buffers.Binary;

namespace IndustrialVision.Filters.Core;

public sealed record ImageDescriptor
{
    public int Width
    {
        get;
    }
    public int Height
    {
        get;
    }
    public int StrideBytes
    {
        get;
    }
    public PixelFormat Format
    {
        get;
    }
    public int ValidBits
    {
        get;
    }
    public long OriginX
    {
        get;
    }
    public long OriginY
    {
        get;
    }
    public double? PixelSizeMicrometers
    {
        get;
    }
    public string ExposureKey
    {
        get;
    }
    public int BytesPerPixel => Format == PixelFormat.Gray8 ? 1 : Format == PixelFormat.Gray16 ? 2 : 4;
    public long RequiredBytes => checked((((long)Height - 1) * StrideBytes) + ((long)Width * BytesPerPixel));
    public long PixelCount => (long)Width * Height;

    public ImageDescriptor(int width, int height, PixelFormat format, int strideBytes = 0,
        int validBits = 0, long originX = 0, long originY = 0, string exposureKey = "unspecified",
        double? pixelSizeMicrometers = null)
    {
        if (width <= 0 || height <= 0 || !Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        Width = width;
        Height = height;
        Format = format;
        var rowBytes = checked(width * BytesPerPixel);
        StrideBytes = strideBytes == 0 ? rowBytes : strideBytes;
        ValidBits = validBits == 0 ? BytesPerPixel * 8 : validBits;
        if (StrideBytes < rowBytes || ValidBits < 1 || ValidBits > BytesPerPixel * 8 ||
            (format == PixelFormat.Float32 && ValidBits != 32))
        {
            throw new ArgumentException("Positive stride and valid bit depth are required.");
        }
        if (pixelSizeMicrometers is { } size && (!double.IsFinite(size) || size <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSizeMicrometers));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(exposureKey);
        OriginX = originX;
        OriginY = originY;
        _ = checked(originX + width);
        _ = checked(originY + height);
        ExposureKey = exposureKey;
        PixelSizeMicrometers = pixelSizeMicrometers;
    }

    public ImageDescriptor WithFormat(PixelFormat format) => new(Width, Height, format,
        originX: OriginX, originY: OriginY, exposureKey: ExposureKey, pixelSizeMicrometers: PixelSizeMicrometers);

    public ImageDescriptor Region(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > Width || (long)y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        return new(width, height, Format, StrideBytes, ValidBits, checked(OriginX + x),
            checked(OriginY + y), ExposureKey, PixelSizeMicrometers);
    }
}
