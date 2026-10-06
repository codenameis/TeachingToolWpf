using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Measures dispatch, total completion and dispatcher gaps separately on the real HWND renderer.</summary>
internal static class ResponsiveEditingRun
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
        var report = Option("--report", "artifacts/responsive-editing.json");
        var synchronous = args.Contains("--synchronous-edits");
        var results = new List<object>();
        string? renderError = null;
        viewport.RendererStatusChanged += value =>
        {
            if (value.StartsWith("렌더 오류:"))
            {
                renderError = value;
            }
        };
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            void Save(string? error = null) => File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                count,
                synchronous,
                results,
                error,
                model.GpuStatus
            }, new JsonSerializerOptions { WriteIndented = true }));
            try
            {
                await model.InitializeAsync();
                model.Session.AddRange(RoiInteractionRun.Fixture(count, false).Select(r => new RoiGeometry((r.Center * .5) + new PointD(2048, 2048), r.Width * .5, r.Height * .5, r.Angle)));
                await Task.Delay(500);
                window.UpdateLayout();
                async Task Measure(string operation, int iteration, Func<Task> execute)
                {
                    var gaps = new List<double>();
                    var navigations = new List<double>();
                    var start = Stopwatch.GetTimestamp();
                    var last = start;
                    void Gap()
                    {
                        var now = Stopwatch.GetTimestamp();
                        gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                        last = now;
                    }
                    var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
                    timer.Tick += (_, _) =>
                    {
                        Gap();
                        var tick = Stopwatch.GetTimestamp();
                        model.Input.Wheel(new(viewport.ViewWidth / 2, viewport.ViewHeight / 2), .01);
                        navigations.Add(Stopwatch.GetElapsedTime(tick).TotalMilliseconds);
                    };
                    var allocated = GC.GetTotalAllocatedBytes();
                    double dispatch;
                    timer.Start();
                    try
                    {
                        var pending = execute();
                        dispatch = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        await pending;
                        window.UpdateLayout();
                        Gap();
                    }
                    finally
                    {
                        timer.Stop();
                    }
                    results.Add(new
                    {
                        operation,
                        iteration,
                        warmup = iteration == 0,
                        dispatchMs = dispatch,
                        completeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                        maxDispatcherGapMs = gaps.Max(),
                        navigationCount = navigations.Count,
                        maxNavigationMs = navigations.Count == 0 ? 0 : navigations.Max(),
                        allocatedBytes = GC.GetTotalAllocatedBytes() - allocated
                    });
                    Save();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(150);
                }
                Task Sync(Action action)
                {
                    action();
                    return Task.CompletedTask;
                }
                for (var iteration = 0; iteration < 4; iteration++)
                {
                    model.Session.Select([]);
                    await Measure("select-all", iteration, () => synchronous ? Sync(() => model.Session.Select(model.Session.State.Rois.Keys)) : model.SelectAll.ExecuteAsync());
                    if (model.Session.State.Selection.Count != count)
                    {
                        throw new InvalidOperationException("Select all mismatch.");
                    }

                    var before = model.Session.State;
                    var center = before.Viewport.ImageToScreen(before.SelectedSpatial.Bounds.Center);
                    // Choose a real ROI interior to exercise the pointer-up commit boundary.
                    var roi = before.Rois.Values.MinBy(r => (r.Geometry.Center - before.SelectedSpatial.Bounds.Center).Length)!;
                    var point = before.Viewport.ImageToScreen(roi.Geometry.Center);
                    model.Input.Down(point);
                    model.Input.Move(point + new PointD(2, 1));
                    await Measure("move-commit", iteration, () => synchronous ? Sync(() =>
                    {
                        model.Session.CommitPreview("ROI 이동");
                        model.Input.Cancel();
                    })
                        : CommitPointer());
                    Task CommitPointer()
                    {
                        model.Input.Up(point + new PointD(2, 1));
                        return model.EditCompletion;
                    }
                    if (ReferenceEquals(before.Rois, model.Session.State.Rois))
                    {
                        throw new InvalidOperationException("Move did not commit.");
                    }

                    var after = model.Session.State;
                    await Measure("undo-move", iteration, () => synchronous ? Sync(model.Session.Undo) : model.Undo.ExecuteAsync());
                    if (before.Rois.Any(pair => model.Session.State.Rois[pair.Key] != pair.Value))
                    {
                        throw new InvalidOperationException("Undo geometry mismatch.");
                    }

                    await Measure("redo-move", iteration, () => synchronous ? Sync(model.Session.Redo) : model.Redo.ExecuteAsync());
                    if (after.Rois.Any(pair => model.Session.State.Rois[pair.Key] != pair.Value))
                    {
                        throw new InvalidOperationException("Redo geometry mismatch.");
                    }

                    await Measure("delete-all", iteration, () => synchronous ? Sync(model.Session.DeleteSelection) : model.Delete.ExecuteAsync());
                    if (model.RoiCount != 0)
                    {
                        throw new InvalidOperationException("Delete mismatch.");
                    }

                    await Measure("undo-delete", iteration, () => synchronous ? Sync(model.Session.Undo) : model.Undo.ExecuteAsync());
                    if (model.RoiCount != count || model.Session.State.Selection.Count != count)
                    {
                        throw new InvalidOperationException("Restore mismatch.");
                    }
                }
                if (renderError is not null)
                {
                    throw new InvalidOperationException(renderError);
                }

                viewport.Capture(Path.ChangeExtension(report, ".png"));
                await Task.Delay(500);
                Save();
            }
            catch (Exception ex)
            {
                Save(ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }
}
