using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Benchmarks;

/// <summary>CPU-only copy/paste measurement through public contracts, with an independent cold-index replay.</summary>
internal static class CopyPasteProfile
{
    private const int WarmupCount = 2;
    private const int SampleCount = 5;

    public static async Task RunAsync(string[] args)
    {
        var count = int.Parse(Option(args, "--rois", "40000"), CultureInfo.InvariantCulture);
        var layout = Option(args, "--layout", "pcb");
        if (count < 1 || layout is not ("pcb" or "flat"))
        {
            throw new ArgumentException("ROI 수는 1 이상, layout은 pcb 또는 flat이어야 합니다.", nameof(args));
        }

        var fixtureStages = new List<Stage>();
        var session = Measure(fixtureStages, "session-construction-and-seed", () => CreateFixture(count, layout));
        var before = session.State;
        _ = Measure(fixtureStages, "source-index-warming", () => Warm(before));
        var iterations = new List<Iteration>();
        PreparedEditorPaste? lastPrepared = null;
        RoiClipboardContent? lastContent = null;
        for (var index = 0; index < WarmupCount + SampleCount; index++)
        {
            var stages = new List<Stage>();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var start = Stopwatch.GetTimestamp();
            var clipboard = Measure(stages, "copy", () =>
            {
                var value = new RoiClipboard();
                if (value.Copy(before) != count)
                {
                    throw new InvalidOperationException("복사 ROI 수가 입력과 다릅니다.");
                }
                return value;
            });
            var prepared = await MeasureAsync(stages, "prepare-paste-inclusive", () => session.PreparePasteAsync(clipboard));
            var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            if (!ReferenceEquals(before, session.State))
            {
                throw new InvalidOperationException("준비 작업이 원본 세션을 변경했습니다.");
            }

            // PreparePasteAsync already warms every index. Recreate its output snapshot before separate attribution.
            var added = prepared.State.Rois.Keys.Where(id => !before.Rois.ContainsKey(id)).ToImmutableArray();
            var replay = before.WithRois(prepared.State.Rois, added) with
            {
                Selection = prepared.State.Selection
            };
            var replayStages = new List<Stage>();
            var content = clipboard.Capture();
            _ = Measure(replayStages, "place", () => content.Place(before.Image!, new(20, 20)));
            _ = Measure(replayStages, "document-edit", () => new DocumentEdit("붙여넣기 진단", before, replay));
            ProfileIndexes(replay, replayStages);
            iterations.Add(new(index + 1, index < WarmupCount, milliseconds, bytes, stages, replayStages));
            lastPrepared = prepared;
            lastContent = content;
        }

        Verify(session, before, lastPrepared!, lastContent!);
        var samples = iterations.Where(i => !i.Warmup).ToArray();
        using var assembly = File.OpenRead(typeof(EditorSession).Assembly.Location);
        var output = JsonSerializer.Serialize(new
        {
            Kind = "copy-paste-profile",
            RoiCount = count,
            ResultRoiCount = count * 2,
            Layout = layout,
            ImageWidth = 8192,
            ImageHeight = 8192,
            Selection = "all",
            WarmupCount,
            SampleCount,
            CoreSha256 = Convert.ToHexString(SHA256.HashData(assembly)),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            Stopwatch.Frequency,
            Note = "Same immutable source and session per iteration; no forced GC. Prepare includes placement, clone, edit and index warming. ReplayStages repeat public suboperations on a cold output snapshot and must not be added to total. Allocation counts are process-wide, including the preparation worker. UI publication/rendering and verification are excluded. Public AddRange assigns new source GUIDs per process.",
            Verification = "PASS: exact clone count, geometry, metadata, parents, root selection, unchanged originals and Undo/Redo values",
            FixtureStages = fixtureStages,
            MeanMilliseconds = samples.Average(i => i.Milliseconds),
            MeanAllocatedBytes = samples.Average(i => i.AllocatedBytes),
            MeanStages = Summarize(samples.Select(i => i.Stages)),
            MeanReplayStages = Summarize(samples.Select(i => i.ReplayStages)),
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

    private static EditorSession CreateFixture(int count, string layout)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(layout == "pcb" ? Math.Ceiling(count / 9d) : count));
        var cell = 4096d / columns;
        var definitions = new RoiCreation[count];
        for (var index = 0; index < count; index++)
        {
            var component = layout == "pcb" ? index / 9 : index;
            var pad = layout == "pcb" ? (index % 9) - 1 : -1;
            var origin = new PointD(2048 + (component % columns * cell), 2048 + (component / columns * cell));
            var geometry = pad < 0
                ? new RoiGeometry(origin + new PointD(cell / 2, cell / 2), cell * .8, cell * .8, 0)
                : new RoiGeometry(origin + new PointD(cell * (.2 + (pad % 4 * .2)), cell * (.3 + (pad / 4 * .4))), cell * .1, cell * .12, 0);
            definitions[index] = new(geometry, pad < 0 ? null : component * 9);
        }
        var session = new EditorSession();
        session.InitializeImage(new("copy-paste-profile", "copy-paste-profile", 8192, 8192), ViewportTransform.Identity);
        session.AddRange(definitions);
        session.Select(session.State.Rois.Keys);
        return session;
    }

    private static bool Warm(DocumentState state)
    {
        ProfileIndexes(state, []);
        return true;
    }

    private static void ProfileIndexes(DocumentState state, List<Stage> stages)
    {
        _ = Measure(stages, "spatial", () => state.Snapshot.Spatial);
        _ = Measure(stages, "children", () => state.Snapshot.Children);
        _ = Measure(stages, "selection-facts", () => state.SelectionFacts.HasMultipleRoots);
        _ = Measure(stages, "selected-spatial", () => state.SelectedSpatial);
        var selected = Measure(stages, "transform-selection", () => state.TransformSelection);
        _ = Measure(stages, "moving-spatial", () => selected.Moving);
        _ = Measure(stages, "stationary-spatial", () => selected.Stationary);
        _ = Measure(stages, "rotation-limits", () => selected.RotationLimits(selected.Bounds.Center));
    }

    private static void Verify(EditorSession session, DocumentState before, PreparedEditorPaste prepared, RoiClipboardContent content)
    {
        var next = prepared.State;
        var added = next.Rois.Values.Where(r => !before.Rois.ContainsKey(r.Id)).OrderBy(r => r.Order).ToArray();
        var placed = content.Place(before.Image!, new(20, 20));
        if (added.Length != before.Rois.Count || next.Rois.Count != before.Rois.Count * 2 ||
            before.Rois.Any(pair => next.Rois[pair.Key] != pair.Value))
        {
            throw new InvalidOperationException("복제 개수 또는 원본 보존 검증 실패.");
        }
        var ids = placed.Select((roi, index) => (roi.Id, CopiedId: added[index].Id)).ToDictionary(pair => pair.Id, pair => pair.CopiedId);
        var order = before.Rois.Values.Max(r => r.Order);
        for (var index = 0; index < placed.Length; index++)
        {
            var original = placed[index];
            var expected = original with
            {
                Id = ids[original.Id],
                ParentId = original.ParentId is { } parent && ids.TryGetValue(parent, out var copiedParent) ? copiedParent : null,
                Order = ++order,
                Name = original.Name[..Math.Min(original.Name.Length, 253)] + " 복사"
            };
            if (added[index] != expected)
            {
                throw new InvalidOperationException("복제 형상·이름·필터·순서·부모 검증 실패.");
            }
        }
        if (!next.Selection.SetEquals(added.Where(r => r.ParentId is null).Select(r => r.Id)))
        {
            throw new InvalidOperationException("복제 루트 선택 검증 실패.");
        }
        session.CommitPreparedPaste(prepared);
        AssertState(next, session.State);
        session.Undo();
        AssertState(before, session.State);
        session.Redo();
        AssertState(next, session.State);
    }

    private static void AssertState(DocumentState expected, DocumentState actual)
    {
        if (actual.Rois.Count != expected.Rois.Count || !actual.Selection.SetEquals(expected.Selection) ||
            actual.Image != expected.Image || actual.Viewport != expected.Viewport || expected.Rois.Any(pair => actual.Rois[pair.Key] != pair.Value))
        {
            throw new InvalidOperationException("문서 게시 또는 Undo/Redo 값 복원 검증 실패.");
        }
    }

    private static T Measure<T>(List<Stage> stages, string name, Func<T> operation)
    {
        var allocated = GC.GetTotalAllocatedBytes(true);
        var pause = GC.GetTotalPauseDuration();
        var start = Stopwatch.GetTimestamp();
        var value = operation();
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        stages.Add(new(name, milliseconds, GC.GetTotalAllocatedBytes(true) - allocated, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds));
        return value;
    }

    private static async Task<T> MeasureAsync<T>(List<Stage> stages, string name, Func<Task<T>> operation)
    {
        var allocated = GC.GetTotalAllocatedBytes(true);
        var pause = GC.GetTotalPauseDuration();
        var start = Stopwatch.GetTimestamp();
        var value = await operation();
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        stages.Add(new(name, milliseconds, GC.GetTotalAllocatedBytes(true) - allocated, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds));
        return value;
    }

    private static object[] Summarize(IEnumerable<IReadOnlyList<Stage>> samples) => samples.SelectMany(s => s).GroupBy(s => s.Name)
        .Select(group => (object)new { Name = group.Key, MeanMilliseconds = group.Average(s => s.Milliseconds), MeanAllocatedBytes = group.Average(s => s.AllocatedBytes) }).ToArray();

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

    private sealed record Stage(string Name, double Milliseconds, long AllocatedBytes, double GcPauseMilliseconds);
    private sealed record Iteration(int Index, bool Warmup, double Milliseconds, long AllocatedBytes, IReadOnlyList<Stage> Stages, IReadOnlyList<Stage> ReplayStages);
}
