using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class DrawGesture(IGestureContext context, PointD dragStartScreen) : IGesture
{
    private const double MinimumDrawDistanceDip = 4;
    private const double MinimumSizeInImage = .01;

    private readonly ViewportTransform _initialViewport = context.State.Viewport;
    private RoiGeometry? _draft;

    public void Start()
    {
    }

    public void Move(PointD screen, InputModifiers modifiers)
    {
        var image = context.State.Image;
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
            _draft = RoiGeometry.Create(context.DrawShape, (startInImage + endInImage) / 2,
                Math.Abs(startInImage.X - endInImage.X), Math.Abs(startInImage.Y - endInImage.Y));
        }

        var startOnScreen = _initialViewport.ImageToScreen(startInImage);
        var endOnScreen = _initialViewport.ImageToScreen(endInImage);
        context.Show(new(Draft: _draft)
        {
            DraftIsGroup = context.DrawAsGroup,
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

        context.AddDrawnRoi(_draft);
        context.Tool = EditorTool.Select;
    }

    public void Cancel()
    {
    }
}
