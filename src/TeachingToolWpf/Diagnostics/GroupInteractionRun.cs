using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

/// <summary>수만 개 실제 ROI의 그룹 편집과 계층 선택을 동일한 입력/프레임 구간으로 측정한다.</summary>
internal static class GroupInteractionRun
{
    public static void Attach(Window window, Dx12Viewport viewport, MainWindowViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? fallback : args[index + 1];
        }
        var report = Option("--report", "artifacts/group-interaction.json");
        var count = int.Parse(Option("--rois", "40000"));
        var repeats = int.Parse(Option("--iterations", "3"));
        var phases = new List<object>();
        var selectionSamples = new List<object>();
        string? error = null;
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            try
            {
                if (count < 1 || repeats < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(args));
                }
                await model.InitializeAsync();
                var side = (int)Math.Ceiling(Math.Sqrt(count));
                var pitch = 4000d / side;
                var fixture = new List<RoiCreation>
                {
                    new(new(new(4096, 4096), 7600, 7600, 0)),
                    new(new(new(4096, 4096), 6500, 6500, 0), 0),
                    new(new(new(4096, 4096), 4800, 4800, 0), 1)
                };
                fixture.AddRange(Enumerable.Range(0, count).Select(i => new RoiCreation(
                    new(new(2096 + (((i % side) + .5) * pitch), 2096 + (((i / side) + .5) * pitch)), pitch * .4, pitch * .4, 0), 2)));
                model.Session.AddRange(fixture);
                var ordered = model.Session.State.Rois.Values.OrderBy(roi => roi.Order).ToArray();
                var group = ordered[2].Id;
                var leaf = ordered[3 + (count / 2)].Id;
                var view = model.Session.State.Viewport;
                await Task.Delay(1000);
                for (var iteration = 0; iteration <= repeats; iteration++)
                {
                    model.Session.SetViewport(view);
                    model.Session.Select([group]);
                    await Task.Delay(100);
                    var before = model.Session.State;
                    var point = view.ImageToScreen(before.Rois[group].Geometry.Center);
                    var target = point;
                    var down = Stopwatch.GetTimestamp();
                    Pointer(0x201, point);
                    await Phase("move", iteration, 2000, n =>
                    {
                        var position = n % 100;
                        target = point + new PointD(position < 50 ? position : 100 - position, 10);
                        Pointer(0x200, target);
                    }, () => Task.Delay(2000), down);
                    await Phase("move-commit", iteration, 700, Pan, async () =>
                    {
                        Pointer(0x202, target);
                        await model.EditCompletion;
                    });
                    await CheckHistory(before);
                    model.Session.SetViewport(view);
                    before = model.Session.State;
                    var geometry = before.Rois[group].Geometry;
                    var pivot = view.ImageToScreen(geometry.Center);
                    var handle = HandleGeometry.RotationHandle(geometry, view);
                    target = handle;
                    down = Stopwatch.GetTimestamp();
                    Pointer(0x201, handle);
                    await Phase("rotate", iteration, 2000, n =>
                    {
                        target = pivot + ((handle - pivot) * (1000 / (handle - pivot).Length)).Rotate(n * .08);
                        Pointer(0x200, target);
                    }, () => Task.Delay(2000), down);
                    await Phase("rotate-commit", iteration, 700, Pan, async () =>
                    {
                        Pointer(0x202, target);
                        await model.EditCompletion;
                    });
                    await CheckHistory(before);
                    var document = model.Session.State.Rois;
                    var history = model.Session.HistoryBytes;
                    var zoomed = view.ZoomAt(view.ImageToScreen(document[leaf].Geometry.Center), 4);
                    model.Session.SetViewport(zoomed);
                    var selectionChecks = new List<Task>();
                    async Task SelectionCompleted(long start, Guid expected)
                    {
                        await model.EditCompletion;
                        if (!model.Session.State.Selection.SetEquals([expected]))
                        {
                            throw new InvalidOperationException("Double-click did not select the expected hierarchy level.");
                        }
                        selectionSamples.Add(new
                        {
                            iteration,
                            elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds
                        });
                    }
                    await Phase("double-click-pan", iteration, 2000, n =>
                    {
                        model.Session.SetViewport(zoomed.Pan(new(Math.Sin(n * .025) * 8, 0)));
                        if (n % 15 != 0)
                        {
                            return;
                        }
                        var start = Stopwatch.GetTimestamp();
                        var cycle = new[] { leaf, group, ordered[1].Id, ordered[0].Id };
                        var current = Array.IndexOf(cycle, model.Session.State.Selection.Single());
                        var expected = cycle[(current + 1) % cycle.Length];
                        var hit = model.Session.State.Viewport.ImageToScreen(document[leaf].Geometry.Center);
                        Pointer(0x201, hit);
                        Pointer(0x202, hit);
                        Pointer(0x203, hit);
                        Pointer(0x202, hit);
                        selectionChecks.Add(SelectionCompleted(start, expected));
                    }, () => Task.Delay(2000));
                    await Task.WhenAll(selectionChecks);
                    if (!ReferenceEquals(document, model.Session.State.Rois) || history != model.Session.HistoryBytes)
                    {
                        throw new InvalidOperationException("Double-click changed geometry or history.");
                    }
                }
                void Pan(int n) => model.Session.SetViewport(view.Pan(new(Math.Sin(n * .025) * 8, 0)));
                async Task CheckHistory(DocumentState before)
                {
                    var after = model.Session.State;
                    if (ReferenceEquals(before.Rois, after.Rois))
                    {
                        throw new InvalidOperationException("Edit did not commit: " + model.Status);
                    }
                    await model.Undo.ExecuteAsync();
                    Verify(before);
                    await model.Redo.ExecuteAsync();
                    Verify(after);
                    await model.Undo.ExecuteAsync();
                    Verify(before);
                }
                void Verify(DocumentState expected)
                {
                    if (!expected.Rois.SequenceEqual(model.Session.State.Rois) || !expected.Selection.SetEquals(model.Session.State.Selection))
                    {
                        throw new InvalidOperationException("Undo/Redo did not restore all ROI values and selection.");
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                Environment.ExitCode = 1;
            }
            finally
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    count,
                    repeats,
                    stopwatchFrequency = Stopwatch.Frequency,
                    model.GpuStatus,
                    model.Status,
                    note = "8192x8192 image, ordinary ROI, four levels; production controller and VM; high-resolution 120Hz producer at Send priority; iteration0 warmup; commit/selection include active navigation; off-screen, not physical input/display latency.",
                    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EditorSession).Assembly.Location))),
                    phases,
                    selectionSamples,
                    error
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }
        };

        void Pointer(int message, PointD point)
        {
            // 성능 측정은 사용자의 실제 마우스/포커스와 경합하지 않는다. HWND 라우팅은 별도 smoke에서 검사한다.
            switch (message)
            {
                case 0x201:
                    model.Input.Down(point);
                    break;
                case 0x202:
                    model.Input.Up(point);
                    break;
                case 0x203:
                    model.Input.DoubleClick(point);
                    break;
                case 0x200:
                    model.Input.Move(point);
                    break;
            }
        }

        async Task Phase(string name, int iteration, int minimumMilliseconds, Action<int> input, Func<Task> action, long? begin = null)
        {
            var samples = new List<object>();
            var start = begin ?? Stopwatch.GetTimestamp();
            var pause = GC.GetTotalPauseDuration();
            var allocated = GC.GetTotalAllocatedBytes();
            using var stop = new CancellationTokenSource();
            var producer = Task.Factory.StartNew(() =>
            {
                using var timer = new InputTimer();
                var next = 1;
                while (!stop.IsCancellationRequested)
                {
                    var due = start + (long)(next * Stopwatch.Frequency / 120d);
                    timer.WaitUntil(due);
                    if (stop.IsCancellationRequested)
                    {
                        break;
                    }
                    var created = Stopwatch.GetTimestamp();
                    var sequence = next;
                    window.Dispatcher.Invoke(() =>
                    {
                        var execute = Stopwatch.GetTimestamp();
                        var scene = model.Scene;
                        input(sequence);
                        var end = Stopwatch.GetTimestamp();
                        samples.Add(new
                        {
                            sequence,
                            dueTicks = due,
                            createdTicks = created,
                            executeTicks = execute,
                            endTicks = end,
                            sceneChanged = !ReferenceEquals(scene, model.Scene),
                            previewAngle = model.Session.TransformPreview?.Transform.AngleDegrees,
                            previewX = model.Session.TransformPreview?.Transform.Translation.X
                        });
                    }, DispatcherPriority.Send);
                    next = Math.Max(next + 1, (int)((Stopwatch.GetTimestamp() - start) * 120d / Stopwatch.Frequency) + 1);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            long commandEnd = 0;
            try
            {
                await action();
                commandEnd = Stopwatch.GetTimestamp();
                var remaining = minimumMilliseconds - Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (remaining > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                }
            }
            finally
            {
                stop.Cancel();
                await producer;
                phases.Add(new
                {
                    name,
                    iteration,
                    startTicks = start,
                    commandEndTicks = commandEnd,
                    endTicks = Stopwatch.GetTimestamp(),
                    gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    samples
                });
            }
        }
    }

    private sealed class InputTimer : WaitHandle
    {
        public InputTimer()
        {
            SafeWaitHandle = CreateWaitableTimerExW(0, 0, 2, 0x100002);
            if (SafeWaitHandle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        public void WaitUntil(long timestamp)
        {
            var remaining = timestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }
            var due = -Math.Max(1, (long)(remaining * 10_000_000d / Stopwatch.Frequency));
            if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, 0, 0, false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if (!WaitOne(1000))
            {
                throw new TimeoutException("Diagnostic input timer timed out.");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    }
}
