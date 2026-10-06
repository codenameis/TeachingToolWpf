using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RoiRenderRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string key, string fallback)
        {
            var i = Array.IndexOf(args, key);
            return i < 0 ? fallback : args[i + 1];
        }
        var count = int.Parse(Option("--rois", "32940"));
        var layout = Option("--layout", "flat");
        var motion = Option("--motion", "pan");
        var report = Option("--report", "artifacts/roi-render.json");
        var steps = new List<object>();
        string? renderError = null;
        viewport.RendererStatusChanged += value =>
        {
            if (value.StartsWith("렌더 오류:") || value.StartsWith("D3D12"))
            {
                renderError = value;
            }
        };
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            try
            {
                await model.InitializeAsync();
                model.Session.AddRange(RoiInteractionRun.DescribeFixture(layout == "overlap" ? Enumerable.Repeat(new RoiGeometry(new(4096, 4096), 2500, 2200, 12), count)
                    : RoiInteractionRun.Fixture(count, layout == "pcb"), layout == "pcb"));
                await Task.Delay(1000);
                foreach (var selection in new[] { "none", "all" })
                {
                    model.Session.Select(selection == "all" ? model.Session.State.Rois.Keys : []);
                    await Task.Delay(300);
                    var baseline = model.Session.State.Viewport;
                    var samples = new List<double>();
                    for (var i = 0; i < 180; i++)
                    {
                        var start = Stopwatch.GetTimestamp();
                        model.Session.SetViewport(motion == "zoom" ? baseline.ZoomAt(new(viewport.ViewWidth / 2, viewport.ViewHeight / 2), Math.Pow(2, Math.Sin(i * .04) * 3))
                            : baseline.Pan(new(Math.Sin(i * .07) * (motion == "wide" ? 600 : 30), Math.Cos(i * .07) * (motion == "wide" ? 300 : 20))));
                        window.UpdateLayout();
                        samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        await Task.Delay(16);
                    }
                    steps.Add(new
                    {
                        selection,
                        samples
                    });
                    model.Session.SetViewport(baseline);
                    if (args.Contains("--capture-labels"))
                    {
                        var capture = Path.ChangeExtension(report, $"{selection}.png");
                        if (File.Exists(capture))
                        {
                            File.Delete(capture);
                        }

                        viewport.Capture(capture);
                        for (var i = 0; i < 200 && !File.Exists(capture); i++)
                        {
                            await Task.Delay(50);
                        }

                        if (!File.Exists(capture))
                        {
                            throw new TimeoutException("Deferred label capture did not complete.");
                        }
                    }
                }
                if (renderError is not null)
                {
                    throw new InvalidOperationException(renderError);
                }

                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    count,
                    layout,
                    motion,
                    steps,
                    error = renderError,
                    model.GpuStatus
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }
}
