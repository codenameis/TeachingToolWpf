using System.Buffers.Binary;
using System.Text;
using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Examples;

/// <summary>예제 전용 P5/Raw 어댑터. Core에는 디코더 의존성을 추가하지 않는다.</summary>
public sealed class FileTileSource : IImageTileSource, IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly bool _bigEndian;
    private readonly bool _leftAligned;
    public ImageDescriptor Descriptor
    {
        get;
    }

    public FileTileSource(string path, ImageDescriptor descriptor, long dataOffset = 0, bool bigEndian = false, bool leftAligned = false)
    {
        if (dataOffset < 0 || (descriptor.Format == PixelFormat.Float32 && (bigEndian || leftAligned)))
        {
            throw new ArgumentException("Invalid Raw layout.");
        }
        Descriptor = descriptor;
        _dataOffset = dataOffset;
        _bigEndian = bigEndian;
        _leftAligned = leftAligned;
        _stream = File.OpenRead(path);
        if (_stream.Length < checked(dataOffset + descriptor.RequiredBytes))
        {
            _stream.Dispose();
            throw new InvalidDataException("Truncated Raw/PGM file.");
        }
    }

    public static FileTileSource OpenPgm(string path)
    {
        using var stream = File.OpenRead(path);
        if (Token(stream) != "P5")
        {
            throw new InvalidDataException("Only binary grayscale P5 PGM is supported.");
        }
        var width = int.Parse(Token(stream), System.Globalization.CultureInfo.InvariantCulture);
        var height = int.Parse(Token(stream), System.Globalization.CultureInfo.InvariantCulture);
        var maximum = int.Parse(Token(stream), System.Globalization.CultureInfo.InvariantCulture);
        if (maximum < 1 || maximum > 65535)
        {
            throw new InvalidDataException("Unsupported PGM maxval.");
        }
        var bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)maximum);
        return new(path, new(width, height, maximum < 256 ? PixelFormat.Gray8 : PixelFormat.Gray16, validBits: bits),
            stream.Position, bigEndian: maximum >= 256);
    }

    private static string Token(Stream stream)
    {
        var value = stream.ReadByte();
        while (value >= 0)
        {
            if (value == '#')
            {
                while (value >= 0 && value != '\n')
                {
                    value = stream.ReadByte();
                }
            }
            if (value > 32)
            {
                break;
            }
            value = stream.ReadByte();
        }
        var result = new StringBuilder();
        while (value > 32 && value >= 0)
        {
            result.Append((char)value);
            value = stream.ReadByte();
        }
        if (value == '\r')
        {
            var next = stream.ReadByte();
            if (next != '\n' && next >= 0)
            {
                stream.Position--;
            }
        }
        return result.Length > 0 ? result.ToString() : throw new InvalidDataException("Incomplete PGM header.");
    }

    public void Read(ImageRegion region, HostImage destination, CancellationToken token)
    {
        Descriptor.Region(region.X, region.Y, region.Width, region.Height);
        var d = destination.Descriptor;
        if (d.Width != region.Width || d.Height != region.Height || d.Format != Descriptor.Format || d.ValidBits != Descriptor.ValidBits)
        {
            throw new ArgumentException("Destination layout mismatch.");
        }
        for (var y = 0; y < region.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var row = destination.Data.Span.Slice(y * d.StrideBytes, d.Width * d.BytesPerPixel);
            _stream.Position = checked(_dataOffset + ((long)(region.Y + y) * Descriptor.StrideBytes) + ((long)region.X * Descriptor.BytesPerPixel));
            _stream.ReadExactly(row);
            if (d.Format == PixelFormat.Gray16)
            {
                for (var x = 0; x < d.Width; x++)
                {
                    var bytes = row.Slice(x * 2, 2);
                    var value = _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                    if (_leftAligned)
                    {
                        value = (ushort)(value >> (16 - d.ValidBits));
                    }
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
                }
            }
        }
        destination.Validity.Span.Fill(255);
    }

    public void Dispose() => _stream.Dispose();
}
