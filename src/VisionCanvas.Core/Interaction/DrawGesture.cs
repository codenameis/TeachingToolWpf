using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class DrawGesture(InteractionController controller, PointD dragStartScreen) : IGesture
{
    private const double MinimumDrawDistanceDip = 4;
    private const double MinimumSizeInImage = .01;

    private readonly ViewportTransform _initialViewport = controller.Editor.State.Viewport;
    private RoiGeometry? _draft;

    public void Move(PointD screen, InputModifiers modifiers)
    {
        var image = controller.Editor.State.Image;
        var startInImage = RoiImageBounds.Clamp(_initialViewport.ScreenToImage(dragStartScreen), image);
        var endInImage = RoiImageBounds.Clamp(_initialViewport.ScreenToImage(screen), image);
        if ((screen - dragStartScreen).Length < MinimumDrawDistanceDip ||
            Math.Abs(startInImage.X - endInImage.X) < MinimumSizeInImage ||
            Math.Abs(startInImage.Y - endInImage.Y) < MinimumSizeInImage)
        {
            _draft = null;
        }
        else
        {
            _draft = RoiGeometry.Create(controller.DrawShape, (startInImage + endInImage) / 2,
                Math.Abs(startInImage.X - endInImage.X), Math.Abs(startInImage.Y - endInImage.Y));
        }

        var startOnScreen = _initialViewport.ImageToScreen(startInImage);
        var endOnScreen = _initialViewport.ImageToScreen(endInImage);
        controller.Show(new(Draft: _draft)
        {
            DraftIsGroup = controller.DrawAsGroup,
            DragArea = _draft is null ? null : new(
                Math.Min(startOnScreen.X, endOnScreen.X), Math.Min(startOnScreen.Y, endOnScreen.Y),
                Math.Max(startOnScreen.X, endOnScreen.X), Math.Max(startOnScreen.Y, endOnScreen.Y))
        });
    }

    public void Complete()
    {
        if (_draft is null)
        {
            return;
        }

        controller.AddDrawnRoi(_draft);
        controller.Tool = EditorTool.Select;
    }

    public void Cancel()
    {
    }
}
