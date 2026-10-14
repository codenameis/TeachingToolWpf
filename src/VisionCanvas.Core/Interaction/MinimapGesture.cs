using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class MinimapGesture : IGesture
{
    private readonly EditorSession _editor;
    private readonly MinimapLayout _layout;
    private readonly ViewportTransform _initialViewport;
    private readonly PointD _dragStartScreen;

    private readonly PointD _grabOffsetInImage;
    private PointD _lastPointerScreen;

    public MinimapGesture(EditorSession editor, MinimapLayout map, PointD start)
    {
        _editor = editor;
        _layout = map;
        _lastPointerScreen = start;
        _dragStartScreen = _lastPointerScreen;
        _initialViewport = editor.State.Viewport;
        _grabOffsetInImage = map.VisibleBounds(_initialViewport).Contains(start)
            ? _initialViewport.ScreenToImage(new(map.ViewWidth / 2, map.ViewHeight / 2)) - map.MapToImage(start) : new();
        Move(start, InputModifiers.None);
    }

    public void Move(PointD screen, InputModifiers modifiers)
    {
        _lastPointerScreen = screen;
        _editor.SetViewport(_layout.CenterView(_initialViewport, _layout.MapToImage(screen) + _grabOffsetInImage));
    }

    public void Complete()
    {
        if ((_lastPointerScreen - _dragStartScreen).Length < 2)
        {
            _editor.SetViewport(_layout.CenterView(_initialViewport, _layout.MapToImage(_lastPointerScreen)));
        }
    }

    public void Cancel() => _editor.SetViewport(_initialViewport);
}
