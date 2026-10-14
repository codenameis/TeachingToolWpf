using VisionImaging.Fixtures;

namespace PyramidComparison;

internal static class Fixtures
{
    public static void Create(string directory)
    {
        Directory.CreateDirectory(directory);
        Make("8k-gray8.png", 8192, 8192, 8, 1);
        Make("8k-color.jpg", 8192, 8192, 8, 3);
        Make("24k-gray8.tif", 24576, 8192, 8, 1);
        Make("24k-gray16.tif", 24576, 8192, 16, 1);
        Make("odd-gray16.png", 1031, 519, 16, 1);
        Make("odd-rgba.png", 1031, 519, 8, 4);

        void Make(string name, int width, int height, int bits, int bands)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return;
            var pixelBytes = bands * (bits / 8);
            var tile = new byte[512 * 512 * pixelBytes];
            new Random(7421).NextBytes(tile);
            var pixels = new byte[checked(width * height * pixelBytes)];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x += 512)
                tile.AsSpan((y % 512) * 512 * pixelBytes, Math.Min(512, width - x) * pixelBytes)
                    .CopyTo(pixels.AsSpan((y * width + x) * pixelBytes));
            ImageFixtureWriter.Write(path, width, height, bands, bits, pixels);
            Console.WriteLine(path);
        }
    }
}
