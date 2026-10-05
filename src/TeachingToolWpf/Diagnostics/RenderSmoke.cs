using System.IO;
using System.Windows;
using TeachingToolWpf.Controls;
using VisionCanvas.Controls;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging;

namespace TeachingToolWpf.Diagnostics;

internal static class RenderSmoke
{
    public static void Attach(Window window, Dx12Viewport viewport, Presentation.EditorViewModel model)
    {
        window.ShowInTaskbar = false;
        window.Left = -10000;
        window.Top = -10000;
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/smoke.log", "");
        viewport.RendererStatusChanged += status => File.AppendAllText("artifacts/smoke.log", status + Environment.NewLine);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await model.InitializeAsync();
                await ExplicitHierarchySmokeScenario.Run(window, model, viewport);
                await EditingSmokeScenario.Run(model, viewport);
                await InspectorSmokeScenario.Run(window, model);
                File.AppendAllText("artifacts/smoke.log", "Paged list selection, pan, focused inspector editing and Korean value undo passed" + Environment.NewLine);
                File.AppendAllText("artifacts/smoke.log", "Native input, toolbar, history, grouping and renderer restart passed" + Environment.NewLine);
                await RoiStyleSmokeScenario.Run(window, model, viewport);
                File.AppendAllText("artifacts/smoke.log", "ROI role colors, independent widths, explicit child relation after native movement, Undo/Redo and style reset passed" + Environment.NewLine);
                await ShapeSmokeScenario.Run(window, model, viewport);
                var session = model.Session;
                var parent = session.Add(new(new(4096, 4096), 4400, 4200, 12));
                session.Rename(parent, "메인 검사 영역");
                session.AddChild(parent, new(new(4000, 4000), 1500, 900, 25));
                await MinimapSmokeScenario.Run(model, viewport, "8k");
                File.AppendAllText("artifacts/smoke.log", "8K minimap native click/drag, capture cancellation and history preservation passed" + Environment.NewLine);
                await Task.Delay(2500);
                viewport.Capture("artifacts/viewport-smoke.png");
                await Task.Delay(1500);
                WindowCapture.Save(window, viewport, "artifacts/viewport-smoke.png", "artifacts/app-smoke.png");
                await FilterSmokeScenario.Run(model, viewport);
                File.AppendAllText("artifacts/smoke.log", "OpenCV Binarize/Edge, selection update, image reset/undo and live bounds passed" + Environment.NewLine);
                await Task.Delay(3000);
                viewport.Capture("artifacts/live-smoke.png");
                await Task.Delay(500);
                await model.Live.StopAsync();
                window.WindowState = WindowState.Minimized;
                await Task.Delay(250);
                window.WindowState = WindowState.Normal;
                await viewport.RestartAsync();
                await Task.Delay(500);
                File.AppendAllText("artifacts/smoke.log", "8K live start/stop completed" + Environment.NewLine);
            }
            catch (Exception ex)
            {
                File.AppendAllText("artifacts/smoke.log", ex + Environment.NewLine);
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }
}
