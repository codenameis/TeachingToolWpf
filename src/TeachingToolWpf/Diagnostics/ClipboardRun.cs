using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Reproduces marquee/copy/paste through the actual WPF application command path.</summary>
internal static class ClipboardRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string name, string fallback)
        {
            var i = Array.IndexOf(args, name);
            return i < 0 ? fallback : args[i + 1];
        }
        var layout = Option("--clipboard-layout", "flat");
        var output = Option("--clipboard-report", "artifacts/clipboard-check.json");
        var timings = new List<object>();
        var checks = new List<string>();
        int? initialCount = null;
        var cycles = 0;
        int? cycle = null;
        var phase = "initial";
        var binaries = new[] { typeof(EditorSession).Assembly, typeof(EditorViewModel).Assembly }.Select(assembly =>
        {
            using var stream = File.OpenRead(assembly.Location);
            return new
            {
                name = assembly.GetName().Name,
                path = assembly.Location,
                sha256 = Convert.ToHexString(SHA256.HashData(stream))
            };
        }).ToArray();
        string? renderError = null;
        viewport.RendererStatusChanged += message =>
        {
            if (message.StartsWith("렌더 오류:"))
            {
                renderError = message;
            }
        };
        string stage = "initialize";
        void Save(string? error = null) => File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            stage,
            layout,
            initialCount,
            cycles,
            warmupCycles = Math.Min(cycles, 2),
            sampleCycles = Math.Max(0, cycles - 2),
            binaries,
            timings,
            checks,
            error,
            model.GpuStatus
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            try
            {
                var repeat = args.Contains("--clipboard-repeat");
                if (!int.TryParse(Option("--clipboard-count", repeat ? "300" : "7000"), out var count) || count <= 0)
                {
                    throw new ArgumentException("--clipboard-count must be a positive integer.");
                }
                initialCount = count;
                if (!int.TryParse(Option("--clipboard-cycles", "0"), out cycles) || cycles < 0 ||
                    (cycles == 0 && args.Contains("--clipboard-cycles")))
                {
                    throw new ArgumentException("--clipboard-cycles must be a positive integer when specified.");
                }
                await model.InitializeAsync();
                Task Measure(string name, Action action) => MeasureAsync(name, () =>
                {
                    action();
                    return Task.CompletedTask;
                });
                async Task MeasureAsync(string name, Func<Task> action)
                {
                    stage = name;
                    Save();
                    var allocatedBefore = GC.GetTotalAllocatedBytes();
                    var start = Stopwatch.GetTimestamp();
                    var previousTick = start;
                    var maxDispatcherGapMs = 0d;
                    var dispatcherTicks = 0;
                    void RecordGap()
                    {
                        var now = Stopwatch.GetTimestamp();
                        maxDispatcherGapMs = Math.Max(maxDispatcherGapMs, Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds);
                        previousTick = now;
                    }
                    var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
                    heartbeat.Tick += (_, _) =>
                    {
                        RecordGap();
                        dispatcherTicks++;
                    };
                    double dispatchMs;
                    heartbeat.Start();
                    try
                    {
                        var pending = action();
                        dispatchMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        await pending;
                        await model.EditCompletion;
                        await model.PasteCompletion;
                        window.UpdateLayout();
                        RecordGap();
                    }
                    finally
                    {
                        heartbeat.Stop();
                    }
                    timings.Add(new
                    {
                        name,
                        phase,
                        cycle,
                        dispatchMs,
                        milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                        maxDispatcherGapMs,
                        dispatcherTicks,
                        allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
                        count = model.RoiCount,
                        selected = model.Session.State.Selection.Count
                    });
                    Save();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Task.Delay(100);
                }
                await Measure("create", () => model.Session.AddRange(RoiInteractionRun.DescribeFixture(layout == "overlap"
                    ? Enumerable.Repeat(new RoiGeometry(new(4096, 4096), 100, 100, 0), count)
                    : RoiInteractionRun.Fixture(count, layout == "pcb"), layout == "pcb")));
                while (repeat && count < 7000)
                {
                    await Measure($"select-{count}", () =>
                    {
                        model.Input.Down(new(0, 0));
                        model.Input.Move(new(viewport.ViewWidth, viewport.ViewHeight));
                        model.Input.Up(new(viewport.ViewWidth, viewport.ViewHeight));
                    });
                    if (model.Session.State.Selection.Count != count)
                    {
                        throw new InvalidOperationException("Repeated marquee selection mismatch.");
                    }

                    await Measure($"copy-{count}", () => model.HandleKey(Key.C, true, false));
                    await Measure($"paste-{count}", () => model.HandleKey(Key.V, true, false));
                    count = checked(count * 2);
                    if (model.RoiCount != count)
                    {
                        throw new InvalidOperationException("Repeated paste mismatch: " + model.Status);
                    }
                }
                await Measure("marquee", () =>
                {
                    model.Input.Down(new(0, 0));
                    model.Input.Move(new(viewport.ViewWidth, viewport.ViewHeight));
                    model.Input.Up(new(viewport.ViewWidth, viewport.ViewHeight));
                });
                if (model.Session.State.Selection.Count != count)
                {
                    throw new InvalidOperationException("Marquee did not select every ROI.");
                }

                await Measure("copy", () => model.HandleKey(Key.C, true, false));
                if (args.Contains("--clipboard-filter"))
                {
                    await Measure("register-filter", () => model.Session.SetFilters(new(RoiFilterKind.Binarize)));
                    stage = "filter-preview";
                    Save();
                    await model.Filters.WhenIdle;
                    await Task.Delay(1000);
                    if (renderError is not null)
                    {
                        throw new InvalidOperationException(renderError);
                    }

                    model.HandleKey(Key.C, true, false);
                }
                var original = model.Session.State;
                async Task CancelPaste()
                {
                    await Task.Delay(20);
                    model.HandleKey(Key.Escape, false, false);
                }
                Task cancellation = Task.CompletedTask;
                await Measure("cancel-paste", () =>
                {
                    model.HandleKey(Key.V, true, false);
                    cancellation = CancelPaste();
                });
                await cancellation;
                if (!ReferenceEquals(original.Rois, model.Session.State.Rois) || !model.CanEdit)
                {
                    throw new InvalidOperationException("Cancelled paste changed the document or left editing disabled.");
                }

                await Measure("paste", () => model.HandleKey(Key.V, true, false));
                if (model.RoiCount != count * 2)
                {
                    throw new InvalidOperationException("Paste failed: " + model.Status);
                }

                var pasted = model.Session.State;
                VerifyPaste(original, pasted, original.Rois.Values.Max(roi => roi.Order));
                VerifyRows();
                checks.Add("paste fresh IDs, parent relationships, metadata, selection and paged rows");
                await MeasureAsync("undo", () => model.Undo.ExecuteAsync());
                VerifyState(original, model.Session.State, "Undo");
                VerifyRows();
                checks.Add("undo exact ROI values, selection, image, viewport and paged rows");
                await MeasureAsync("redo", () => model.Redo.ExecuteAsync());
                VerifyState(pasted, model.Session.State, "Redo");
                VerifyRows();
                checks.Add("redo exact ROI values, selection, image, viewport and paged rows");

                for (var iteration = 1; iteration <= cycles; iteration++)
                {
                    cycle = iteration;
                    phase = iteration <= 2 ? "warmup" : "sample";
                    var prefix = $"{phase}-{iteration}";
                    var orderBase = model.Session.State.Rois.Values.Max(roi => roi.Order);
                    await MeasureAsync(prefix + "-undo", () => model.Undo.ExecuteAsync());
                    VerifyState(original, model.Session.State, "Repeated Undo");
                    VerifyRows();
                    await Measure(prefix + "-copy", () => model.Copy.Execute(null));
                    VerifyState(original, model.Session.State, "Repeated Copy");
                    VerifyRows();
                    await MeasureAsync(prefix + "-paste", () => model.Paste.ExecuteAsync());
                    VerifyPaste(original, model.Session.State, orderBase);
                    VerifyRows();
                    checks.Add(prefix + " exact undo/copy state, fresh pasted IDs, parents, root selection and paged rows");
                }
                cycle = null;
                phase = "post";
                await Measure("pan", () => model.Session.SetViewport(model.Session.State.Viewport.Pan(new(10, 10))));
                await model.Filters.WhenIdle;
                if (renderError is not null)
                {
                    throw new InvalidOperationException(renderError);
                }

                var capture = Path.ChangeExtension(output, ".png");
                viewport.Capture(capture);
                for (var i = 0;
i < 100 && !File.Exists(capture);
i++)
                {
                    await Task.Delay(100);
                }

                if (!File.Exists(capture))
                {
                    throw new TimeoutException("Renderer did not complete the paste capture.");
                }

                stage = "complete";
                Save();

                void VerifyRows()
                {
                    var state = model.Session.State;
                    var rows = model.Rows.ToDictionary(row => row.Id);
                    var pageLimit = state.Rois.Count > 4096 ? 512 : 4096;
                    if (rows.Count != state.Rois.Count || model.VisibleRows.Count != Math.Min(pageLimit, rows.Count) ||
                        model.VisibleRows.Any(row => !rows.TryGetValue(row.Id, out var expected) || row != expected))
                    {
                        throw new InvalidOperationException("ROI row count or displayed page mismatch.");
                    }
                    foreach (var row in rows.Values)
                    {
                        var roi = state.Rois[row.Id];
                        var depth = roi.ParentId is { } parent ? rows[parent].Depth + 1 : 0;
                        var children = state.Snapshot.Children.GetValueOrDefault(roi.Id, []).Length;
                        if (row.Label != roi.Name || row.Depth != depth || row.Kind != RoiRow.Describe(roi, children))
                        {
                            throw new InvalidOperationException("ROI row metadata or hierarchy mismatch.");
                        }
                    }
                }
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

    private static void VerifyState(DocumentState expected, DocumentState actual, string operation)
    {
        if (expected.Rois.Count != actual.Rois.Count || expected.Image != actual.Image || expected.Viewport != actual.Viewport ||
            !expected.Selection.SetEquals(actual.Selection) || expected.Rois.Any(pair => !actual.Rois.TryGetValue(pair.Key, out var roi) || pair.Value != roi))
        {
            throw new InvalidOperationException(operation + " did not restore the exact document and selection.");
        }
    }

    private static void VerifyPaste(DocumentState original, DocumentState pasted, long orderBase)
    {
        var source = original.Rois.Values.OrderBy(roi => roi.Order).ToArray();
        var copies = pasted.Rois.Values.Where(roi => !original.Rois.ContainsKey(roi.Id)).OrderBy(roi => roi.Order).ToArray();
        if (copies.Length != source.Length || !pasted.Selection.SetEquals(copies.Where(roi => roi.ParentId is null).Select(roi => roi.Id)) ||
            original.Rois.Any(pair => !pasted.Rois.TryGetValue(pair.Key, out var roi) || pair.Value != roi))
        {
            throw new InvalidOperationException("Paste changed existing ROI values or selected an incorrect copy set.");
        }
        var ids = source.Select((roi, index) => (roi.Id, CopyId: copies[index].Id)).ToDictionary(pair => pair.Id, pair => pair.CopyId);
        for (var i = 0; i < source.Length; i++)
        {
            var expectedParent = source[i].ParentId is { } parent ? ids[parent] : (Guid?)null;
            if (copies[i].ParentId != expectedParent || copies[i].Name != source[i].Name[..Math.Min(source[i].Name.Length, 253)] + " 복사" || copies[i].Filter != source[i].Filter ||
                copies[i].IsGroup != source[i].IsGroup || copies[i].Order != orderBase + i + 1 ||
                copies[i].Geometry.Width != source[i].Geometry.Width || copies[i].Geometry.Height != source[i].Geometry.Height ||
                copies[i].Geometry.Angle != source[i].Geometry.Angle || copies[i].Geometry.Shape != source[i].Geometry.Shape)
            {
                throw new InvalidOperationException("Paste did not preserve source metadata, shape, order or explicit parent relationships.");
            }
        }
    }
}
