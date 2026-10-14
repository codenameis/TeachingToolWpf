using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class PanGesture(EditorSession editor, PointD dragStartScreen) : IGesture
{
    private readonly ViewportTransform _initialViewport = editor.State.Viewport;

    public void Move(PointD screen, InputModifiers modifiers) => editor.SetViewport(_initialViewport.Pan(screen - dragStartScreen));

    public void Complete()
    {
    }

    public void Cancel() => editor.SetViewport(_initialViewport);
}
