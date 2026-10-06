using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RoiDragRun
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
        var count = int.Parse(Option("--rois", "10000"));
        var report = Option("--report", "artifacts/roi-drag.json");
        var interior = args.Contains("--interior");
        var capture = args.Contains("--capture-drag");
        var steps = new List<object>();
        string? renderError = null;
        viewport.RendererStatusChanged += value =>
        {
            if (value.StartsWith("렌더 오류:") || value.StartsWith("D3D12"))
            {
                renderError = value;
            }
        };
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            try
            {
                await model.InitializeAsync();
                model.Session.AddRange(RoiInteractionRun.Fixture(count, false).Select(r => interior
                    ? new RoiGeometry((r.Center * .5) + new PointD(2048, 2048), r.Width * .5, r.Height * .5, r.Angle) : r));
                model.Session.Select(model.Session.State.Rois.Keys);
                await Task.Delay(500);
                var before = model.Session.State;
                var roi = before.Rois.Values.MinBy(r => (r.Geometry.Center - new PointD(4096, 4096)).Length)!;
                var point = before.Viewport.ImageToScreen(roi.Geometry.Center);
                model.Input.Down(point);
                var target = point;
                for (var i = 1; i <= (interior ? 100 : 5); i++)
                {
                    target = point + (interior ? new PointD(20 + (Math.Sin(i * .07) * 40), Math.Cos(i * .07) * 30) : new PointD(i * 10, i * 5));
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    model.Input.Move(target);
                    window.UpdateLayout();
                    steps.Add(new
                    {
                        move = i,
                        milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated
                    });
                    File.WriteAllText(report, JsonSerializer.Serialize(new
                    {
                        count,
                        steps,
                        stage = "drag"
                    }));
                    await Task.Delay(30);
                }
                if (capture)
                {
                    var image = Path.ChangeExtension(report, ".png");
                    viewport.Capture(image);
                    for (var i = 0; i < 100 && !File.Exists(image); i++)
                    {
                        await Task.Delay(50);
                    }

                    if (!File.Exists(image))
                    {
                        throw new TimeoutException("Translation capture did not complete.");
                    }
                }
                double commitMs = 0;
                if (interior)
                {
                    var start = Stopwatch.GetTimestamp();
                    model.Input.Up(target);
                    await model.EditCompletion;
                    window.UpdateLayout();
                    commitMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var after = model.Session.State;
                    if (ReferenceEquals(before.Rois, after.Rois))
                    {
                        throw new InvalidOperationException("Drag did not commit.");
                    }

                    model.Session.Undo();
                    if (!before.Rois.SequenceEqual(model.Session.State.Rois))
                    {
                        throw new InvalidOperationException("Undo changed geometry.");
                    }

                    model.Session.Redo();
                    if (!after.Rois.SequenceEqual(model.Session.State.Rois))
                    {
                        throw new InvalidOperationException("Redo changed geometry.");
                    }

                    after = model.Session.State;
                    model.Input.Down(before.Viewport.ImageToScreen(after.Rois[roi.Id].Geometry.Center));
                    model.Input.Move(target + new PointD(10, 10));
                    model.Input.Cancel();
                    if (!ReferenceEquals(after.Rois, model.Session.State.Rois))
                    {
                        throw new InvalidOperationException("Drag cancellation changed geometry.");
                    }
                }
                else
                {
                    model.Input.Cancel();
                    if (!ReferenceEquals(before.Rois, model.Session.State.Rois))
                    {
                        throw new InvalidOperationException("Drag cancellation changed geometry.");
                    }
                }
                await Task.Delay(300);
                if (renderError is not null)
                {
                    throw new InvalidOperationException(renderError);
                }

                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    count,
                    interior,
                    steps,
                    commitMs,
                    stage = "complete",
                    model.GpuStatus
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }
}
