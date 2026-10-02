using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Reproduces marquee/copy/paste through the actual WPF application command path.</summary>
internal static class ClipboardRun
{
    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        string Option(string name, string fallback) { var i = Array.IndexOf(args, name); return i < 0 ? fallback : args[i + 1]; }
        var layout = Option("--clipboard-layout", "flat");
        var output = Option("--clipboard-report", "artifacts/clipboard-check.json");
        var timings = new List<object>();
        string? renderError = null;
        viewport.RendererStatusChanged += message => { if (message.StartsWith("렌더 오류:")) renderError = message; };
        string stage = "initialize";
        void Save(string? error = null) => File.WriteAllText(output, JsonSerializer.Serialize(new { stage, layout, timings, error, model.GpuStatus }, new JsonSerializerOptions { WriteIndented = true }));
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            try
            {
                await model.InitializeAsync();
                async Task Measure(string name, Action action)
                {
                    stage = name; Save(); var start = Stopwatch.GetTimestamp();
                    action(); var dispatchMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    await model.PasteCompletion; window.UpdateLayout();
                    timings.Add(new { name, dispatchMs, milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds, count = model.RoiCount, selected = model.Session.State.Selection.Count });
                    Save(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(100);
                }
                var count = args.Contains("--clipboard-repeat") ? 300 : 7000;
                await Measure("create", () => model.Session.AddRange(layout == "overlap"
                    ? Enumerable.Repeat(new RotatedRect(new(4096, 4096), 100, 100, 0), count)
                    : RoiInteractionRun.Fixture(count, layout == "pcb")));
                while (count < 7000)
                {
                    await Measure($"select-{count}", () => { model.Input.Down(new(0, 0)); model.Input.Move(new(viewport.ViewWidth, viewport.ViewHeight)); model.Input.Up(new(viewport.ViewWidth, viewport.ViewHeight)); });
                    if (model.Session.State.Selection.Count != count) throw new InvalidOperationException("Repeated marquee selection mismatch.");
                    await Measure($"copy-{count}", () => model.HandleKey(Key.C, true, false));
                    await Measure($"paste-{count}", () => model.HandleKey(Key.V, true, false));
                    count *= 2;
                    if (model.RoiCount != count) throw new InvalidOperationException("Repeated paste mismatch: " + model.Status);
                }
                await Measure("marquee", () => { model.Input.Down(new(0, 0)); model.Input.Move(new(viewport.ViewWidth, viewport.ViewHeight)); model.Input.Up(new(viewport.ViewWidth, viewport.ViewHeight)); });
                if (model.Session.State.Selection.Count != count) throw new InvalidOperationException("Marquee did not select every ROI.");
                await Measure("copy", () => model.HandleKey(Key.C, true, false));
                if (args.Contains("--clipboard-filter"))
                {
                    await Measure("register-filter", () => model.Session.SetFilters(new(RoiFilterKind.Binarize)));
                    stage = "filter-preview"; Save(); await model.Filters.WhenIdle;
                    await Task.Delay(1000);
                    if (renderError is not null) throw new InvalidOperationException(renderError);
                    model.HandleKey(Key.C, true, false);
                }
                var original = model.Session.State;
                await Measure("cancel-paste", () => { model.HandleKey(Key.V, true, false); model.HandleKey(Key.Escape, false, false); });
                if (!ReferenceEquals(original.Rois, model.Session.State.Rois) || !model.CanEdit) throw new InvalidOperationException("Cancelled paste changed the document or left editing disabled.");
                await Measure("paste", () => model.HandleKey(Key.V, true, false));
                if (model.RoiCount != count * 2) throw new InvalidOperationException("Paste failed: " + model.Status);
                var pasted = model.Session.State;
                await Measure("undo", model.Session.Undo);
                if (model.RoiCount != count || !model.Session.State.Selection.SetEquals(original.Selection)) throw new InvalidOperationException("Undo mismatch.");
                await Measure("redo", model.Session.Redo);
                if (model.RoiCount != count * 2 || pasted.Rois.Any(pair => model.Session.State.Rois[pair.Key] != pair.Value)) throw new InvalidOperationException("Redo mismatch.");
                await Measure("pan", () => model.Session.SetViewport(model.Session.State.Viewport.Pan(new(10, 10))));
                await model.Filters.WhenIdle;
                if (renderError is not null) throw new InvalidOperationException(renderError);
                var capture = Path.ChangeExtension(output, ".png");
                viewport.Capture(capture);
                for (var i = 0; i < 100 && !File.Exists(capture); i++) await Task.Delay(100);
                if (!File.Exists(capture)) throw new TimeoutException("Renderer did not complete the paste capture.");
                stage = "complete"; Save();
            }
            catch (Exception ex) { Save(ex.ToString()); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
    }
}
