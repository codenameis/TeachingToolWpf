using VisionCanvas.SampleShell.Views;
using VisionCanvas.Skia.Controls;

namespace VisionCanvas.SkiaSample.Views;

public sealed class EditorWindow : SampleEditorWindowBase
{
    public EditorWindow()
        : this(new SkiaViewport())
    {
    }

    private EditorWindow(SkiaViewport viewport)
        : base("VisionCanvas · Skia", viewport)
    {
        Viewport = viewport;
    }

    public SkiaViewport Viewport { get; }
}
