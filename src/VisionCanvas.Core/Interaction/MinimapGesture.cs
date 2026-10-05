using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class MinimapGesture : IGesture
{
    private readonly EditorSession _editor;
    private readonly MinimapLayout _map;
    private readonly ViewportTransform _before;
    private readonly PointD _start;

    private readonly PointD _grabOffset;
    private PointD _last;

    public MinimapGesture(EditorSession editor, MinimapLayout map, PointD start)
    {
        _editor = editor;
        _map = map;
        _start = _last = start;
        _before = editor.State.Viewport;
        _grabOffset = map.VisibleBounds(_before).Contains(start)
            ? _before.ScreenToImage(new(map.ViewWidth / 2, map.ViewHeight / 2)) - map.MapToImage(start) : new();
        Move(start, InputModifiers.None);
    }

    public void Move(PointD screen, InputModifiers modifiers)
    {
        _last = screen;
        _editor.SetViewport(_map.CenterView(_before, _map.MapToImage(screen) + _grabOffset));
    }

    public void Complete()
    {
        if ((_last - _start).Length < 2)
        {
            _editor.SetViewport(_map.CenterView(_before, _map.MapToImage(_last)));
        }
    }

    public void Cancel() => _editor.SetViewport(_before);
}
