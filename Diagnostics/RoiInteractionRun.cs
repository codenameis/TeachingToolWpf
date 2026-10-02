using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Fixed-input UI interaction measurements, separate from render-thread frame metrics.</summary>
internal static class RoiInteractionRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 ? args[i + 1] : fallback; }
        var counts = Option("--roi-counts", "1550,3550").Split(',').Select(int.Parse).ToArray();
        var output = Path.GetFullPath(Option("--roi-report", "artifacts/roi-interactions.json"));
        var results = new List<object>();
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            try
            {
                await model.InitializeAsync();
                foreach (var hierarchy in new[] { false, true })
                foreach (var count in counts)
                {
                    await model.Images.ReplaceAsync(new("sample:8192:8192", "", 8192, 8192));
                    var setup = Stopwatch.StartNew();
                    model.Session.AddRange(Fixture(count, hierarchy));
                    window.UpdateLayout();
                    var setupMs = setup.Elapsed.TotalMilliseconds;
                    await Task.Delay(300);
                    var rois = model.Session.State.Rois.Values.OrderBy(r => r.Order).ToArray();
                    foreach (var operation in new[] { "hover", "select", "click", "pan", "drag" })
                    {
                        var samples = new List<double>(); var allocated = new List<long>();
                        for (var i = 0; i < 15; i++)
                        {
                            var roi = rois[(i * 97) % rois.Length];
                            var point = model.Session.State.Viewport.ImageToScreen(roi.Geometry.Center);
                            var bytes = GC.GetAllocatedBytesForCurrentThread();
                            var start = Stopwatch.GetTimestamp();
                            switch (operation)
                            {
                                case "hover": model.Input.Move(point); break;
                                case "select": model.Session.Select([roi.Id]); break;
                                case "click": model.Input.Down(point); model.Input.Up(point); break;
                                case "pan": model.Session.SetViewport(model.Session.State.Viewport.Pan(new(i % 2 == 0 ? 2 : -2, 0))); break;
                                case "drag": model.Input.Down(point); model.Input.Move(point + new PointD(3, 2)); model.Input.Cancel(); break;
                            }
                            window.UpdateLayout();
                            if (i >= 5) { samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); allocated.Add(GC.GetAllocatedBytesForCurrentThread() - bytes); }
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            await Task.Delay(20);
                        }
                        samples.Sort();
                        results.Add(new { Count = count, Hierarchy = hierarchy, Operation = operation, SetupMs = setupMs,
                            P50Ms = samples[samples.Count / 2], P95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
                            AllocatedBytes = allocated.Average() });
                        Save(null);
                    }
                }
                Save(null);
            }
            catch (Exception ex) { Save(ex.ToString()); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        void Save(string? error) => File.WriteAllText(output, JsonSerializer.Serialize(new { Results = results, Error = error, Adapter = model.GpuStatus,
            Width = viewport.ViewWidth, Height = viewport.ViewHeight, Dpi = viewport.Dpi }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static IEnumerable<RotatedRect> Fixture(int count, bool hierarchy)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(hierarchy ? Math.Ceiling(count / 9d) : count));
        var cell = 8192d / columns;
        for (var i = 0; i < count; i++)
        {
            var component = hierarchy ? i / 9 : i;
            var origin = new PointD(component % columns * cell, component / columns * cell);
            if (!hierarchy || i % 9 == 0) yield return new(origin + new PointD(cell / 2, cell / 2), cell * .8, cell * .8, 0);
            else
            {
                var pad = i % 9 - 1;
                yield return new(origin + new PointD(cell * (.2 + pad % 4 * .2), cell * (.3 + pad / 4 * .4)), cell * .1, cell * .12, 0);
            }
        }
    }
}
