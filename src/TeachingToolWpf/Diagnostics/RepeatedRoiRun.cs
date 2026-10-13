using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TeachingToolWpf.Presentation;
using TeachingToolWpf.Views;
using VisionCanvas.Controls;
using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

internal static class RepeatedRoiRun
{
    public static async Task RunAsync(RepeatedRoiWindow window, Dx12Viewport viewport, RepeatedRoiViewModel model)
    {
        var args = Environment.GetCommandLineArgs();
        var reportIndex = Array.IndexOf(args, "--roi-report");
        var output = reportIndex >= 0 ? args[reportIndex + 1] : "artifacts/repeated-roi.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var sections = new List<object>();
        var session = model.Session;
        await Task.Delay(1800);
        var baseline = session.Grid;
        var centerIndex = baseline.Count / 2;
        model.Detail.Execute(null);
        var screen = session.Viewport.ImageToScreen(baseline.Geometry(centerIndex).Center);
        SendMessage(viewport.Handle, 0x0201, 1, Pack(screen, viewport.Dpi));
        SendMessage(viewport.Handle, 0x0202, 0, Pack(screen, viewport.Dpi));
        SendMessage(viewport.Handle, 0x0203, 1, Pack(screen, viewport.Dpi));
        SendMessage(viewport.Handle, 0x0202, 0, Pack(screen, viewport.Dpi));
        if (session.SelectedIndex != centerIndex)
        {
            throw new InvalidOperationException("Native double-click did not select the center cell.");
        }
        var otherIndex = centerIndex + 2;
        screen = session.Viewport.ImageToScreen(baseline.Geometry(otherIndex).Center);
        SendMessage(viewport.Handle, 0x0201, 1, Pack(screen, viewport.Dpi));
        SendMessage(viewport.Handle, 0x0202, 0, Pack(screen, viewport.Dpi));
        SendMessage(viewport.Handle, 0x0203, 1, Pack(screen, viewport.Dpi));
        SendMessage(viewport.Handle, 0x0202, 0, Pack(screen, viewport.Dpi));
        if (session.SelectedIndex != otherIndex)
        {
            throw new InvalidOperationException("Native double-click did not replace the existing selection.");
        }
        model.Move(new(1, 1));
        model.Rotate(33);
        var edited = session.Grid;
        session.Undo();
        session.Undo();
        if (!ReferenceEquals(session.Grid, baseline))
        {
            throw new InvalidOperationException("Individual edit undo did not restore the grid.");
        }
        session.Redo();
        session.Redo();
        if (!ReferenceEquals(session.Grid, edited) || session.Grid.Count <= 100_000_000)
        {
            throw new InvalidOperationException("Individual redo/count validation failed.");
        }
        viewport.Capture(output + ".detail.png");
        await Task.Delay(500);
        session.Select(null);
        model.Fit.Execute(null);
        viewport.Capture(output + ".fit.png");
        await Task.Delay(500);
        var captureWait = Stopwatch.StartNew();
        while (!File.Exists(output + ".fit.png") && captureWait.Elapsed.TotalSeconds < 15)
        {
            await Task.Delay(100);
        }
        WindowCapture.Save(window, viewport, output + ".fit.png", output + ".window.png");

        foreach (var detail in new[] { false, true })
        {
            (detail ? model.Detail : model.Fit).Execute(null);
            await MeasureAsync(detail ? "detail-horizontal" : "fit-horizontal", t => session.PreviewTranslation(new(Math.Sin(t) * 100, 0)));
            await MeasureAsync(detail ? "detail-vertical" : "fit-vertical", t => session.PreviewTranslation(new(0, Math.Sin(t) * 100)));
            await MeasureAsync(detail ? "detail-diagonal" : "fit-diagonal", t => session.PreviewTranslation(new(Math.Sin(t) * 100, Math.Cos(t) * 100)));
            await MeasureAsync(detail ? "detail-rotation" : "fit-rotation", t => session.PreviewRotation(t * 180));
        }
        session.Select(centerIndex);
        model.Detail.Execute(null);
        await MeasureAsync("individual-move", t => session.PreviewTranslation(new(Math.Sin(t), Math.Cos(t))));
        await MeasureAsync("individual-rotation", t => session.PreviewRotation(t * 180));

        var completed = new
        {
            Date = DateTimeOffset.Now,
            LogicalRoiCount = session.Grid.Count,
            StoredOverrides = session.Grid.Overrides.Count,
            Image = new
            {
                session.Image.Width,
                session.Image.Height
            },
            Viewport = new
            {
                viewport.ActualWidth,
                viewport.ActualHeight,
                viewport.Dpi
            },
            window.Adapter,
            Correctness = "PASS: native double-click replacement, individual move/rotation/undo/redo, exact count, preview cancellation",
            Scope = "Repeated rectangular grid. Summary at low zoom, exact per-cell outline at detail. Frame CSV contains actual renderer submissions; physical input/display latency is not measured.",
            StopwatchFrequency = Stopwatch.Frequency,
            LogicalProcessorCount = Environment.ProcessorCount,
            RendererAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12Viewport).Assembly.Location))),
            RepeatedRoiShaderSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", "RepeatedRoi.hlsl")))),
            Sections = sections
        };
        File.WriteAllText(output, JsonSerializer.Serialize(completed, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();

        async Task MeasureAsync(string name, Action<double> update)
        {
            var before = session.Grid;
            session.Begin();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 1)
            {
                update(clock.Elapsed.TotalSeconds);
                await Task.Delay(1);
            }
            var samples = new List<double>();
            var gc = GC.GetTotalPauseDuration();
            var allocated = GC.GetTotalAllocatedBytes();
            using var process = Process.GetCurrentProcess();
            var processorStart = process.TotalProcessorTime;
            var start = Stopwatch.GetTimestamp();
            clock.Restart();
            while (clock.Elapsed.TotalSeconds < 4)
            {
                var inputStart = Stopwatch.GetTimestamp();
                update(clock.Elapsed.TotalSeconds);
                samples.Add(Stopwatch.GetElapsedTime(inputStart).TotalMilliseconds);
                await Task.Delay(1);
            }
            var end = Stopwatch.GetTimestamp();
            var processorMilliseconds = (process.TotalProcessorTime - processorStart).TotalMilliseconds;
            var pause = (GC.GetTotalPauseDuration() - gc).TotalMilliseconds;
            var bytes = GC.GetTotalAllocatedBytes() - allocated;
            session.Cancel();
            if (!ReferenceEquals(before, session.Grid))
            {
                throw new InvalidOperationException("Preview cancellation did not restore the exact snapshot.");
            }
            samples.Sort();
            sections.Add(new
            {
                Name = name,
                StartTicks = start,
                EndTicks = end,
                InputCount = samples.Count,
                InputP95Milliseconds = samples[(int)Math.Ceiling(samples.Count * .95) - 1],
                InputMaxMilliseconds = samples[^1],
                ProcessCpuMilliseconds = processorMilliseconds,
                AverageCpuCores = processorMilliseconds / Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
                AllocatedBytes = bytes,
                GcPauseMilliseconds = pause,
                PrivateBytes = process.PrivateMemorySize64
            });
        }
    }

    private static nint Pack(PointD point, double dpi) => (nint)(((int)Math.Round(point.Y * dpi) << 16) | ((int)Math.Round(point.X * dpi) & 0xffff));

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
