using System.Windows;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Diagnostics;

internal static class DiagnosticStartup
{
    public static bool TryAttach(Window window, Dx12Viewport viewport, MainWindowViewModel model,
        string[] args, Func<double> computeInitializationMilliseconds)
    {
        if (args.Contains("--sidebar-layout-check"))
        {
            SidebarLayoutRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--cpu-kensa-comparison") || args.Contains("--cpu-cuda-comparison") || args.Contains("--cpu-full8k-comparison"))
        {
            CpuKensaComparisonRun.Attach(window, viewport, model, args.Contains("--cpu-cuda-comparison") || args.Contains("--cpu-full8k-comparison"), args.Contains("--cpu-full8k-comparison"));
        }
        else if (args.Contains("--tdcm-latency"))
        {
            TdcmLatencyRun.Attach(window, model);
        }
        else if (args.Contains("--rotation-inspection-smoke"))
        {
            RotationInspectionSmoke.Attach(window, model);
        }
        else if (args.Contains("--shape-target-smoke") || args.Contains("--paper-latency-smoke"))
        {
            ShapeTargetSmoke.Attach(window, viewport, model, computeInitializationMilliseconds);
        }
        else if (args.Contains("--inspection-smoke"))
        {
            InspectionSmoke.Attach(window, viewport, model);
        }
        else if (args.Contains("--group-interaction-benchmark"))
        {
            GroupInteractionRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--roi-rotation-benchmark"))
        {
            RoiRotationRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--frame-cadence-check"))
        {
            FrameCadenceRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--main-repeated-benchmark"))
        {
            RepeatedDocumentRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--selection-benchmark"))
        {
            SelectionRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--responsive-editing"))
        {
            ResponsiveEditingRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--roi-drag-benchmark"))
        {
            RoiDragRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--roi-render-benchmark"))
        {
            RoiRenderRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--clipboard-check"))
        {
            ClipboardRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--roi-benchmark"))
        {
            RoiInteractionRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--memory-check"))
        {
            ImageReplacementRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--large-image"))
        {
            LargeImageRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--benchmark"))
        {
            BenchmarkRun.Attach(window, viewport, model);
        }
        else if (args.Contains("--smoke"))
        {
            RenderSmoke.Attach(window, viewport, model);
        }
        else
        {
            return false;
        }
        return true;
    }
}
