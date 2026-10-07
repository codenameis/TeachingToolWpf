using System.Buffers.Binary;

namespace IndustrialVision.Filters.Core;

public enum PixelFormat
{
    Gray8, Gray16, Float32
}

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

/// <summary>호출자가 소유하는 읽기 전용 뷰. 실행이 끝날 때까지 저장소를 변경/반환하지 않는다.</summary>
public sealed class ImageView
{
    public ImageDescriptor Descriptor
    {
        get;
    }
    public ReadOnlyMemory<byte> Data
    {
        get;
    }
    public ReadOnlyMemory<byte> Validity
    {
        get;
    }
    public int MaskStride
    {
        get;
    }

    public ImageView(ImageDescriptor descriptor, ReadOnlyMemory<byte> data,
        ReadOnlyMemory<byte> validity = default, int maskStride = 0)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        MaskStride = maskStride == 0 ? descriptor.Width : maskStride;
        if (descriptor.RequiredBytes > data.Length || MaskStride < descriptor.Width ||
            (!validity.IsEmpty && (((long)(descriptor.Height - 1) * MaskStride) + descriptor.Width > validity.Length)))
        {
            throw new ArgumentException("Image or validity buffer is too small.");
        }
        Data = data;
        Validity = validity;
    }

    public ImageView Region(int x, int y, int width, int height)
    {
        var region = Descriptor.Region(x, y, width, height);
        return new(region, Data.Slice(checked((y * Descriptor.StrideBytes) + (x * Descriptor.BytesPerPixel))),
            Validity.IsEmpty ? default : Validity.Slice(checked((y * MaskStride) + x)), MaskStride);
    }

    public void CopyToFloat(Span<float> target)
    {
        var d = Descriptor;
        if (target.Length < d.PixelCount)
        {
            throw new ArgumentException("Target is too small.");
        }
        for (var y = 0; y < d.Height; y++)
        {
            var row = Data.Span.Slice(y * d.StrideBytes, d.Width * d.BytesPerPixel);
            for (var x = 0; x < d.Width; x++)
            {
                var valid = Validity.IsEmpty || Validity.Span[(y * MaskStride) + x] != 0;
                float value = d.Format switch
                {
                    PixelFormat.Gray8 => row[x],
                    PixelFormat.Gray16 => BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(x * 2, 2)),
                    _ => BinaryPrimitives.ReadSingleLittleEndian(row.Slice(x * 4, 4))
                };
                if (valid && (!float.IsFinite(value) || (d.Format != PixelFormat.Float32 && value >= (1L << d.ValidBits))))
                {
                    throw new ArgumentException($"Non-finite or out-of-valid-bits pixel ({x}, {y}).");
                }
                target[(y * d.Width) + x] = valid ? value : float.NaN;
            }
        }
    }
}

/// <summary>명시적 소유 출력. 호출 간 재사용 가능하며 실행 중 Dispose/쓰기 금지.</summary>
public sealed class HostImage : IDisposable
{
    private byte[]? _data;
    private byte[]? _mask;
    public ImageDescriptor Descriptor
    {
        get;
    }
    public Memory<byte> Data => _data ?? throw new ObjectDisposedException(nameof(HostImage));
    public Memory<byte> Validity => _mask ?? throw new ObjectDisposedException(nameof(HostImage));
    public ImageView View => new(Descriptor, Data, Validity);
    public long OwnedBytes => Descriptor.RequiredBytes + Descriptor.PixelCount;

    public HostImage(ImageDescriptor descriptor)
    {
        Descriptor = descriptor;
        _data = new byte[checked((int)descriptor.RequiredBytes)];
        _mask = new byte[checked((int)descriptor.PixelCount)];
        _mask.AsSpan().Fill(255);
    }

    public void WriteFloat(ReadOnlySpan<float> pixels)
    {
        if (pixels.Length != Descriptor.PixelCount)
        {
            throw new ArgumentException("Pixel count mismatch.");
        }
        var data = Data.Span;
        var mask = Validity.Span;
        var d = Descriptor;
        for (var y = 0; y < d.Height; y++)
        {
            for (var x = 0; x < d.Width; x++)
            {
                var i = (y * d.Width) + x;
                var value = pixels[i];
                var valid = float.IsFinite(value);
                mask[i] = valid ? (byte)255 : (byte)0;
                var offset = (y * d.StrideBytes) + (x * d.BytesPerPixel);
                if (d.Format == PixelFormat.Float32)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(data.Slice(offset, 4), valid ? value : float.NaN);
                }
                else
                {
                    var integer = valid ? Math.Clamp(MathF.Round(value, MidpointRounding.ToEven), 0, (1L << d.ValidBits) - 1) : 0;
                    if (d.Format == PixelFormat.Gray8)
                    {
                        data[offset] = (byte)integer;
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(offset, 2), (ushort)integer);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _data = null;
        _mask = null;
    }
}
