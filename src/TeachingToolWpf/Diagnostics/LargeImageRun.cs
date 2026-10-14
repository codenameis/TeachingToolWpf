using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
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
                    var loader = ImageLoaderHost.Create(Path.GetFullPath("artifacts/image-cache"));
                    var reference = new ImageReference(path, "", 24576, 131072, ImageFileVersion.Read(path));
                    using (var source = await loader.LoadAsync(reference, progress, CancellationToken.None))
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
                        using var warm = await loader.LoadAsync(reference, new Progress<string>(), CancellationToken.None);
                        results.Add(new
                        {
                            FileBackend = ImageLoaderHost.Backend,
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
                    await model.Images.ReplaceAsync(new(path, "", 24576, 131072, ImageFileVersion.Read(path)));
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

#if STREAMING_IMAGE_LOADER
        throw new FileNotFoundException("먼저 Baseline diagnostic-fixtures 명령으로 진단 입력을 준비하세요.", path);
#else
        DiagnosticImageFixtures.CreateLarge(path, bits);
#endif
    }
}
