using OpenCvSharp;

namespace VisionImaging.Fixtures;

/// <summary>테스트/벤치마크 입력 생성 전용. 제품 파일 읽기 경로에서는 참조하지 않는다.</summary>
public static class ImageFixtureWriter
{
    public static void Write(string path, int width, int height, int channels, int bits, byte[] pixels, params int[] options)
    {
        if (bits is not (8 or 16) || channels is not (1 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(bits));
        if (pixels.Length != checked(width * height * channels * (bits / 8))) throw new ArgumentException("Pixel buffer size mismatch.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // OpenCV TIFF 출력은 RGBA ExtraSamples 태그가 없어 strict reader의 입력으로 쓰지 않는다.
        if (channels == 4 && Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff")
        {
            if (options.Length != 0) throw new NotSupportedException("RGBA TIFF fixture는 무압축만 생성합니다.");
            WriteRgbaTiff(path, width, height, bits, pixels);
            return;
        }
        var type = MatType.MakeType(bits == 8 ? MatType.CV_8U : MatType.CV_16U, channels);
        using var input = Mat.FromPixelData(height, width, type, pixels);
        using var converted = new Mat();
        var output = input;
        if (channels != 1)
        {
            Cv2.CvtColor(input, converted, channels == 3 ? ColorConversionCodes.RGB2BGR : ColorConversionCodes.RGBA2BGRA);
            output = converted;
        }
        if (!Cv2.ImWrite(path, output, options)) throw new IOException($"Fixture encoding failed: {path}");
    }

    private static void WriteRgbaTiff(string path, int width, int height, int bits, byte[] pixels)
    {
        const ushort entries = 11;
        const uint samplesOffset = 8 + 2 + entries * 12 + 4;
        const uint dataOffset = samplesOffset + 8;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8u);
        writer.Write(entries);
        Entry(256, 4, 1, (uint)width); Entry(257, 4, 1, (uint)height);
        Entry(258, 3, 4, samplesOffset); Entry(259, 3, 1, 1); Entry(262, 3, 1, 2);
        Entry(273, 4, 1, dataOffset); Entry(277, 3, 1, 4); Entry(278, 4, 1, (uint)height);
        Entry(279, 4, 1, (uint)pixels.Length); Entry(284, 3, 1, 1); Entry(338, 3, 1, 2);
        writer.Write(0u);
        for (var channel = 0; channel < 4; channel++) writer.Write((ushort)bits);
        writer.Write(pixels);
        void Entry(ushort tag, ushort type, uint count, uint value)
        { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
    }

    public static void Constant(string path, int width, int height, int bits, params ushort[] values)
    {
        var bytes = new byte[checked(width * height * values.Length * (bits / 8))];
        for (var pixel = 0; pixel < width * height; pixel++)
        for (var channel = 0; channel < values.Length; channel++)
        {
            var index = (pixel * values.Length + channel) * (bits / 8);
            if (bits == 8) bytes[index] = checked((byte)values[channel]);
            else System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index), values[channel]);
        }
        Write(path, width, height, values.Length, bits, bytes);
    }

    /// <summary>초대형 fixture는 전체 영상 RAM 없이 한 행씩 쓰는 무압축 BigTIFF로 만든다.</summary>
    public static void WriteBigTiff(string path, int width, int height, int bits, Action<int, byte[]> fillRow)
    {
        if (width <= 0 || height <= 0 || bits is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(width));
        var stride = checked(width * (bits / 8));
        const int entries = 10;
        const ulong ifd = 16;
        const ulong arrays = ifd + 8 + entries * 20 + 8;
        var counts = arrays + (ulong)height * 8;
        var data = counts + (ulong)height * 8;
        var length = checked((long)data + (long)stride * height);
        var fullPath = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(fullPath)!).AvailableFreeSpace < length + 64L * 1024 * 1024)
            throw new IOException("BigTIFF fixture 생성 공간이 부족합니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4949); writer.Write((ushort)43); writer.Write((ushort)8); writer.Write((ushort)0); writer.Write(ifd);
        writer.Write((ulong)entries);
        Entry(256, 4, 1, (ulong)width); Entry(257, 4, 1, (ulong)height);
        Entry(258, 3, 1, (ulong)bits); Entry(259, 3, 1, 1); Entry(262, 3, 1, 1);
        Entry(273, 16, (ulong)height, height == 1 ? data : arrays);
        Entry(277, 3, 1, 1); Entry(278, 4, 1, 1);
        Entry(279, 16, (ulong)height, height == 1 ? (ulong)stride : counts); Entry(284, 3, 1, 1);
        writer.Write(0UL);
        for (var y = 0; y < height; y++) writer.Write(data + (ulong)y * (ulong)stride);
        for (var y = 0; y < height; y++) writer.Write((ulong)stride);
        var row = new byte[stride];
        for (var y = 0; y < height; y++) { fillRow(y, row); writer.Write(row); }
        void Entry(ushort tag, ushort type, ulong count, ulong value)
        { writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value); }
    }
}
