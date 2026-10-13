using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

public sealed class RepeatedRoiInput(RepeatedRoiSession session) : IViewportInput
{
    private enum Gesture
    {
        None, Move, Rotate, Pan, Minimap
    }
    private Gesture _gesture;
    private PointD _start;
    private PointD _previous;
    private PointD _pivot;
    private double _degrees;
    private ViewportTransform _view;
    private MinimapLayout? _minimap;
    public EditorTool Tool
    {
        get; set;
    }
    public bool IsDragging => _gesture != Gesture.None;

    public void SetViewportSize(double width, double height)
        => _minimap = MinimapLayout.Create(session.Image.Width, session.Image.Height, width, height);

    public void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
    {
        Cancel();
        _start = screen;
        _view = session.Viewport;
        if (!middle && _minimap is { } map && map.Panel.Contains(screen))
        {
            _gesture = Gesture.Minimap;
            Move(screen);
            return;
        }
        if (middle || Tool == EditorTool.Pan)
        {
            _gesture = Gesture.Pan;
            return;
        }
        var geometry = session.SelectionGeometry;
        if ((HandleGeometry.RotationHandle(geometry, _view) - screen).Length < 10)
        {
            _gesture = Gesture.Rotate;
            _pivot = geometry.Center;
            _previous = _view.ScreenToImage(screen) - _pivot;
            _degrees = 0;
        }
        else
        {
            var hit = session.Grid.HitTest(_view.ScreenToImage(screen), 3 / _view.Scale);
            if (hit is null && !geometry.Contains(_view.ScreenToImage(screen)))
            {
                return;
            }
            if (session.SelectedIndex is not null && hit is { } index && index != session.SelectedIndex)
            {
                session.Select(index);
            }
            _gesture = Gesture.Move;
        }
        session.Begin();
    }

    public void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        Cancel();
        if (Tool == EditorTool.Pan || (_minimap is { } map && map.Panel.Contains(screen)))
        {
            Down(screen, modifiers);
            return;
        }
        if (session.Grid.HitTest(session.Viewport.ScreenToImage(screen), 3 / session.Viewport.Scale) is { } hit)
        {
            session.Select(hit);
        }
    }

    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        switch (_gesture)
        {
            case Gesture.Pan:
                session.SetViewport(_view.Pan(screen - _start));
                break;
            case Gesture.Minimap:
                session.SetViewport(_minimap!.CenterView(_view, _minimap.MapToImage(screen)));
                break;
            case Gesture.Move:
                var delta = (screen - _start).Length < 2 ? default : (screen - _start) / _view.Scale;
                if (modifiers.HasFlag(InputModifiers.Shift))
                {
                    delta = Math.Abs(delta.X) > Math.Abs(delta.Y) ? new(delta.X, 0) : new(0, delta.Y);
                }
                session.PreviewTranslation(delta);
                break;
            case Gesture.Rotate:
                var current = _view.ScreenToImage(screen) - _pivot;
                if (current.Length > 1e-12)
                {
                    _degrees += Math.Atan2((_previous.X * current.Y) - (_previous.Y * current.X),
                        (_previous.X * current.X) + (_previous.Y * current.Y)) * 180 / Math.PI;
                    _previous = current;
                    session.PreviewRotation(modifiers.HasFlag(InputModifiers.Shift) ? Math.Round(_degrees / 15) * 15 : _degrees);
                }
                break;
        }
    }

    public void Up(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        Move(screen, modifiers);
        _gesture = Gesture.None;
        session.Complete();
    }

    public void Cancel()
    {
        if (_gesture is Gesture.Pan or Gesture.Minimap)
        {
            session.SetViewport(_view);
        }
        _gesture = Gesture.None;
        session.Cancel();
    }

    public void Wheel(PointD screen, double notches)
    {
        if (!IsDragging)
        {
            session.SetViewport(session.Viewport.ZoomAt(screen, Math.Pow(1.2, notches)));
        }
    }
}
