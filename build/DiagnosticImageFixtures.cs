using System.Buffers.Binary;
using VisionImaging.Fixtures;

namespace VisionImaging;

/// <summary>검증 입력 생성 전용. 제품 실행물에는 포함하지 않는다.</summary>
internal static class DiagnosticImageFixtures
{
    public static void CreateLarge(string path, int bits)
        => ImageFixtureWriter.WriteBigTiff(path, 24576, 131072, bits, (_, row) =>
        {
            for (var x = 0; x < 24576; x++)
            {
                var value = x / 16 % 256;
                if (bits == 8) row[x] = (byte)value;
                else BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), (ushort)(value * 257));
            }
        });

    public static void CreateReplacement(string path, int width, int height, bool gray16)
    {
        if (gray16)
        {
            ImageFixtureWriter.WriteBigTiff(path, width, height, 16, (_, row) =>
            {
                for (var x = 0; x < width; x++)
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), (ushort)(x % 256 * 257));
            });
            return;
        }
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)(x % 256);
            pixels[offset + 3] = 255;
        }
        ImageFixtureWriter.Write(path, width, height, 4, 8, pixels);
    }
}
