using System.Windows;
using TeachingToolWpf.ViewModels;
using VisionCanvas.Controls;

namespace TeachingToolWpf.Diagnostics;

/// <summary>명령줄 인자로 진단 실행 하나를 골라 창에 붙인다. 인자를 해석하는 곳은 여기 한 곳뿐이다.</summary>
/// <remarks>
/// <para>예전에는 if/else 사슬이었다. 진단을 하나 더할 때마다 분기를 끼워 넣어야 했고, 인자 하나를 빠뜨리면
/// 조용히 아무 일도 일어나지 않았다. 지금은 <see cref="s_runs"/> 표에 한 줄만 추가하면 된다(개방-폐쇄 원칙).</para>
/// <para>표의 순서는 의미가 없다. 한 인자가 두 줄에 걸리지 않기 때문이다. 같은 실행을 여러 인자로 여는 경우는
/// 한 줄에 인자를 여러 개 적는다(예: CPU 비교 4종).</para>
/// <para>진단은 시작할 때 한 번만 도는 코드라 할당을 아끼지 않는다. 대신 읽기 쉬운 쪽을 고른다.</para>
/// </remarks>
internal static class DiagnosticStartup
{
    /// <summary>진단 실행에 넘기는 값 묶음. 각 실행은 자기가 쓰는 것만 꺼내 쓴다.</summary>
    /// <param name="Args">명령줄 인자 전체. 실행마다 플래그 조합이 달라서 함께 넘긴다.</param>
    /// <param name="InitializationMilliseconds">창이 뜨기까지 걸린 시간을 재는 함수. 일부 측정 실행만 쓴다.</param>
    private readonly record struct Context(
        Window Window,
        Dx12Viewport Viewport,
        MainWindowViewModel Model,
        string[] Args,
        Func<double> InitializationMilliseconds)
    {
        /// <summary>이 인자가 명령줄에 있었는지 확인한다.</summary>
        public bool Has(string option) => Array.Exists(Args, arg => arg == option);
    }

    /// <summary>인자 묶음 → 실행. 한 줄이 곧 진단 하나다.</summary>
    private static readonly (string[] Options, Action<Context> Attach)[] s_runs =
    [
        (["--sidebar-layout-check"], c => SidebarLayoutRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--cpu-kensa-comparison", "--cpu-cuda-comparison", "--cpu-full8k-comparison", "--cpu-modules-comparison"],
            AttachCpuComparison),
        (["--rotation-inspection-smoke"], c => RotationInspectionSmoke.Attach(c.Window, c.Model)),
        (["--shape-target-smoke", "--paper-latency-smoke"],
            c => ShapeTargetSmoke.Attach(c.Window, c.Viewport, c.Model, c.InitializationMilliseconds)),
        (["--inspection-smoke"], c => InspectionSmoke.Attach(c.Window, c.Viewport, c.Model)),
        (["--group-interaction-benchmark"], c => GroupInteractionRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--roi-rotation-benchmark"], c => RoiRotationRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--frame-cadence-check"], c => FrameCadenceRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--main-repeated-benchmark"], c => RepeatedDocumentRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--selection-benchmark"], c => SelectionRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--responsive-editing"], c => ResponsiveEditingRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--roi-drag-benchmark"], c => RoiDragRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--roi-render-benchmark"], c => RoiRenderRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--clipboard-check"], c => ClipboardRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--roi-benchmark"], c => RoiInteractionRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--memory-check"], c => ImageReplacementRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--large-image"], c => LargeImageRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--benchmark"], c => BenchmarkRun.Attach(c.Window, c.Viewport, c.Model)),
        (["--smoke"], c => RenderSmoke.Attach(c.Window, c.Viewport, c.Model)),
    ];

    /// <summary>명령줄에 해당하는 진단이 있으면 붙이고 true를 돌려준다. 없으면 아무것도 하지 않고 false.</summary>
    public static bool TryAttach(Window window, Dx12Viewport viewport, MainWindowViewModel model,
        string[] args, Func<double> computeInitializationMilliseconds)
    {
        var context = new Context(window, viewport, model, args, computeInitializationMilliseconds);
        foreach (var (options, attach) in s_runs)
        {
            if (!Array.Exists(options, context.Has))
            {
                continue;
            }

            attach(context);
            return true;
        }

        return false;
    }

    /// <summary>CPU 비교 4종은 같은 실행에 다른 플래그만 켠다. 플래그 조합 계산은 여기서만 한다.</summary>
    private static void AttachCpuComparison(Context context)
    {
        var modules = context.Has("--cpu-modules-comparison");
        var compareCuda = modules || context.Has("--cpu-cuda-comparison") || context.Has("--cpu-full8k-comparison");
        var full8k = modules || context.Has("--cpu-full8k-comparison");
        CpuKensaComparisonRun.Attach(context.Window, context.Viewport, context.Model, compareCuda, full8k, modules);
    }
}
