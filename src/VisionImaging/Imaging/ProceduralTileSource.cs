using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging;

/// <summary>Deterministic, full-resolution sample surface. No source-sized allocation.</summary>
public sealed class ProceduralTileSource : ITileSource, IOriginalGrayTileSource
{
    public ImageInfo Info
    {
        get;
    }

    public ProceduralTileSource(int width = 8192, int height = 8192)
        => Info = new($"sample:{width}:{height}", $"검사 샘플 · {width:N0} × {height:N0}", width, height,
            PixelKind.Rgba32, TilePlanner.LevelCount(width, height));

    public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        => ReadPixels(key, PixelKind.Rgba32, cancellationToken);

    public PixelBuffer ReadOriginalGray8(int tileX, int tileY, CancellationToken cancellationToken)
        => ReadPixels(new(0, tileX, tileY), PixelKind.Gray8, cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private PixelBuffer ReadPixels(TileKey key, PixelKind kind, CancellationToken token)
    {
        if (key.Level < 0 || key.Level >= Info.Levels || key.X < 0 || key.Y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        var factor = 1 << key.Level;
        var levelWidth = (Info.Width + factor - 1) / factor;
        var levelHeight = (Info.Height + factor - 1) / factor;
        var left = checked(key.X * ImageInfo.TileSize);
        var top = checked(key.Y * ImageInfo.TileSize);
        if (left >= levelWidth || top >= levelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }
        var pixels = new PixelBuffer(Math.Min(512, levelWidth - left), Math.Min(512, levelHeight - top), kind);
        try
        {
            Span<byte> grayNoise = stackalloc byte[pixels.Width];
            Span<uint> colorNoise = stackalloc uint[pixels.Width];
            Span<ProceduralPattern.Segment> segments = stackalloc ProceduralPattern.Segment[16];
            for (var x = 0; x < pixels.Width; x++)
            {
                grayNoise[x] = (byte)(((left + x) * factor * 13) & 7);
                colorNoise[x] = grayNoise[x] * 0x00010101u;
            }
            var worldLeft = left * factor;
            var worldRight = worldLeft + (pixels.Width * factor);
            for (var y = 0; y < pixels.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                var worldY = (top + y) * factor;
                var row = pixels.Span.Slice(y * pixels.Stride, pixels.Width * (kind == PixelKind.Gray8 ? 1 : 4));
                Fill(row, kind, 0, pixels.Width, ProceduralPattern.Background);
                var count = ProceduralPattern.WriteRow(worldY % ProceduralPattern.Period, segments);
                for (var cell = worldLeft / ProceduralPattern.Period * ProceduralPattern.Period; cell < worldRight; cell += ProceduralPattern.Period)
                {
                    foreach (var segment in segments[..count])
                    {
                        var from = Math.Max(0, (int)Math.Ceiling((cell + Math.Max(0, segment.Start) - worldLeft) / (double)factor));
                        var to = Math.Min(pixels.Width, (int)Math.Ceiling((cell + Math.Min(ProceduralPattern.Period, segment.End) - worldLeft) / (double)factor));
                        if (from < to)
                        {
                            Fill(row, kind, from, to, segment.Color);
                        }
                    }
                }
                var vertical = (byte)((worldY * 7) & 7);
                if (kind == PixelKind.Gray8)
                {
                    AddGrayNoise(row, grayNoise, vertical);
                }
                else
                {
                    AddColorNoise(MemoryMarshal.Cast<byte, uint>(row), colorNoise, vertical * 0x00010101u);
                }
            }
            return pixels;
        }
        catch
        {
            pixels.Dispose();
            throw;
        }
    }

    private static void Fill(Span<byte> row, PixelKind kind, int start, int end, uint color)
    {
        if (kind == PixelKind.Gray8)
        {
            row.Slice(start, end - start).Fill(ProceduralPattern.Gray(color));
        }
        else
        {
            MemoryMarshal.Cast<byte, uint>(row).Slice(start, end - start).Fill(color);
        }
    }

    private static void AddGrayNoise(Span<byte> row, ReadOnlySpan<byte> noise, byte vertical)
    {
        var x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var y = Vector256.Create(vertical);
            for (; x <= row.Length - Vector256<byte>.Count; x += Vector256<byte>.Count)
            {
                var colors = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(row), (nuint)x);
                var horizontal = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(noise), (nuint)x);
                (colors + (horizontal ^ y)).StoreUnsafe(ref MemoryMarshal.GetReference(row), (nuint)x);
            }
        }
        for (; x < row.Length; x++)
        {
            row[x] += (byte)(noise[x] ^ vertical);
        }
    }

    private static void AddColorNoise(Span<uint> row, ReadOnlySpan<uint> noise, uint vertical)
    {
        var x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var y = Vector256.Create(vertical);
            for (; x <= row.Length - Vector256<uint>.Count; x += Vector256<uint>.Count)
            {
                var colors = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(row), (nuint)x);
                var horizontal = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(noise), (nuint)x);
                (colors + (horizontal ^ y)).StoreUnsafe(ref MemoryMarshal.GetReference(row), (nuint)x);
            }
        }
        for (; x < row.Length; x++)
        {
            row[x] += noise[x] ^ vertical;
        }
    }

    public void Dispose()
    {
    }
}
