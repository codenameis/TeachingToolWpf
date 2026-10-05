using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NetVips;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

internal static class LargeImageRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory("artifacts/large-images");
            var results = new List<object>();
            try
            {
                foreach (var bits in new[] { 8, 16 })
                {
                    var path = Path.GetFullPath($"artifacts/large-images/24k-{bits}bit.tif");
                    var generation = Stopwatch.StartNew();
                    await Task.Run(() => CreateFixture(path, bits));
                    var generationSeconds = generation.Elapsed.TotalSeconds;
                    var process = Process.GetCurrentProcess();
                    long peakPrivate = 0;

                    long peakWorking = 0;
                    using var timer = new Timer(_ =>
                    {
                        process.Refresh();
                        Interlocked.Exchange(ref peakPrivate, Math.Max(Interlocked.Read(ref peakPrivate), process.PrivateMemorySize64));
                        Interlocked.Exchange(ref peakWorking, Math.Max(Interlocked.Read(ref peakWorking), process.WorkingSet64));
                    }, null, 0, 100);
                    var watch = Stopwatch.StartNew();
                    var progress = new Progress<string>(text => model.Status = text);
                    using (var source = await Task.Run(() => DiskPyramidSource.Open(path, Path.GetFullPath("artifacts/image-cache"), progress, CancellationToken.None)))
                    {
                        var cold = watch.Elapsed.TotalSeconds;
                        using var last = await Task.Run(() => source.Read(new(0, 47, 255), CancellationToken.None));
                        var expected = 24064 / 16 % 256;
                        var actual = bits == 8 ? last.Bytes[0] : BitConverter.ToUInt16(last.Bytes);
                        if (actual != expected * (bits == 8 ? 1 : 257))
                        {
                            throw new InvalidDataException("4 GiB 경계를 넘는 마지막 타일의 픽셀 값이 다릅니다.");
                        }

                        watch.Restart();
                        using var warm = await Task.Run(() => DiskPyramidSource.Open(path, Path.GetFullPath("artifacts/image-cache"), new Progress<string>(), CancellationToken.None));
                        results.Add(new
                        {
                            Width = 24576,
                            Height = 131072,
                            Bits = bits,
                            DecodedBytes = 24576L * 131072 * (bits / 8),
                            FileBytes = new FileInfo(path).Length,
                            GenerationSeconds = generationSeconds,
                            ColdSeconds = cold,
                            WarmSeconds = watch.Elapsed.TotalSeconds,
                            PeakPrivateBytes = peakPrivate,
                            PeakWorkingBytes = peakWorking,
                            LastTilePixel = actual
                        });
                    }
                    await model.Images.ReplaceAsync(new(path, "", 24576, 131072, DiskPyramidSource.Version(path)));
                    var roi = model.Session.Add(new(new(12000, 65000), 18000, 25000, 8));
                    model.Session.Rename(roi, $"24K / {bits}비트 검사");
                    await Task.Delay(1500);
                    viewport.Capture($"artifacts/large-images/24k-{bits}bit.png");
                    await Task.Delay(500);
                    File.WriteAllText("artifacts/large-image-results.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText("artifacts/large-image-error.log", ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }

    private static void CreateFixture(string path, int bits)
    {
        if (File.Exists(path))
        {
            return;
        }

        VipsRuntime.Configure();
        var row = Enumerable.Range(0, 24576).Select(x => (ushort)(x / 16 % 256 * (bits == 8 ? 1 : 257))).ToArray();
        using var line = Image.NewFromMemory(row, 24576, 1, 1, Enums.BandFormat.Ushort);
        using var typed = line.Cast(bits == 8 ? Enums.BandFormat.Uchar : Enums.BandFormat.Ushort);
        using var image = typed.Replicate(1, 131072);
        image.WriteToFile(path, new VOption { { "bigtiff", true }, { "tile", true }, { "tile_width", 512 }, { "tile_height", 512 }, { "compression", Enums.ForeignTiffCompression.Deflate } });
    }
}
