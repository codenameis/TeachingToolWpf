using VisionCanvas.SampleShell.Views;
using VisionCanvas.Wgpu.Controls;

namespace VisionCanvas.WgpuSample.Views;

public sealed class EditorWindow : SampleEditorWindowBase
{
    public EditorWindow()
        : this(new WgpuViewport())
    {
    }

    private EditorWindow(WgpuViewport viewport)
        : base("VisionCanvas · Wgpu", viewport)
    {
        Viewport = viewport;
    }

    public WgpuViewport Viewport { get; }
}
