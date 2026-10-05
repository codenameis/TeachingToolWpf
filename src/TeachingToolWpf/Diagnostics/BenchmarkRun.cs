using System.IO;
using System.Text.Json;
using System.Windows;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

internal static class BenchmarkRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string key, string fallback)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var kind = Enum.Parse<PixelKind>(Option("--format", "Gray8"));
        var seconds = int.Parse(Option("--seconds", "70"));
        var roiCount = int.Parse(Option("--rois", "0"));
        var output = Option("--metrics", "artifacts/benchmark.csv");
        var log = output + ".json";
        window.Loaded += async (_, _) =>
        {
            try
            {
                window.Width += (1920 / viewport.Dpi) - viewport.ActualWidth;
                window.Height += (1080 / viewport.Dpi) - viewport.ActualHeight;
                window.UpdateLayout();
                await model.InitializeAsync();
                model.LiveFormat = kind;
                await model.StartLive.ExecuteAsync();
                if (roiCount > 0)
                {
                    model.Session.AddRange(Enumerable.Range(0, roiCount).Select(i => new RoiGeometry(new((i % 100 * 76) + 38, (i / 100 * 42) + 21), 50, 26, i % 45)));
                }

                if (model.Session.State.Rois.Count != roiCount)
                {
                    throw new InvalidOperationException("Benchmark ROI count does not match the requested workload.");
                }

                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await model.Live.StopAsync();
                File.WriteAllText(log, JsonSerializer.Serialize(new
                {
                    Format = kind.ToString(),
                    Source = "7680x4320 synthetic full-frame CPU source",
                    TargetFps = 60,
                    Seconds = seconds,
                    ViewportWidth = viewport.ActualWidth * viewport.Dpi,
                    ViewportHeight = viewport.ActualHeight * viewport.Dpi,
                    RoiCount = roiCount,
                    Dpi = viewport.Dpi,
                    Adapter = model.GpuStatus,
                    Date = DateTimeOffset.Now
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(log, ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }
}
