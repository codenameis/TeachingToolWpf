using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Benchmarks;

internal static class SelectionProfile
{
    public static async Task RunAsync(string[] args)
    {
        var editor = new EditorSession();
        editor.SetImage(new("selection-test", "8K", 8192, 8192), ViewportTransform.Identity);
        editor.AddRange(Enumerable.Range(0, 300000).Select(i => new RoiGeometry(new(1000 + (i % 1000 * 6), 1000 + (i / 1000 * 12)), 3, 5, 0)));
        var source = editor.State;
        _ = source.Snapshot.Spatial;
        _ = source.Snapshot.Children;
        _ = source.Snapshot.SearchSpatial;
        var results = new List<object>();
        foreach (var count in new[] { 150000, 200000 })
        {
            var area = new BoundsD(996, 996, 7000, 1000 + (count / 1000 * 12) - 6);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                editor.Select([]);
                var start = Stopwatch.GetTimestamp();
                var allocated = GC.GetTotalAllocatedBytes();
                var prepared = await editor.PrepareSelectionAsync(Ids());
                var prepareMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                editor.CommitPrepared(prepared);
                if (editor.State.Selection.Count != count)
                {
                    throw new InvalidOperationException($"Selection count mismatch: expected {count}, actual {editor.State.Selection.Count}.");
                }
                results.Add(new
                {
                    count,
                    iteration,
                    operation = "prepare-and-commit",
                    milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    prepareMs,
                    allocatedBytes = GC.GetTotalAllocatedBytes() - allocated
                });

                var stages = new List<object>();
                var selected = Measure("query-and-set", () => RoiIdSet.CreateRange(Ids().Where(source.Rois.ContainsKey)));
                var state = source with
                {
                    Selection = selected
                };
                _ = Measure("selection-facts", () => state.SelectionFacts.HasMultipleRoots);
                _ = Measure("selected-spatial", () => state.SelectedSpatial);
                var transform = Measure("transform-membership", () => state.TransformSelection);
                _ = Measure("moving", () => transform.Moving);
                _ = Measure("stationary", () => transform.Stationary);
                _ = Measure("moving-search", () => transform.MovingSearch);
                _ = Measure("stationary-search", () => transform.StationarySearch);
                _ = Measure("rotation-limits", () => transform.RotationLimits(transform.Bounds.Center));
                results.Add(new
                {
                    count,
                    iteration,
                    operation = "attribution",
                    stages
                });

                T Measure<T>(string name, Func<T> action)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var tick = Stopwatch.GetTimestamp();
                    var value = action();
                    stages.Add(new
                    {
                        name,
                        milliseconds = Stopwatch.GetElapsedTime(tick).TotalMilliseconds,
                        allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before
                    });
                    return value;
                }
            }
            IEnumerable<Guid> Ids() => source.Snapshot.Spatial.Intersecting(area)
                .Where(r => r.Geometry.Bounds is var bounds && bounds.Left >= area.Left && bounds.Top >= area.Top && bounds.Right <= area.Right && bounds.Bottom <= area.Bottom)
                .Select(r => r.Id);
        }
        var reportIndex = Array.IndexOf(args, "--report");
        var report = reportIndex < 0 ? "artifacts/selection-profile.json" : args[reportIndex + 1];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            note = "300000 ordinary ROI; empty selection to 150000/200000; iteration0 warmup; exact marquee bounds; existing document spatial index warm; CPU service only, UI/render excluded. Attribution uses a separate fresh selection.",
            coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EditorSession).Assembly.Location))),
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report);
    }
}
