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
        var iterations = int.Parse(Option("--roi-samples", "10"));
        var detail = args.Contains("--roi-detail");
        var distributed = args.Contains("--roi-distributed");
        var singleBatch = args.Contains("--roi-single-batch");
        var operations = args.Contains("--roi-legacy-operations") ? new[] { "hover", "select", "click", "pan", "drag" }
            : new[] { "hover", "select", "multi-select", "click", "pan", "drag", "commit", "rename" };
        var results = new List<object>();
        var bulkResults = new List<object>();
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
                    foreach (var batch in Fixture(count, hierarchy).Chunk(singleBatch ? count : 100_000))
                    { model.Session.AddRange(batch); await Dispatcher.Yield(DispatcherPriority.Background); }
                    window.UpdateLayout();
                    var setupMs = setup.Elapsed.TotalMilliseconds;
                    if (model.Session.State.Rois.Count != count) throw new InvalidOperationException("ROI fixture count mismatch.");
                    await Task.Delay(300);
                    var rois = model.Session.State.Rois.Values.OrderBy(r => r.Order).ToArray();
                    if (detail)
                    {
                        var scale = 4d; var center = rois[rois.Length / 2].Geometry.Center;
                        model.Session.SetViewport(new ViewportTransform(scale, new(viewport.ViewWidth / 2 - center.X * scale, viewport.ViewHeight / 2 - center.Y * scale)));
                        var a = model.Session.State.Viewport.ScreenToImage(new(0, 0)); var b = model.Session.State.Viewport.ScreenToImage(new(viewport.ViewWidth, viewport.ViewHeight));
                        rois = model.Session.State.Snapshot.Spatial.Intersecting(new(a.X, a.Y, b.X, b.Y)).OrderBy(r => r.Order).ToArray();
                    }
                    foreach (var operation in operations)
                    {
                        var samples = new List<double>(); var allocated = new List<long>(); var details = new List<object>();
                        for (var i = 0; i < iterations + 5; i++)
                        {
                            var roiIndex = distributed ? (int)((long)i * Math.Max(1, rois.Length / (iterations + 5)) % rois.Length) : i * 97 % rois.Length;
                            var roi = rois[roiIndex];
                            var point = model.Session.State.Viewport.ImageToScreen(roi.Geometry.Center);
                            var bytes = GC.GetAllocatedBytesForCurrentThread();
                            var gcPause = GC.GetTotalPauseDuration(); var threadCpu = ThreadCpuClock.Milliseconds();
                            var start = Stopwatch.GetTimestamp();
                            switch (operation)
                            {
                                case "hover": model.Input.Move(point); break;
                                case "select": model.Session.Select([roi.Id]); break;
                                case "multi-select": model.Session.Select([roi.Id, rois[(roiIndex + rois.Length / 2) % rois.Length].Id]); break;
                                case "click": model.Input.Down(point); model.Input.Up(point); break;
                                case "pan": model.Session.SetViewport(model.Session.State.Viewport.Pan(new(i % 2 == 0 ? 2 : -2, 0))); break;
                                case "drag": model.Input.Down(point); model.Input.Move(point + new PointD(3, 2)); model.Input.Cancel(); break;
                                case "commit": model.Session.Select([roi.Id]); model.Session.TranslateSelection(new(.1, 0)); model.Session.Undo(); break;
                                case "rename": model.Session.Rename(roi.Id, roi.Name + " 검사"); model.Session.Undo(); break;
                            }
                            var inputMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            window.UpdateLayout();
                            if (i >= 5)
                            {
                                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                samples.Add(elapsed); allocated.Add(GC.GetAllocatedBytesForCurrentThread() - bytes);
                                details.Add(new { ElapsedMs = elapsed, InputMs = inputMs, LayoutMs = elapsed - inputMs,
                                    GcPauseMs = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds, ThreadCpuMs = ThreadCpuClock.Milliseconds() - threadCpu });
                            }
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            await Task.Delay(20);
                        }
                        samples.Sort();
                        results.Add(new { Count = count, Hierarchy = hierarchy, Operation = operation, SetupMs = setupMs,
                            P50Ms = samples[samples.Count / 2], P95Ms = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
                            AllocatedBytes = allocated.Average(), Detail = detail, Distributed = distributed, SingleBatch = singleBatch, Samples = details });
                        Save(null);
                    }
                    if (args.Contains("--roi-bulk"))
                    {
                        var original = model.Session.State.Rois;
                        void Measure(string operation, Action action)
                        {
                            var start = Stopwatch.GetTimestamp(); action(); window.UpdateLayout();
                            bulkResults.Add(new { Count = count, Hierarchy = hierarchy, Operation = operation, Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
                            Save(null);
                        }
                        Measure("select-all", () => model.Session.Select(original.Keys));
                        Measure("delete-all", model.Session.DeleteSelection);
                        if (model.Session.State.Rois.Count != 0) throw new InvalidOperationException("Bulk delete failed.");
                        Measure("undo-delete", model.Session.Undo);
                        if (model.Session.State.Selection.Count != count || original.Any(pair => model.Session.State.Rois[pair.Key] != pair.Value))
                            throw new InvalidOperationException("Bulk Undo failed to restore exact ROI values/selection.");
                        Measure("redo-delete", model.Session.Redo);
                        if (model.Session.State.Rois.Count != 0) throw new InvalidOperationException("Bulk Redo failed.");
                        Measure("undo-again", model.Session.Undo);
                        Measure("replace-image-state", () => model.Session.SetImage(new("sample:8192:8192", "", 8192, 8192), model.Session.State.Viewport));
                        Measure("undo-image-state", model.Session.Undo);
                        if (original.Any(pair => model.Session.State.Rois[pair.Key] != pair.Value)) throw new InvalidOperationException("Image state Undo failed.");
                    }
                    if (args.Contains("--roi-capture"))
                    {
                        var capture = Path.ChangeExtension(output, $"{count}-{hierarchy}.png");
                        viewport.Capture(capture); await Task.Delay(500);
                        WindowCapture.Save(window, viewport, capture, Path.ChangeExtension(output, $"{count}-{hierarchy}-app.png"));
                    }
                }
                Save(null);
            }
            catch (Exception ex) { Save(ex.ToString()); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        void Save(string? error) => File.WriteAllText(output, JsonSerializer.Serialize(new { Results = results, BulkResults = bulkResults, Error = error, Adapter = model.GpuStatus,
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
