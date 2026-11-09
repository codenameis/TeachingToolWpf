// File: src/VisionAlgorithm/OpenSrc/Common/Gray8BmpFileExporter.cs
using System.Buffers.Binary;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Pure .NET standard 8-bit grayscale Windows Bitmap (<c>.bmp</c>) exporter and loader with 256-entry identity palette
/// and 4-byte row padding alignment. Requires zero external codec libraries.
/// </summary>
public static class Gray8BmpFileExporter
{
    private const ushort BmpMagic = 0x4D42; // 'B', 'M'
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int PaletteEntries = 256;
    private const int BytesPerPaletteEntry = 4;
    private const int PaletteSize = PaletteEntries * BytesPerPaletteEntry;
    private const int PixelDataOffset = FileHeaderSize + InfoHeaderSize + PaletteSize; // 1078 bytes
    private const ushort ColorPlanes = 1;
    private const ushort BitsPerPixel8 = 8;
    private const int DefaultDpiPpm = 3780; // ~96 DPI in pixels per meter
    private const int RowAlignmentBytes = 4;

    /// <summary>
    /// Exports an 8-bit <see cref="ReadOnlyImageView2D{T}"/> to a standard 8-bit indexed grayscale <c>.bmp</c> file on disk.
    /// </summary>
    public static void SaveGray8Bmp(string filePath, ReadOnlyImageView2D<byte> image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        image.EnsureValid();
        SaveGray8Bmp(filePath, image.Pixels, image.Width, image.Height, image.Stride);
    }

    /// <summary>
    /// Exports a raw 8-bit grayscale buffer to a standard 8-bit indexed grayscale <c>.bmp</c> file on disk.
    /// </summary>
    public static void SaveGray8Bmp(
        string filePath,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (pixels.Length < ((long)(height - 1) * stride) + width)
        {
            throw new ArgumentException("Pixel buffer is smaller than required by dimensions and stride.", nameof(pixels));
        }

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        int paddedRowStride = (width + (RowAlignmentBytes - 1)) & ~(RowAlignmentBytes - 1);
        long imageSize = (long)paddedRowStride * height;
        long totalFileSize = PixelDataOffset + imageSize;

        using FileStream fs = new(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 65536);
        Span<byte> header = stackalloc byte[PixelDataOffset];
        header.Clear();

        // 1. BITMAPFILEHEADER (14 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(0, 2), BmpMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(2, 4), (uint)Math.Min(uint.MaxValue, totalFileSize));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(10, 4), PixelDataOffset);

        // 2. BITMAPINFOHEADER (40 bytes, top-down negative height or bottom-up positive height; use standard bottom-up positive height for universal viewer compatibility)
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(18, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(22, 4), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26, 2), ColorPlanes);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), BitsPerPixel8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(30, 4), 0u); // BI_RGB
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(34, 4), (uint)Math.Min(uint.MaxValue, imageSize));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(38, 4), DefaultDpiPpm);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(42, 4), DefaultDpiPpm);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(46, 4), PaletteEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(50, 4), PaletteEntries);

        // 3. 256-entry Grayscale Palette (B, G, R, 0)
        int paletteOffset = FileHeaderSize + InfoHeaderSize;
        for (int i = 0; i < PaletteEntries; i++)
        {
            int entryOffset = paletteOffset + (i * BytesPerPaletteEntry);
            byte gray = (byte)i;
            header[entryOffset] = gray;
            header[entryOffset + 1] = gray;
            header[entryOffset + 2] = gray;
            header[entryOffset + 3] = 0;
        }

        fs.Write(header);

        // 4. Write rows bottom-up with 4-byte padding alignment
        int paddingBytes = paddedRowStride - width;
        Span<byte> padZero = stackalloc byte[RowAlignmentBytes];
        padZero.Clear();

        for (int y = height - 1; y >= 0; y--)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * stride, width);
            fs.Write(row);
            if (paddingBytes > 0)
            {
                fs.Write(padZero.Slice(0, paddingBytes));
            }
        }
    }

    /// <summary>
    /// Loads a standard 8-bit grayscale <c>.bmp</c> file from disk into a tight row-major byte array.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) LoadGray8Bmp(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        Span<byte> fileHeader = stackalloc byte[FileHeaderSize + InfoHeaderSize];
        fs.ReadExactly(fileHeader);

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.Slice(0, 2));
        if (magic != BmpMagic)
        {
            throw new InvalidDataException("Invalid BMP magic header.");
        }

        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader.Slice(10, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.Slice(18, 4));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.Slice(22, 4));
        ushort bpp = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.Slice(28, 2));
        if (bpp != BitsPerPixel8 || width <= 0 || rawHeight == 0)
        {
            throw new InvalidDataException("Only 8-bit grayscale BMP files with positive dimensions are supported.");
        }

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        int paddedRowStride = (width + (RowAlignmentBytes - 1)) & ~(RowAlignmentBytes - 1);
        byte[] pixels = new byte[checked(width * height)];

        fs.Position = dataOffset;
        byte[] paddedRow = new byte[paddedRowStride];

        for (int i = 0; i < height; i++)
        {
            int dstY = topDown ? i : (height - 1 - i);
            fs.ReadExactly(paddedRow);
            Buffer.BlockCopy(paddedRow, 0, pixels, dstY * width, width);
        }

        return (pixels, width, height);
    }

    /// <summary>
    /// Loads a standard 8-bit grayscale <c>.bmp</c> file from disk into a tight row-major byte array and outputs dimensions.
    /// </summary>
    public static byte[] LoadGray8Bmp(string filePath, out int width, out int height)
    {
        var (pixels, w, h) = LoadGray8Bmp(filePath);
        width = w;
        height = h;
        return pixels;
    }
}
