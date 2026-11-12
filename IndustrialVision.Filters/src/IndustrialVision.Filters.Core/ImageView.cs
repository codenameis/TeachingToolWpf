using System.Buffers.Binary;

namespace IndustrialVision.Filters.Core;

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
