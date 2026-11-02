using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using VisionCanvas.Core.Alignment;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Benchmarks;

internal static class FiducialAlignmentProfile
{
    public static void Run(string[] args)
    {
        PointD[] reference = [new(120, 300), new(17000, 300), new(17000, 30000), new(120, 30000)];
        var detected = reference.Select(p => new PointD(50000 - p.Y, 100 + p.X)).ToArray();
        var (state, leaves) = CreateDocument();
        var map = new RoiAlignmentMap(state);
        if (!map.TryAlign(null, reference, detected, out map, out var error))
        {
            throw new InvalidOperationException(error);
        }
        foreach (var group in state.Rois.Values.Where(r => r.IsGroup && r.ParentId is not null).OrderBy(r => r.Order))
        {
            var point = group.Geometry.Center;
            var module = state.Rois[group.ParentId!.Value].ParentId is null;
            var found = new PointD(50000 - point.Y + (module ? 5 : 8), 100 + point.X + (module ? 7 : 5));
            if (!map.TryAlign(group.Id, [point], [found], out map, out error))
            {
                throw new InvalidOperationException(error);
            }
        }
        foreach (var leaf in leaves)
        {
            var mapped = map.Map(leaf.Id);
            var expected = new PointD(50008 - leaf.Geometry.Center.Y, 105 + leaf.Geometry.Center.X);
            if ((mapped.Geometry.Center - expected).Length > 1e-8 || mapped.ParentId != leaf.ParentId)
            {
                throw new InvalidOperationException("4만 ROI의 좌표 또는 부모 연결이 예상과 다릅니다.");
            }
        }

        var groupTransforms = Enumerable.Range(0, leaves.Length / 100)
            .Select(i => map.GetAlignment(leaves[i * 100].ParentId!.Value)!.Transform).ToArray();
        var results = new List<object>();
        for (var count = 1; count <= 4; count++)
        {
            var markCount = count;
            results.Add(Measure($"Solve{count}Marks", 10000, () =>
            {
                var checksum = 0d;
                for (var i = 0; i < 10000; i++)
                {
                    if (!FiducialAlignment.TryCreate(reference.AsSpan(0, markCount), detected.AsSpan(0, markCount), out var alignment, out var failure))
                    {
                        throw new InvalidOperationException(failure);
                    }
                    checksum += alignment.Transform.Translation.X;
                }
                return checksum;
            }));
        }
        results.Add(Measure("ResolveParentsAndMap40000Centers", leaves.Length, () =>
        {
            var checksum = 0d;
            foreach (var leaf in leaves)
            {
                checksum += map.GetAlignment(leaf.Id)!.Transform.Apply(leaf.Geometry.Center).X;
            }
            return checksum;
        }));
        results.Add(Measure("Cached400GroupTransformsMap40000Centers", leaves.Length, () =>
        {
            var checksum = 0d;
            for (var groupIndex = 0; groupIndex < groupTransforms.Length; groupIndex++)
            {
                var transform = groupTransforms[groupIndex];
                for (var i = groupIndex * 100; i < (groupIndex + 1) * 100; i++)
                {
                    checksum += transform.Apply(leaves[i].Geometry.Center).X;
                }
            }
            return checksum;
        }));
        results.Add(Measure("Map40000RoiRecordsWithParents", leaves.Length, () =>
        {
            var checksum = 0d;
            foreach (var leaf in leaves)
            {
                checksum += map.Map(leaf.Id).Geometry.Center.X;
            }
            return checksum;
        }));
        var json = JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            WarmupRounds = 3,
            MeasuredRounds = 7,
            Modules = 100,
            RoiGroups = 400,
            Leaves = leaves.Length,
            CoordinateAndParentChecksPassed = leaves.Length,
            Excludes = "mark detection, image I/O, inspection, rendering, document preparation, alignment registration and cached group transform preparation",
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        var outputIndex = Array.IndexOf(args, "--output");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
        {
            File.WriteAllText(args[outputIndex + 1], json);
        }
    }

    private static object Measure(string name, int operations, Func<double> action)
    {
        for (var i = 0; i < 3; i++)
        {
            action();
        }
        var elapsed = new double[7];
        var allocated = new long[7];
        var checksum = 0d;
        for (var i = 0; i < elapsed.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            checksum += action();
            elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var ordered = elapsed.Order().ToArray();
        return new
        {
            Name = name,
            Operations = operations,
            MedianMilliseconds = ordered[3],
            Milliseconds = elapsed,
            AllocatedBytes = allocated,
            Checksum = checksum
        };
    }

    private static (DocumentState State, Roi[] Leaves) CreateDocument()
    {
        var root = new Roi(Guid.NewGuid(), "전체", new(new(15000, 15000), 30000, 30000, 0), null, true, 0);
        var rois = ImmutableDictionary.CreateBuilder<Guid, Roi>();
        rois.Add(root.Id, root);
        var leaves = new List<Roi>(40000);
        for (var moduleIndex = 0; moduleIndex < 100; moduleIndex++)
        {
            var center = new PointD((moduleIndex % 10 * 3000) + 1500, (moduleIndex / 10 * 3000) + 1500);
            var module = new Roi(Guid.NewGuid(), "모듈", new(center, 3000, 3000, 0), root.Id, true, 1);
            rois.Add(module.Id, module);
            for (var groupIndex = 0; groupIndex < 4; groupIndex++)
            {
                var groupCenter = center + new PointD(((groupIndex % 2) - 0.5) * 1500, ((groupIndex / 2) - 0.5) * 1500);
                var group = new Roi(Guid.NewGuid(), "ROI 그룹", new(groupCenter, 1500, 1500, 0), module.Id, true, 2);
                rois.Add(group.Id, group);
                for (var i = 0; i < 100; i++)
                {
                    var point = groupCenter + new PointD(((i % 10) - 4.5) * 100, ((i / 10) - 4.5) * 100);
                    var leaf = new Roi(Guid.NewGuid(), "검사 ROI", new(point, 10, 10, 0), group.Id, false, 3);
                    rois.Add(leaf.Id, leaf);
                    leaves.Add(leaf);
                }
            }
        }
        return (new(rois.ToImmutable(), RoiIdSet.Empty, null, ViewportTransform.Identity), leaves.ToArray());
    }
}
