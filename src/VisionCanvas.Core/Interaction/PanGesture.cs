using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class PanGesture(IGestureContext context, PointD dragStartScreen) : IGesture
{
    private readonly ViewportTransform _initialViewport = context.State.Viewport;

    public void Start()
    {
    }

    public void Move(PointD screen, InputModifiers modifiers)
        => context.Commands.SetViewport(_initialViewport.Pan(screen - dragStartScreen));

    public void Complete()
    {
    }

    public void Cancel() => context.Commands.SetViewport(_initialViewport);
}
