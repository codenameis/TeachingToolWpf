using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class MinimapGesture : IGesture
{
    private const double ClickSnapDistanceDip = 2;

    private readonly IGestureContext _context;
    private readonly MinimapLayout _layout;
    private readonly ViewportTransform _initialViewport;
    private readonly PointD _dragStartScreen;
    private readonly PointD _grabOffsetInImage;
    private PointD _lastPointerScreen;

    public MinimapGesture(IGestureContext context, MinimapLayout map, PointD start)
    {
        _context = context;
        _layout = map;
        _lastPointerScreen = start;
        _dragStartScreen = start;
        _initialViewport = context.State.Viewport;
        _grabOffsetInImage = map.VisibleBounds(_initialViewport).Contains(start)
            ? _initialViewport.ScreenToImage(new(map.ViewWidth / 2, map.ViewHeight / 2)) - map.MapToImage(start)
            : new();
    }

    public void Start() => Move(_dragStartScreen, InputModifiers.None);

    public void Move(PointD screen, InputModifiers modifiers)
    {
        _lastPointerScreen = screen;
        _context.Commands.SetViewport(_layout.CenterView(_initialViewport, _layout.MapToImage(screen) + _grabOffsetInImage));
    }

    public void Complete()
    {
        if ((_lastPointerScreen - _dragStartScreen).Length < ClickSnapDistanceDip)
        {
            _context.Commands.SetViewport(_layout.CenterView(_initialViewport, _layout.MapToImage(_lastPointerScreen)));
        }
    }

    public void Cancel() => _context.Commands.SetViewport(_initialViewport);
}
