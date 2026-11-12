using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal interface IGesture
{
    void Start();

    void Move(PointD screen, InputModifiers modifiers);

    void Complete();

    void Cancel();
}

internal sealed class NoGesture : IGesture
{
    public void Start()
    {
    }

    public void Move(PointD screen, InputModifiers modifiers)
    {
    }

    public void Complete()
    {
    }

    public void Cancel()
    {
    }
}
