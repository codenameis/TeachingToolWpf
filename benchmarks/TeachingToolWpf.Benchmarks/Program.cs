using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Benchmarks;

if (args.Contains("--rotation-commit-profile"))
{
    RotationCommitProfile.Run(args);
    return;
}

var results = new List<object>();
foreach (var count in new[] { 1000, 10000 })
{
    var random = new Random(42);
    var rois = Enumerable.Range(0, count).Select(i => new Roi(Guid.NewGuid(), $"ROI {i}",
        new(new((i % 100) * 100, (i / 100) * 100), 30 + random.Next(20), 30 + random.Next(20), random.Next(360)), null, false, i)).ToImmutableDictionary(r => r.Id);
    var timer = Stopwatch.StartNew();
    var rebuilt = RoiHierarchy.Rebuild(rois);
    results.Add(new { Count = count, HierarchyMilliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes() });
}
var output = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(output);
if (args.Length > 0) File.WriteAllText(args[0], output);
