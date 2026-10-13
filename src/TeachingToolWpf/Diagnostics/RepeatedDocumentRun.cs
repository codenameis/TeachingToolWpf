using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RepeatedDocumentRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        var args = Environment.GetCommandLineArgs();
        var reportIndex = Array.IndexOf(args, "--roi-report");
        var output = reportIndex < 0 ? "artifacts/main-repeated.json" : args[reportIndex + 1];
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var actions = new List<object>();
            var sections = new List<object>();
            string? error = null;
            var session = model.Session;
            try
            {
                await model.InitializeAsync();
                session.Add(new(new(3000, 4096), 20, 20, 0));
                await Action("add-100020001", () => { model.AddRepeatedTest.Execute(null); return Task.CompletedTask; });
                Check(session.State.RoiCount == 100020002, "Main test command/count");
                var id = session.State.RepeatedRois.Keys.Single();
                var grid = session.State.RepeatedRois[id].Parts.Single().Grid;
                await Task.Delay(1000);
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    session.Select([]);
                    await Action($"half-select-{iteration}", async () =>
                    {
                        var view = session.State.Viewport;
                        var a = view.ImageToScreen(new(0, 0));
                        var b = view.ImageToScreen(new(4096, 8192));
                        model.Input.Down(a);
                        model.Input.Move(b);
                        model.Input.Up(b);
                        await model.EditCompletion;
                    });
                    Check(session.State.SelectionCount == 50005001, $"Partial selection: {session.State.SelectionCount}");
                }
                await Action("copy-half", () => { model.Copy.Execute(null); return Task.CompletedTask; });
                await Action("paste-half", () => model.Paste.ExecuteAsync());
                Check(session.State.RoiCount == 150025003 && session.State.SelectionCount == 50005001, "Partial paste/count");
                await Action("undo-paste", () => model.Undo.ExecuteAsync());
                Check(session.State.RoiCount == 100020002, "Undo paste");
                await Action("redo-paste", () => model.Redo.ExecuteAsync());
                Check(session.State.RoiCount == 150025003, "Redo paste");
                await model.Undo.ExecuteAsync();
                await Action("select-all", () => model.SelectAll.ExecuteAsync());
                await Action("copy-all", () => { model.Copy.Execute(null); return Task.CompletedTask; });
                await Action("paste-all", () => model.Paste.ExecuteAsync());
                Check(session.State.RoiCount == 200040004, "Whole paste/count");
                await model.Undo.ExecuteAsync();

                var center = new PointD(viewport.ViewWidth / 2, viewport.ViewHeight / 2);
                var scale = 12 / grid.Pitch;
                session.SetViewport(new(scale, center - (grid.Center * scale)));
                var cell = new RepeatedRoiCell(id, grid.Count / 2);
                NativeDoubleClick(cell);
                Check(session.State.SingleRepeatedCell == cell, "Native first cell selection");
                cell = cell with
                {
                    Index = cell.Index + 2
                };
                NativeDoubleClick(cell);
                Check(session.State.SingleRepeatedCell == cell, "Native double-click replaces selection");
                var original = session.State.RepeatedRois[id].Geometry(cell.Index);
                model.Properties.Width = "2";
                model.Properties.Height = "3";
                model.Properties.Angle = "33";
                model.ApplyProperties.Execute(null);
                Check(session.State.RepeatedRois[id].Geometry(cell.Index).Angle == 33, "Main properties apply");
                await model.Undo.ExecuteAsync();
                Check(session.State.RepeatedRois[id].Geometry(cell.Index) == original, "Individual undo");
                await Capture("detail");
                model.Fit.Execute(null);
                await model.SelectAll.ExecuteAsync();
                await Capture("fit");
                WindowCapture.Save(window, viewport, output + ".fit.png", output + ".window.png");

                var unchanged = session.State.RepeatedRois;
                await Action("native-whole-move", async () =>
                {
                    var point = session.State.Viewport.ImageToScreen(grid.Center);
                    NativeDrag(point, point + new PointD(8, 4));
                    await model.EditCompletion;
                    Check(session.State.RepeatedRois[id].Geometry(cell.Index).Center != original.Center, "Native whole move");
                });
                await model.Undo.ExecuteAsync();
                Check(ReferenceEquals(unchanged, session.State.RepeatedRois), "Native move undo");
                await Action("native-whole-rotation", async () =>
                {
                    var bounds = session.State.TransformSelection.Bounds;
                    var view = session.State.Viewport;
                    var pivot = view.ImageToScreen(bounds.Center);
                    var handle = view.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28);
                    NativeDrag(handle, new RigidTransform2D(pivot, 20, default).Apply(handle));
                    await model.EditCompletion;
                    Check(Math.Abs(session.State.RepeatedRois[id].Geometry(cell.Index).Angle) > 10, "Native whole rotation");
                });
                await model.Undo.ExecuteAsync();
                Check(ReferenceEquals(unchanged, session.State.RepeatedRois), "Native rotation undo");

                if (args.Contains("--fragmented-selection"))
                {
                    session.BeginPreview();
                    session.PreviewRotation(grid.Center, 37);
                    session.CommitPreview("rotated grid fixture");
                    await Action("rotated-partial-select", async () => session.CommitPrepared(
                        await session.PrepareRegionSelectionAsync(new(0, 0, 4096, 8192))));
                    Check(session.State.SelectionCount > 0 && session.State.SelectionCount < grid.Count, "Rotated partial selection");
                    await Capture("rotated-partial");
                    foreach (var detail in new[] { false, true })
                    {
                        if (detail)
                        {
                            session.SetViewport(new(scale, center - (grid.Center * scale)));
                        }
                        else
                        {
                            model.Fit.Execute(null);
                        }
                        var prefix = $"fragmented-{(detail ? "detail" : "fit")}";
                        await Motion(prefix + "-diagonal", t => session.PreviewTranslation(new(Math.Sin(t) * 80, Math.Cos(t) * 80)));
                        var pivot = session.State.TransformSelection.Bounds.Center;
                        await Motion(prefix + "-rotation", t => session.PreviewRotation(pivot, Math.Sin(t) * 45));
                    }
                    return;
                }

                foreach (var partial in new[] { false, true })
                {
                    if (partial)
                    {
                        session.CommitPrepared(await session.PrepareRegionSelectionAsync(new(0, 0, 4096, 8192)));
                    }
                    else
                    {
                        await model.SelectAll.ExecuteAsync();
                    }

                    foreach (var detail in new[] { false, true })
                    {
                        if (detail)
                        {
                            session.SetViewport(new(scale, center - (grid.Center * scale)));
                        }
                        else
                        {
                            model.Fit.Execute(null);
                        }

                        var prefix = $"{(partial ? "partial" : "whole")}-{(detail ? "detail" : "fit")}";
                        await Motion(prefix + "-horizontal", t => session.PreviewTranslation(new(Math.Sin(t) * 80, 0)));
                        await Motion(prefix + "-vertical", t => session.PreviewTranslation(new(0, Math.Sin(t) * 80)));
                        await Motion(prefix + "-diagonal", t => session.PreviewTranslation(new(Math.Sin(t) * 80, Math.Cos(t) * 80)));
                        var pivot = session.State.TransformSelection.Bounds.Center;
                        await Motion(prefix + "-rotation", t => session.PreviewRotation(pivot, Math.Sin(t) * 45));
                    }
                }
                session.SelectRepeated(cell);
                session.SetViewport(new(scale, center - (original.Center * scale)));
                await Motion("individual-move", t => session.PreviewTranslation(new(Math.Sin(t), Math.Cos(t))));
                await Motion("individual-rotation", t => session.PreviewRotation(original.Center, t * 180));
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                Environment.ExitCode = 1;
            }
            finally
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new
                {
                    note = "Main TeachingToolWpf document and commands, 8K image, ordinary + compressed repeated ROI; action wall time and renderer CSV section ticks. Motion samples include main VM publication; physical display/input latency is not measured.",
                    Adapter = model.GpuStatus,
                    LogicalRoiCount = model.RoiCount,
                    session.HistoryBytes,
                    Viewport = new
                    {
                        Width = viewport.ViewWidth,
                        Height = viewport.ViewHeight,
                        viewport.Dpi
                    },
                    LogicalProcessorCount = Environment.ProcessorCount,
                    StopwatchFrequency = Stopwatch.Frequency,
                    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EditorSession).Assembly.Location))),
                    RendererAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12Viewport).Assembly.Location))),
                    RepeatedRoiShaderSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", "RepeatedRoi.hlsl")))),
                    Correctness = error is null ? "PASS" : "FAIL",
                    actions,
                    sections,
                    error
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close();
            }

            async Task Action(string name, Func<Task> action)
            {
                var start = Stopwatch.GetTimestamp();
                var bytes = GC.GetTotalAllocatedBytes();
                await action();
                actions.Add(new
                {
                    name,
                    milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes() - bytes,
                    session.State.SelectionCount,
                    session.State.RoiCount
                });
            }

            async Task Capture(string name)
            {
                var path = output + $".{name}.png";
                viewport.Capture(path);
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed.TotalSeconds < 30)
                {
                    await Task.Delay(50);
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                        if (stream.Length > 0)
                        {
                            return;
                        }
                    }
                    catch (IOException) { /* Wait for the renderer's PNG writer to close the file. */ }
                }
                throw new TimeoutException("Main repeated capture did not complete.");
            }

            async Task Motion(string name, Action<double> update)
            {
                var before = session.State;
                session.BeginPreview();
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalSeconds < .5)
                {
                    update(timer.Elapsed.TotalSeconds);
                    await Task.Delay(1);
                }
                var samples = new List<double>();
                var changes = 0;
                var allocated = GC.GetTotalAllocatedBytes();
                var pause = GC.GetTotalPauseDuration();
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var start = Stopwatch.GetTimestamp();
                timer.Restart();
                while (timer.Elapsed.TotalSeconds < 3)
                {
                    var input = Stopwatch.GetTimestamp();
                    var previous = session.TransformPreview?.Transform;
                    update(timer.Elapsed.TotalSeconds);
                    if (session.TransformPreview?.Transform != previous)
                    {
                        changes++;
                    }

                    samples.Add(Stopwatch.GetElapsedTime(input).TotalMilliseconds);
                    await Task.Delay(1);
                }
                var end = Stopwatch.GetTimestamp();
                Check(changes >= samples.Count * .9, $"{name}: boundary-clamped/static motion must not be counted as active rendering.");
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                samples.Sort();
                sections.Add(new
                {
                    Name = name,
                    SelectedRoiCount = session.State.SelectionCount,
                    SelectedBlocks = session.State.TransformSelection.Repeated.Sum(batch => batch.Parts.Sum(part => part.Blocks.Length)),
                    StartTicks = start,
                    EndTicks = end,
                    InputCount = samples.Count,
                    ChangedSamples = changes,
                    InputP95Milliseconds = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
                    InputMaxMilliseconds = samples[^1],
                    ProcessCpuMilliseconds = cpuMs,
                    AverageCpuCores = cpuMs / Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
                    AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                    GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds
                });
                session.CancelPreview();
                Check(ReferenceEquals(before.RepeatedRois, session.State.RepeatedRois), "Motion cancellation");
            }

            void NativeDoubleClick(RepeatedRoiCell cell)
            {
                var point = session.State.Viewport.ImageToScreen(session.State.RepeatedRois[cell.BatchId].Geometry(cell.Index).Center);
                var packed = (nint)(((int)Math.Round(point.Y * viewport.Dpi) << 16) | ((int)Math.Round(point.X * viewport.Dpi) & 0xffff));
                SendMessage(viewport.Handle, 0x0201, 1, packed);
                SendMessage(viewport.Handle, 0x0202, 0, packed);
                SendMessage(viewport.Handle, 0x0203, 1, packed);
                SendMessage(viewport.Handle, 0x0202, 0, packed);
            }

            void NativeDrag(PointD start, PointD end)
            {
                SendMessage(viewport.Handle, 0x0201, 1, Pack(start));
                SendMessage(viewport.Handle, 0x0200, 1, Pack(end));
                SendMessage(viewport.Handle, 0x0202, 0, Pack(end));
                nint Pack(PointD point) => (nint)(((int)Math.Round(point.Y * viewport.Dpi) << 16) | ((int)Math.Round(point.X * viewport.Dpi) & 0xffff));
            }
        };
    }

    private static void Check(bool valid, string message)
    {
        if (!valid)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
