using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal abstract class EditGesture(IGestureContext context) : IGesture
{
    protected const double MinimumDragDistanceDip = 2;

    protected IGestureContext Context { get; } = context;

    protected IEditorCommandSink Commands => Context.Commands;

    protected ViewportTransform InitialViewport { get; } = context.State.Viewport;

    public virtual void Start()
    {
        // 드래그 시작 상태를 한 번 기억하고, 포인터를 놓을 때 하나의 편집으로 확정한다.
        Commands.BeginPreview();
    }

    public abstract void Move(PointD screen, InputModifiers modifiers);

    protected abstract string Name { get; }

    public void Complete() => Commands.CommitPreview(Name);

    public void Cancel() => Commands.CancelPreview();
}
