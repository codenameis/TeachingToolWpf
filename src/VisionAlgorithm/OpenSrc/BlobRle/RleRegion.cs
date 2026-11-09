// File: src/VisionAlgorithm/OpenSrc/BlobRle/RleRegion.cs
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.BlobRle;

/// <summary>
/// Represents a 2D binary region encoded compactly as sorted horizontal <see cref="PixelRun"/> chords.
/// </summary>
public sealed class RleRegion
{
    private const int MinInitialRunCapacity = 16;

    private readonly List<PixelRun> _runs;

    public int BoundingWidth { get; }
    public int BoundingHeight { get; }
    public IReadOnlyList<PixelRun> Runs => _runs;
    public bool IsEmpty => _runs.Count == 0;

    public RleRegion(int boundingWidth, int boundingHeight, List<PixelRun> runs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(boundingWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(boundingHeight, 0);
        ArgumentNullException.ThrowIfNull(runs);

        BoundingWidth = boundingWidth;
        BoundingHeight = boundingHeight;
        _runs = runs;
    }

    /// <summary>
    /// Encodes an 8-bit <see cref="ReadOnlyImageView2D{T}"/> into horizontal run-length intervals in a single $O(W \times H)$ scan
    /// where pixels in $[minIntensity, maxIntensity]$ are treated as foreground.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RleRegion Threshold(
        ReadOnlyImageView2D<byte> imageView,
        byte minIntensity,
        byte maxIntensity = byte.MaxValue)
    {
        imageView.EnsureValid();
        return Threshold(imageView.Pixels, imageView.Width, imageView.Height, imageView.Stride, minIntensity, maxIntensity);
    }

    /// <summary>
    /// Encodes an 8-bit grayscale or binary image into horizontal run-length intervals in a single $O(W \times H)$ scan
    /// where pixels in $[minIntensity, maxIntensity]$ are treated as foreground.
    /// </summary>
    public static RleRegion Threshold(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        byte minIntensity,
        byte maxIntensity = byte.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (image.Length < ((height - 1) * stride) + width)
        {
            throw new ArgumentException("Image buffer is smaller than the specified dimensions and stride.", nameof(image));
        }

        List<PixelRun> runs = new(capacity: Math.Max(MinInitialRunCapacity, height));
        for (int r = 0; r < height; r++)
        {
            ReadOnlySpan<byte> row = image.Slice(r * stride, width);
            int c = 0;
            while (c < width)
            {
                while (c < width && !InRange(row[c], minIntensity, maxIntensity))
                {
                    c++;
                }

                if (c >= width)
                {
                    break;
                }

                int startCol = c;
                c++;
                while (c < width && InRange(row[c], minIntensity, maxIntensity))
                {
                    c++;
                }

                runs.Add(new PixelRun(r, startCol, c - 1));
            }
        }

        return new RleRegion(width, height, runs);
    }

    /// <summary> Computes the total foreground pixel area across all runs in $O(N_{\text{runs}})$.</summary>
    public long ComputeTotalArea()
    {
        long total = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            total += _runs[i].Length;
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InRange(byte value, byte min, byte max)
        => value >= min && value <= max;
}
