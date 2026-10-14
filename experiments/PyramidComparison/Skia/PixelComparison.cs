using System.Runtime.InteropServices;
using System.Text.Json;
using VisionImaging.Codecs;
using VisionImaging.Abstractions;
using VisionImaging;
using VisionImaging.Pyramid;

namespace PyramidComparison;

internal static class PixelComparison
{
    public static int Run(string file, string cache, string reportPath, IRasterDecoder? decoder = null)
    {
        using var baseline = RawPyramidSource.Open(file, Path.Combine(cache, "baseline"), new NativeRasterDecoder(), new Progress<string>(), CancellationToken.None);
        using var own = RawPyramidSource.Open(file, Path.Combine(cache, "own"), decoder ?? new SkiaFileDecoder(), new Progress<string>(), CancellationToken.None);
        if (baseline.Info != own.Info)
        {
            throw new InvalidDataException($"Image information differs: {baseline.Info} / {own.Info}");
        }
        var levels = new List<object>();
        long totalDifferences = 0;
        for (var level = 0; level < own.Info.Levels; level++)
        {
            long differences = 0;
            long sum = 0;
            long count = 0;
            var maximum = 0;
            foreach (var key in Measurement.Keys(own.Info).Where(k => k.Level == level).Distinct())
            {
                using var a = baseline.Read(key, CancellationToken.None);
                using var b = own.Read(key, CancellationToken.None);
                if (a.Width != b.Width || a.Height != b.Height)
                {
                    throw new InvalidDataException($"Level dimensions differ at {key}");
                }
                if (own.Info.Pixels == PixelKind.Gray16)
                {
                    Compare(MemoryMarshal.Cast<byte, ushort>(a.Span), MemoryMarshal.Cast<byte, ushort>(b.Span));
                }
                else
                {
                    for (var i = 0; i < a.Length; i++)
                    {
                        Add(Math.Abs(a.Bytes[i] - b.Bytes[i]));
                    }
                }
            }
            levels.Add(new
            {
                Level = level,
                Samples = count,
                Differences = differences,
                MaximumError = maximum,
                MeanAbsoluteError = count == 0 ? 0 : sum / (double)count
            });
            totalDifferences += differences;

            void Compare(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b)
            {
                for (var i = 0; i < a.Length; i++)
                {
                    Add(Math.Abs(a[i] - b[i]));
                }
            }
            void Add(int difference)
            {
                count++;
                sum += difference;
                maximum = Math.Max(maximum, difference);
                if (difference != 0)
                {
                    differences++;
                }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            File = file,
            Status = totalDifferences == 0 ? "EXACT_SAMPLED" : "DIFFERENT",
            Info = own.Info,
            Levels = levels
        }, new JsonSerializerOptions { WriteIndented = true }));
        return totalDifferences == 0 ? 0 : 1;
    }
}
