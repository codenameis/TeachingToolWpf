using System.Diagnostics;
using CpuMatching.Core;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>원본 reader의 단일 소유 수명을 유지하며 CPU 피라미드 창을 읽는다.</summary>
internal sealed class CpuMatchingImageSource(MatchingImageReader reader, SearchRegion bounds) : IGrayImageSource
{
    private readonly object _gate = new();
    public int Width => bounds.Width;
    public int Height => bounds.Height;
    public double ReadMilliseconds { get; private set; }

    public GrayImage Read(SearchRegion region, int reduction, CancellationToken token)
    {
        if (region.Width < 1 || region.Height < 1 || region.X < 0 || region.Y < 0 || region.Width < 4 || region.Height < 4 ||
            (long)region.X + region.Width > Width || (long)region.Y + region.Height > Height ||
            reduction is < 1 or > 16 || (reduction & (reduction - 1)) != 0 ||
            region.Width % reduction != 0 || region.Height % reduction != 0 ||
            (long)(region.Width / reduction) * (region.Height / reduction) > 8_388_608)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            try
            {
                var left = bounds.X + region.X;
                var top = bounds.Y + region.Y;
                var width = region.Width / reduction;
                var height = region.Height / reduction;
                if (left % reduction == 0 && top % reduction == 0 && (long)width * height <= 2_097_152)
                {
                    var raw = reader.ReadReduced(left, top, region.Width, region.Height, reduction, token);
                    return new GrayImage(width, height, raw.Pixels.Span);
                }
                // ROI 원점이 전역 축소 격자와 달라도 ROI 기준의 2×2 반올림/위상을 지킨다.
                var output = new byte[checked(width * height)];
                var step = 512 / reduction;
                for (var y = 0; y < height; y += step)
                {
                    for (var x = 0; x < width; x += step)
                    {
                        token.ThrowIfCancellationRequested();
                        var columns = Math.Min(step, width - x);
                        var rows = Math.Min(step, height - y);
                        var raw = reader.Read(left + (x * reduction), top + (y * reduction), columns * reduction, rows * reduction, token);
                        using var reduced = InspectionTileReducer.Reduce(raw, reduction, token);
                        for (var row = 0; row < rows; row++)
                        {
                            reduced.Span.Slice(row * reduced.Stride, columns).CopyTo(output.AsSpan(((y + row) * width) + x, columns));
                        }
                    }
                }
                return new GrayImage(width, height, output);
            }
            finally
            {
                ReadMilliseconds += timer.Elapsed.TotalMilliseconds;
            }
        }
    }
}
