using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal abstract class EditGesture : IGesture
{
    protected const double MinimumDragDistanceDip = 2;

    protected InteractionController Controller
    {
        get;
    }

    protected EditorSession Editor
    {
        get;
    }

    protected ViewportTransform InitialViewport
    {
        get;
    }

    protected EditGesture(InteractionController controller)
    {
        Controller = controller;
        Editor = controller.Editor;
        InitialViewport = Editor.State.Viewport;
        // 드래그 시작 상태를 한 번 기억하고, 포인터를 놓을 때 하나의 편집으로 확정한다.
        Editor.BeginPreview();
    }

    public abstract void Move(PointD screen, InputModifiers modifiers);

    protected abstract string Name
    {
        get;
    }

    public void Complete() => Controller.CommitPreview(Name);

    public void Cancel() => Editor.CancelPreview();
}
