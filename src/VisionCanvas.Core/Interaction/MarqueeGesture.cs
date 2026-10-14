using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class MarqueeGesture(InteractionController controller, PointD dragStartScreen, bool additive) : IGesture
{
    private readonly DocumentState _baseline = controller.Editor.State;
    private BoundsD _screenBounds = new(dragStartScreen.X, dragStartScreen.Y, dragStartScreen.X, dragStartScreen.Y);

    public void Move(PointD screen, InputModifiers modifiers)
    {
        _screenBounds = new(Math.Min(dragStartScreen.X, screen.X), Math.Min(dragStartScreen.Y, screen.Y), Math.Max(dragStartScreen.X, screen.X), Math.Max(dragStartScreen.Y, screen.Y));
        controller.Show(new(Marquee: _screenBounds));
    }

    public void Complete()
    {
        var imageTopLeft = _baseline.Viewport.ScreenToImage(new(_screenBounds.Left, _screenBounds.Top));
        var imageBottomRight = _baseline.Viewport.ScreenToImage(new(_screenBounds.Right, _screenBounds.Bottom));
        controller.SelectRegion(_baseline, new(imageTopLeft.X, imageTopLeft.Y, imageBottomRight.X, imageBottomRight.Y), additive);
    }

    public void Cancel()
    {
    }
}
