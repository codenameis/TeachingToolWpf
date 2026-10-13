using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class SelectionRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        var args = Environment.GetCommandLineArgs();
        var reportIndex = Array.IndexOf(args, "--selection-report");
        var report = reportIndex < 0 ? "artifacts/selection-ui.json" : args[reportIndex + 1];
        var results = new List<object>();
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            string? error = null;
            try
            {
                await model.InitializeAsync();
                model.Session.AddRange(Enumerable.Range(0, 100000).Select(i => new RoiGeometry(
                    new(1000 + (i % 1000 * 6), 1000 + (i / 1000 * 12)), 3, 5, 0)));
                await model.SelectAll.ExecuteAsync();
                model.Copy.Execute(null);
                await model.Paste.ExecuteAsync();
                await model.Paste.ExecuteAsync();
                if (model.RoiCount != 300000)
                {
                    throw new InvalidOperationException($"Expected 300000 pasted ROI, got {model.RoiCount}.");
                }
                var bounds = model.Session.State.Snapshot.Spatial.Bounds;
                var bottoms = model.Session.State.Rois.Values.Select(roi => roi.Geometry.Bounds.Bottom).Order().ToArray();
                foreach (var count in new[] { 150000, 200000 })
                {
                    if (bottoms[count - 1] == bottoms[count])
                    {
                        throw new InvalidOperationException("Fixture boundary cannot select the exact requested count.");
                    }
                    var bottom = (bottoms[count - 1] + bottoms[count]) / 2;
                    for (var iteration = 0; iteration < 3; iteration++)
                    {
                        model.Session.Select([]);
                        await Task.Delay(500);
                        var state = model.Session.State;
                        var margin = 32 / state.Viewport.Scale;
                        var a = state.Viewport.ImageToScreen(new(bounds.Left - margin, bounds.Top - margin));
                        var b = state.Viewport.ImageToScreen(new(bounds.Right + margin, bottom));
                        var tick = Stopwatch.GetTimestamp();
                        var last = tick;
                        var maxGap = 0d;
                        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
                        timer.Tick += (_, _) => Gap();
                        var allocated = GC.GetTotalAllocatedBytes();
                        timer.Start();
                        double dispatchMs;
                        try
                        {
                            model.Input.Down(a);
                            model.Input.Move(b);
                            model.Input.Up(b);
                            dispatchMs = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                            await model.EditCompletion;
                            Gap();
                        }
                        finally
                        {
                            timer.Stop();
                        }
                        var completeMs = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                        if (model.Session.State.Selection.Count != count || !ReferenceEquals(state.Rois, model.Session.State.Rois))
                        {
                            throw new InvalidOperationException($"Selection mismatch: expected {count}, actual {model.Session.State.Selection.Count}.");
                        }
                        var capture = report + $".{count}.{iteration}.png";
                        viewport.Capture(capture);
                        var captured = false;
                        while (!captured && Stopwatch.GetElapsedTime(tick).TotalSeconds < 30)
                        {
                            await Task.Delay(16);
                            try
                            {
                                using var stream = new FileStream(capture, FileMode.Open, FileAccess.Read, FileShare.None);
                                captured = stream.Length > 0;
                            }
                            catch (IOException)
                            {
                                // PNG 기록 완료까지 기다린다. 이 시간에는 인코딩/파일 쓰기도 포함된다.
                            }
                        }
                        if (!captured)
                        {
                            throw new TimeoutException("Selection capture did not complete.");
                        }
                        results.Add(new
                        {
                            count,
                            iteration,
                            dispatchMs,
                            completeMs,
                            maxDispatcherGapMs = maxGap,
                            captureReadyMs = Stopwatch.GetElapsedTime(tick).TotalMilliseconds,
                            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated
                        });
                        void Gap()
                        {
                            var now = Stopwatch.GetTimestamp();
                            maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                            last = now;
                        }
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
                    note = "Main window; 100000 ordinary ROI copied/pasted twice to 300000; empty-to-partial marquee via production controller; iteration0 warmup; captureReadyMs includes PNG encoding/I/O, not physical display latency.",
                    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EditorSession).Assembly.Location))),
                    model.GpuStatus,
                    results,
                    error
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }
        };
    }
}
