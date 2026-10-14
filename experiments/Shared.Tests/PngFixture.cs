using System.Buffers.Binary;
using System.IO.Compression;

namespace VisionImaging.TestFixtures;

internal static class PngFixture
{
    internal static (string Path, byte[] Expected) Create(int bits, int channels, bool interlaced)
    {
        const int width = 19, height = 13;
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "인터레이스.png");
        var expected = new byte[width * height * channels * (bits / 8)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * channels * (bits / 8);
                if (bits == 16)
                    BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(offset), (ushort)((x + y * 31) * 137));
                else
                for (var c = 0; c < channels; c++)
                    expected[offset + c] = (byte)(x * 3 + y * 7 + c * 31);
            }
        }
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bits;
        header[9] = channels == 1 ? (byte)0 : (byte)6;
        header[12] = interlaced ? (byte)1 : (byte)0;
        Chunk(file, "IHDR"u8.ToArray(), header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            (int X, int Y, int Dx, int Dy)[] passes = interlaced
                ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
                : [(0, 0, 1, 1)];
            foreach (var pass in passes)
            {
                for (var y = pass.Y; y < height; y += pass.Dy)
                {
                    zlib.WriteByte(0);
                    for (var x = pass.X; x < width; x += pass.Dx)
                    {
                        var offset = (y * width + x) * channels * (bits / 8);
                        if (bits == 16)
                        {
                            zlib.WriteByte(expected[offset + 1]);
                            zlib.WriteByte(expected[offset]);
                        }
                        else
                            zlib.Write(expected, offset, channels);
                    }
                }
            }
        }
        Chunk(file, "IDAT"u8.ToArray(), compressed.ToArray());
        Chunk(file, "IEND"u8.ToArray(), []);
        return (path, expected);
    }
    internal static void Chunk(FileStream file, byte[] type, byte[] bytes)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length);
        file.Write(word);
        file.Write(type);
        file.Write(bytes);
        var crc = uint.MaxValue;
        foreach (var value in type.Concat(bytes))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        file.Write(word);
    }
}
