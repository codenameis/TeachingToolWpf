using System.Buffers.Binary;
using VisionImaging.Abstractions;

namespace VisionImaging.Codecs.Tests;

public sealed class DecoderTests
{
    [Theory]
    [InlineData(8, false, false)]
    [InlineData(16, false, false)]
    [InlineData(16, true, false)]
    [InlineData(8, false, true)]
    [InlineData(16, true, true)]
    public void TiffReadsOriginalValuesAcrossSequentialCalls(int bits, bool bigEndian, bool whiteIsZero)
    {
        using var reader = new NativeRasterDecoder().Open(Tiff(bits, bigEndian, whiteIsZero), CancellationToken.None);
        Assert.Equal((19, 7), (reader.Width, reader.Height));
        Assert.Equal(bits == 8 ? PixelKind.Gray8 : PixelKind.Gray16, reader.Kind);
        var pixels = new byte[19 * 7 * (bits / 8)];
        reader.ReadRows(0, 2, pixels, CancellationToken.None);
        reader.ReadRows(2, 5, pixels.AsSpan(19 * 2 * (bits / 8)), CancellationToken.None);
        for (var index = 0; index < 19 * 7; index++)
        {
            var expected = bits == 8 ? index % 256 : index * 491;
            if (whiteIsZero) expected = (bits == 8 ? 255 : 65535) - expected;
            Assert.Equal(expected,
                bits == 8 ? pixels[index] : BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(index * 2)));
        }
    }

    [Fact]
    public void InvalidRequestDoesNotConsumeRowsAndDisposalIsIdempotent()
    {
        var reader = new NativeRasterDecoder().Open(Tiff(8, false), CancellationToken.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadRows(1, 1, new byte[19], CancellationToken.None));
        Assert.Throws<ArgumentException>(() => reader.ReadRows(0, 1, new byte[18], CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => reader.ReadRows(0, 1, new byte[19], new CancellationToken(true)));
        reader.ReadRows(0, 1, new byte[19], CancellationToken.None);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadRows(1, 1, new byte[19], CancellationToken.None));
    }

    [Fact]
    public void TruncatedImageIsAnErrorAndReleasesTheFile()
    {
        var path = Tiff(16, false);
        using (var file = File.OpenWrite(path))
            file.SetLength(138);
        Assert.Throws<InvalidDataException>(() =>
        {
            using var reader = new NativeRasterDecoder().Open(path, CancellationToken.None);
            reader.ReadRows(0, 7, new byte[19 * 7 * 2], CancellationToken.None);
        });
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void DecoderDoesNotReferenceVipsOpenCvOrWindowsDesktop()
    {
        var references = typeof(NativeRasterDecoder).Assembly.GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(references, name => name.Contains("Vips") || name.Contains("OpenCv") || name.Contains("WindowsDesktop"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GiantStripUsesBoundedReadOrRejectsNativeAllocation(bool packBits)
    {
        var path = Tiff(8, false);
        var header = File.ReadAllBytes(path).AsSpan(0, 134).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(30), 32768);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(102), 32768);
        var bytes = (packBits ? 256U : 128U) * 1024 * 1024;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(114), bytes);
        if (packBits) BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(54), 32773);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            file.Write(header);
            // 무압축은0 픽셀, PackBits는(0,0) 한 픽셀 literal을 반복하는 유효한 스트림이다.
            file.SetLength(134 + bytes);
        }
        void Read(long budget = 8 * 1024 * 1024)
        {
            using var reader = new NativeRasterDecoder(budget).Open(path, default);
            var pixels = new byte[4096];
            reader.ReadRows(0, 1, pixels, default);
            Assert.All(pixels, pixel => Assert.Equal(0, pixel));
        }
        if (packBits)
        {
            var error = Assert.Throws<InvalidDataException>(() => Read());
            Assert.Contains("No space for data buffer", error.Message, StringComparison.Ordinal);
            // 같은 유효 입력이 충분한 예산에서는 읽혀야 메모리 제한으로 인한 거부임을 구별할 수 있다.
            Read(512 * 1024 * 1024);
        }
        else
        {
            // LibTIFF의 strip chopping이 큰 무압축 strip을 작은 읽기로 나눈다.
            Read();
        }
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    internal static string Tiff(int bits, bool bigEndian, bool whiteIsZero = false)
    {
        const int width = 19, height = 7, dataOffset = 134;
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "원본.tif");
        var bytes = new byte[dataOffset + width * height * (bits / 8)];
        bytes[0] = bytes[1] = bigEndian ? (byte)'M' : (byte)'I';
        Short(2, 42);
        Long(4, 8);
        Short(8, 10);
        Tag(0, 256, 4, width);
        Tag(1, 257, 4, height);
        Tag(2, 258, 3, (uint)bits);
        Tag(3, 259, 3, 1);
        Tag(4, 262, 3, whiteIsZero ? 0U : 1U);
        Tag(5, 273, 4, dataOffset);
        Tag(6, 277, 3, 1);
        Tag(7, 278, 4, height);
        Tag(8, 279, 4, (uint)(bytes.Length - dataOffset));
        Tag(9, 284, 3, 1);
        for (var i = 0; i < width * height; i++)
        {
            if (bits == 8)
                bytes[dataOffset + i] = (byte)i;
            else
                Short(dataOffset + i * 2, (ushort)(i * 491));
        }
        File.WriteAllBytes(path, bytes);
        return path;

        void Tag(int index, ushort tag, ushort type, uint value)
        {
            var offset = 10 + index * 12;
            Short(offset, tag);
            Short(offset + 2, type);
            Long(offset + 4, 1);
            if (type == 3)
                Short(offset + 8, (ushort)value);
            else
                Long(offset + 8, value);
        }
        void Short(int offset, ushort value)
        {
            if (bigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        }
        void Long(int offset, uint value)
        {
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        }
    }
}
