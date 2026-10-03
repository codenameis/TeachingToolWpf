using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Benchmarks;

/// <summary>CPU-only attribution of one whole-document rigid commit; each sample starts from the same immutable source.</summary>
internal static class RotationCommitProfile
{
    private const int WarmupCount = 2;
    private const int SampleCount = 3;
    private const double AngleDegrees = 18;

    public static void Run(string[] args)
    {
        var count = int.Parse(Option(args, "--rois", "40000"), CultureInfo.InvariantCulture);
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "ROI 수는 2 이상이어야 합니다.");
        }

        var fixtureStart = Stopwatch.GetTimestamp();
        var before = CreateFixture(count);
        // Source warming is outside the measured commit, just as completed selection preparation precedes a drag.
        _ = before.Snapshot.Spatial;
        _ = before.Snapshot.Children;
        _ = before.SelectionFacts.HasMultipleRoots;
        _ = before.SelectedSpatial;
        var selection = before.TransformSelection;
        _ = selection.Moving;
        _ = selection.Stationary;
        var pivot = selection.Bounds.Center;
        var range = selection.RotationLimits(pivot);
        if (range.Clamp(AngleDegrees) != AngleDegrees)
        {
            throw new InvalidOperationException("진단 fixture의 회전이 제한됐습니다.");
        }

        var transform = new RigidTransform2D(pivot, AngleDegrees);
        if (!selection.PreservesHierarchy(transform))
        {
            throw new InvalidOperationException("공통 강체 변환의 계층 보존 조건이 맞지 않습니다.");
        }

        var fixtureMilliseconds = Stopwatch.GetElapsedTime(fixtureStart).TotalMilliseconds;
        var iterations = new List<Iteration>();
        for (var index = 0;
index < WarmupCount + SampleCount;
index++)
        {
            iterations.Add(Profile(selection, transform, index + 1, index < WarmupCount));
        }

        var samples = iterations.Where(i => !i.Warmup).ToArray();
        var stages = samples[0].Stages.Select(stage => new
        {
            stage.Name,
            MeanMilliseconds = samples.Average(sample => sample.Stages.Single(s => s.Name == stage.Name).Milliseconds),
            MeanAllocatedBytes = samples.Average(sample => sample.Stages.Single(s => s.Name == stage.Name).AllocatedBytes)
        }).ToArray();
        var output = JsonSerializer.Serialize(new
        {
            Kind = "rotation-commit-profile",
            RoiCount = count,
            Layout = "pcb-interior",
            Selection = "all",
            AngleDegrees,
            WarmupCount,
            SampleCount,
            FixtureMilliseconds = fixtureMilliseconds,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Stopwatch.Frequency,
            Note = "CPU-only; same prewarmed source per iteration; no forced GC; UI publication, rendering and Undo are excluded.",
            MeanMilliseconds = samples.Average(s => s.Milliseconds),
            Stages = stages,
            Iterations = iterations
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(output);
        var report = Option(args, "--report", "");
        if (report.Length != 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report, output);
        }
    }

    private static Iteration Profile(RoiTransformSelection source, RigidTransform2D transform, int index, bool warmup)
    {
        var stages = new List<Stage>();
        var start = Stopwatch.GetTimestamp();
        var next = Measure("apply-map-and-snapshot", () => source.Apply(transform));
        var edit = Measure("document-edit", () => new DocumentEdit("회전 진단", source.Before, next));
        _ = Measure("spatial", () => next.Snapshot.Spatial);
        _ = Measure("children", () => next.Snapshot.Children);
        _ = Measure("selection-facts", () => next.SelectionFacts.HasMultipleRoots);
        _ = Measure("selected-spatial", () => next.SelectedSpatial);
        var selected = Measure("transform-selection", () => next.TransformSelection);
        _ = Measure("moving-spatial", () => selected.Moving);
        _ = Measure("stationary-spatial", () => selected.Stationary);
        _ = Measure("rotation-limits", () => selected.RotationLimits(selected.Bounds.Center));
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (next.Rois.Count != source.Before.Rois.Count || edit.Changes.Length != source.Affected.Count)
        {
            throw new InvalidOperationException("진단 확정의 ROI 수 또는 변경 수가 일치하지 않습니다.");
        }

        GC.KeepAlive(next);
        GC.KeepAlive(edit);
        return new(index, warmup, elapsed, stages);

        T Measure<T>(string name, Func<T> operation)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var pause = GC.GetTotalPauseDuration();
            var generation0 = GC.CollectionCount(0);
            var generation1 = GC.CollectionCount(1);
            var generation2 = GC.CollectionCount(2);
            var stageStart = Stopwatch.GetTimestamp();
            var value = operation();
            var milliseconds = Stopwatch.GetElapsedTime(stageStart).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            stages.Add(new(name, milliseconds, bytes, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                GC.CollectionCount(0) - generation0, GC.CollectionCount(1) - generation1, GC.CollectionCount(2) - generation2));
            return value;
        }
    }

    private static DocumentState CreateFixture(int count)
    {
        // The application PCB fixture uses one enclosing component and eight pads per cell.
        // Stable IDs and explicit known parents keep unrelated hierarchy discovery outside this diagnostic.
        var columns = (int)Math.Ceiling(Math.Sqrt(Math.Ceiling(count / 9d)));
        var cell = 4096d / columns;
        var rois = ImmutableDictionary.CreateBuilder<Guid, Roi>();
        for (var index = 0;
index < count;
index++)
        {
            var component = index / 9;
            var pad = (index % 9) - 1;
            var origin = new PointD(2048 + (component % columns * cell), 2048 + (component / columns * cell));
            var geometry = pad < 0
                ? new RoiGeometry(origin + new PointD(cell / 2, cell / 2), cell * .8, cell * .8, 0)
                : new RoiGeometry(origin + new PointD(cell * (.2 + (pad % 4 * .2)), cell * (.3 + (pad / 4 * .4))), cell * .1, cell * .12, 0);
            var id = StableId(index);
            rois.Add(id, new(id, $"ROI {index}", geometry, pad < 0 ? null : StableId(component * 9), false, index));
        }
        var values = rois.ToImmutable();
        return new(values, values.Keys.ToImmutableHashSet(), new("rotation-profile", "rotation-profile", 8192, 8192), ViewportTransform.Identity);
    }

    private static Guid StableId(int index)
    {
        // Permute keys so immutable-map enumeration is not already ordered by grid position.
        var key = unchecked((uint)index + 1);
        key ^= key >> 16;
        key = unchecked(key * 0x7feb352dU);
        key ^= key >> 15;
        key = unchecked(key * 0x846ca68bU);
        key ^= key >> 16;
        return new(unchecked((int)key), 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
        {
            return fallback;
        }

        if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} 값이 필요합니다.", nameof(args));
        }

        return args[index + 1];
    }

    private sealed record Stage(string Name, double Milliseconds, long AllocatedBytes, double GcPauseMilliseconds,
        int Generation0Collections, int Generation1Collections, int Generation2Collections);
    private sealed record Iteration(int Index, bool Warmup, double Milliseconds, IReadOnlyList<Stage> Stages);
}
