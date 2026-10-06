using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NetVips;
using TeachingToolWpf.Controls;
using TeachingToolWpf.Presentation;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionCanvas.Imaging.Filters;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Diagnostics;

/// <summary>Repeatable app-level replacement soak; forced collection is diagnostic only.</summary>
internal static class ImageReplacementRun
{
    private sealed record Sample(int Cycle, long PrivateBytes, long WorkingBytes, long ManagedBytes, int Handles, long HistoryBytes);

    public static void Attach(Window window, Dx12Viewport viewport, EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        var args = Environment.GetCommandLineArgs();
        var reportIndex = Array.IndexOf(args, "--memory-report");
        var cyclesIndex = Array.IndexOf(args, "--memory-cycles");
        var cycles = cyclesIndex >= 0 ? int.Parse(args[cyclesIndex + 1]) : 60;
        var report = Path.GetFullPath(reportIndex >= 0 ? args[reportIndex + 1] : "artifacts/memory-replacement.json");
        var samples = new List<Sample>();
        var rendererMessages = new List<string>();
        viewport.RendererStatusChanged += rendererMessages.Add;
        window.Loaded += async (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            try
            {
                var sources = await Task.Run(CreateFixtures);
                await model.InitializeAsync();
                for (var cycle = 1; cycle <= cycles; cycle++)
                {
                    foreach (var reference in sources)
                    {
                        await model.Images.ReplaceAsync(reference);
                        if (model.Session.State.Image?.Key != reference.Key)
                        {
                            throw new InvalidOperationException("Replacement did not complete.");
                        }

                        var id = model.Session.Add(new(new(reference.Width / 2d, reference.Height / 2d), 2000, 1200, 17));
                        model.Filters.SelectedFilter = cycle % 2 == 0 ? RoiFilterKind.Binarize : RoiFilterKind.Edge;
                        model.Filters.Preview.Execute(null);
                        await model.Filters.WhenIdle;
                        if (model.Filters.Result?.Regions.Single().Id != id)
                        {
                            throw new InvalidOperationException("Filter preview did not complete.");
                        }

                        await Task.Delay(150);
                    }
                    if (cycle % 5 == 0)
                    {
                        // Settle pending decode/render work before comparing the same active source.
                        await Task.Delay(500);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        using var process = Process.GetCurrentProcess();
                        process.Refresh();
                        samples.Add(new(cycle, process.PrivateMemorySize64, process.WorkingSet64,
                            GC.GetTotalMemory(false), process.HandleCount, model.Session.HistoryBytes));
                        Save(null);
                    }
                }
                if (rendererMessages.Any(m => m.StartsWith("렌더 오류") || m.Contains("D3D12 Error") || m.Contains("D3D12 Corruption")))
                {
                    throw new InvalidOperationException("Renderer reported errors; inspect the report.");
                }

                Save(null);
            }
            catch (Exception ex)
            {
                Save(ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                viewport.RendererStatusChanged -= rendererMessages.Add;
                window.Close();
            }
        };
        void Save(string? error) => File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            ReplacementsPerCycle = 2,
            WarmupCycles = 10,
            Samples = samples,
            RendererMessages = rendererMessages,
            Error = error,
            Note = "8K RGBA PNG / 24K Gray16 TIFF warm-cache replacements with ROI filter preview. GC runs only at diagnostic checkpoints. Working set includes mapped file pages."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ImageReference[] CreateFixtures()
    {
        var directory = Path.GetFullPath("artifacts/memory-fixtures");
        Directory.CreateDirectory(directory);
        return [Create("8k-rgba.png", 7680, 4320, false), Create("24k-gray16.tif", 24576, 4096, true)];
        ImageReference Create(string name, int width, int height, bool gray16)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                var row = Enumerable.Range(0, width).Select(x => (ushort)(x % 256 * 257)).ToArray();
                using var line = Image.NewFromMemory(row, width, 1, 1, Enums.BandFormat.Ushort);
                using var full = line.Replicate(1, height);
                if (gray16)
                {
                    full.WriteToFile(path, new VOption { { "tile", true }, { "compression", Enums.ForeignTiffCompression.Deflate } });
                }
                else
                {
                    using var gray = full.Linear([1d / 257], [0]);
                    using var bytes = gray.Cast(Enums.BandFormat.Uchar);
                    using var rgb = bytes.Bandjoin([bytes, bytes]);
                    using var rgba = rgb.Bandjoin(255);
                    rgba.WriteToFile(path);
                }
            }
            return new(path, name, width, height, DiskPyramidSource.Version(path));
        }
    }
}
