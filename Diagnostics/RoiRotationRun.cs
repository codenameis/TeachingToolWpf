using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Exercises one continuous rotation through the real selection handle and application commit path.</summary>
internal static class RoiRotationRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        var samples = new List<object>();
        var operations = new List<object>();
        var captures = new List<string>();
        string stage = "initialize", layout = "flat", mode = "interior", selectionMode = "all";
        string? renderError = null;
        var count = 40000; var sampleCount = 180; var warmupCount = 2;
        var angleStep = .1; var selectedCount = 0; var affectedCount = 0;
        var report = Option("--report", "artifacts/roi-rotation.json");
        var capture = args.Contains("--capture-rotation");
        var undoExact = false; var redoExact = false;
        viewport.RendererStatusChanged += value => { if (value.StartsWith("렌더 오류:") || value.StartsWith("D3D12 ")) renderError = value; };
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            try
            {
                count = int.Parse(Option("--rois", "40000"), CultureInfo.InvariantCulture);
                sampleCount = int.Parse(Option("--rotation-samples", "180"), CultureInfo.InvariantCulture);
                warmupCount = int.Parse(Option("--rotation-warmup", "2"), CultureInfo.InvariantCulture);
                angleStep = double.Parse(Option("--rotation-step", ".1"), CultureInfo.InvariantCulture);
                layout = Option("--rotation-layout", "flat"); mode = Option("--rotation-mode", "interior");
                selectionMode = Option("--rotation-selection", "all");
                if (count < 2 || sampleCount < 1 || warmupCount < 2 || (long)sampleCount + warmupCount > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(args), "ROI >= 2, samples >= 1, warmup >= 2이어야 합니다.");
                if (!double.IsFinite(angleStep) || angleStep == 0 || Math.Abs(angleStep) >= 180)
                    throw new ArgumentOutOfRangeException(nameof(args), "rotation-step은 절댓값이 0보다 크고 180보다 작은 유한한 각도여야 합니다.");
                if (layout is not ("flat" or "pcb") || mode is not ("interior" or "boundary"))
                    throw new ArgumentException("rotation-layout은 flat|pcb, rotation-mode는 interior|boundary입니다.");
                if (selectionMode is not ("all" or "parents" or "half"))
                    throw new ArgumentException("rotation-selection은 all|parents|half입니다.");
                await Save();
                await model.InitializeAsync();
                stage = "fixture"; await Save();
                model.Session.AddRange(RoiInteractionRun.Fixture(count, layout == "pcb").Select(r => mode == "interior"
                    ? new RotatedRect(r.Center * .5 + new PointD(2048, 2048), r.Width * .5, r.Height * .5, r.Angle) : r));
                if (selectionMode == "all") { await model.SelectAll.ExecuteAsync(); await model.EditCompletion; }
                else
                {
                    var ordered = model.Session.State.Rois.Values.OrderBy(r => r.Order);
                    var selected = selectionMode == "parents" ? ordered.Where(r => r.ParentId is null)
                        : ordered.Take((count + 1) / 2);
                    model.Session.Select(selected.Select(r => r.Id));
                }
                selectedCount = model.Session.State.Selection.Count;
                if (model.Session.State.Rois.Count != count || selectedCount == 0)
                    throw new InvalidOperationException("선택 fixture 수가 일치하지 않습니다.");
                await Task.Delay(500); window.UpdateLayout();
                var before = model.Session.State;
                var bounds = before.SelectedSpatial.Bounds;
                var pivot = before.Viewport.ImageToScreen(bounds.Center);
                var handle = before.Viewport.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28);
                var target = handle;
                await MeasureOperation("pointer-down", () => { model.Input.Down(handle); return Task.CompletedTask; });
                using (var probe = new DispatcherProbe())
                {
                    for (var i = 1; i <= warmupCount + sampleCount; i++)
                    {
                        stage = i <= warmupCount ? "warmup" : "rotation";
                        // Every target derives from gesture start, so preview updates never accumulate rounding errors.
                        var degrees = i * angleStep;
                        target = pivot + (handle - pivot).Rotate(degrees);
                        probe.Reset();
                        var timing = MeasureInput(() => model.Input.Move(target));
                        var displayedAngle = model.Session.TransformPreview?.Transform.AngleDegrees;
                        probe.RecordGap();
                        await Task.Delay(16);
                        probe.RecordGap();
                        samples.Add(new { index = i, phase = stage, warmup = i <= warmupCount, requestedDegrees = degrees,
                            displayedAngleDegrees = displayedAngle, timing.StartTicks, timing.CallbackEndTicks, timing.LayoutEndTicks, timing.UtcUnixMs,
                            timing.CallbackMs, timing.LayoutMs, timing.CallbackAllocatedBytes, timing.LayoutAllocatedBytes,
                            timing.ThreadCpuMs, timing.GcPauseMs, probe.MaxGapMs, probe.TickCount });
                        if (renderError is not null) throw new InvalidOperationException(renderError);
                    }
                }
                affectedCount = model.Session.TransformPreview?.Selection.Affected.Count ?? selectedCount;
                await Save();
                if (capture) await Capture("preview");
                await MeasureOperation("commit", () => { model.Input.Up(target); return model.EditCompletion; });
                var after = model.Session.State;
                if (after.Rois.Count != count || !after.Selection.SetEquals(before.Selection))
                    throw new InvalidOperationException("회전 확정 후 ROI/선택이 달라졌습니다.");
                var changed = before.Rois.Any(pair => after.Rois[pair.Key] != pair.Value);
                if (!changed) throw new InvalidOperationException("회전 핸들 입력이 형상 변경을 확정하지 못했습니다.");
                if (capture) await Capture("committed");
                await MeasureOperation("undo", () => model.Undo.ExecuteAsync());
                AssertExact(before, model.Session.State, "Undo"); undoExact = true;
                await MeasureOperation("redo", () => model.Redo.ExecuteAsync());
                AssertExact(after, model.Session.State, "Redo"); redoExact = true;
                if (renderError is not null) throw new InvalidOperationException(renderError);
                stage = "complete"; await Save();
            }
            catch (Exception ex) { await Save(ex.ToString()); Environment.ExitCode = 1; }
            finally { window.Close(); }

            InputTiming MeasureInput(Action action)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var cpu = ThreadCpuClock.Milliseconds(); var pause = GC.GetTotalPauseDuration();
                var utcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var start = Stopwatch.GetTimestamp();
                action();
                var callbackEnd = Stopwatch.GetTimestamp();
                var callbackMs = Stopwatch.GetElapsedTime(start, callbackEnd).TotalMilliseconds;
                var afterCallback = GC.GetAllocatedBytesForCurrentThread();
                var layoutStart = Stopwatch.GetTimestamp();
                window.UpdateLayout();
                var layoutEnd = Stopwatch.GetTimestamp();
                return new(callbackMs, Stopwatch.GetElapsedTime(layoutStart, layoutEnd).TotalMilliseconds,
                    afterCallback - allocated, GC.GetAllocatedBytesForCurrentThread() - afterCallback,
                    ThreadCpuClock.Milliseconds() - cpu, (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                    start, callbackEnd, layoutEnd, utcUnixMs);
            }

            async Task MeasureOperation(string name, Func<Task> execute)
            {
                stage = name; await Save();
                using var probe = new DispatcherProbe();
                var utcUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var allocated = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                var completion = execute();
                var dispatchMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var dispatchAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                await completion;
                var completionMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var layoutStart = Stopwatch.GetTimestamp(); window.UpdateLayout();
                var layoutMs = Stopwatch.GetElapsedTime(layoutStart).TotalMilliseconds;
                probe.RecordGap();
                operations.Add(new { name, startTicks = start, endTicks = Stopwatch.GetTimestamp(), utcUnixMs,
                    dispatchMs, dispatchAllocatedBytes, completionMs, layoutMs,
                    probe.MaxGapMs, probe.TickCount });
                await Save(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(16);
            }

            async Task Capture(string suffix)
            {
                stage = "capture-" + suffix; await Save();
                var path = Path.GetFullPath(Path.ChangeExtension(report, suffix + ".png"));
                if (File.Exists(path)) File.Delete(path);
                viewport.Capture(path);
                for (var i = 0; i < 200 && !File.Exists(path); i++)
                {
                    if (renderError is not null) throw new InvalidOperationException(renderError);
                    await Task.Delay(50);
                }
                if (!File.Exists(path)) throw new TimeoutException("회전 캡처가 완료되지 않았습니다.");
                captures.Add(path);
            }

            Task Save(string? error = null) => File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, stage, count, layout, mode, selectionMode, selectedCount, affectedCount,
                sampleCount, warmupCount, samples, operations, captures, undoExact, redoExact,
                sampleIntervalMs = 16, angleStepDegrees = angleStep, stopwatchFrequency = Stopwatch.Frequency,
                samplePersistence = "buffered; saved once after input loop", error, model.GpuStatus,
                metricsPath = Option("--metrics", ""), renderProfilePath = Option("--render-profile", ""),
                viewport.ViewWidth, viewport.ViewHeight, viewport.Dpi
            }, new JsonSerializerOptions { WriteIndented = true }));
        };

        string Option(string key, string fallback)
        {
            var index = Array.IndexOf(args, key);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException(key + " 값이 필요합니다.");
            return args[index + 1];
        }
    }

    private static void AssertExact(DocumentState expected, DocumentState actual, string operation)
    {
        if (expected.Rois.Count != actual.Rois.Count || !expected.Selection.SetEquals(actual.Selection)
            || expected.Rois.Any(pair => !actual.Rois.TryGetValue(pair.Key, out var roi) || roi != pair.Value))
            throw new InvalidOperationException(operation + "가 ID·형상·이름·부모·필터·선택을 정확히 복원하지 못했습니다.");
    }

    private sealed record InputTiming(double CallbackMs, double LayoutMs, long CallbackAllocatedBytes,
        long LayoutAllocatedBytes, double ThreadCpuMs, double GcPauseMs,
        long StartTicks, long CallbackEndTicks, long LayoutEndTicks, long UtcUnixMs);

    private sealed class DispatcherProbe : IDisposable
    {
        private readonly DispatcherTimer timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
        private long previous;
        public double MaxGapMs { get; private set; }
        public int TickCount { get; private set; }
        public DispatcherProbe() { Reset(); timer.Tick += Tick; timer.Start(); }
        public void Reset() { previous = Stopwatch.GetTimestamp(); MaxGapMs = 0; TickCount = 0; }
        public void RecordGap()
        {
            var now = Stopwatch.GetTimestamp();
            MaxGapMs = Math.Max(MaxGapMs, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds); previous = now;
        }
        private void Tick(object? sender, EventArgs e) { RecordGap(); TickCount++; }
        public void Dispose() { timer.Stop(); timer.Tick -= Tick; }
    }
}
