using System.Buffers.Binary;

namespace IndustrialVision.Filters.Core;

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
