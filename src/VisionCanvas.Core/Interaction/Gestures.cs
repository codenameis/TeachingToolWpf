using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal interface IGesture
{
    void Move(PointD screen, InputModifiers modifiers);
    void Complete();
    void Cancel();
}
internal sealed class NoGesture : IGesture
{
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
internal sealed class PanGesture(EditorSession editor, PointD start) : IGesture
{
    private readonly ViewportTransform _view = editor.State.Viewport;

    public void Move(PointD screen, InputModifiers modifiers) => editor.SetViewport(_view.Pan(screen - start));

    public void Complete()
    {
    }

    public void Cancel() => editor.SetViewport(_view);
}
internal abstract class EditGesture : IGesture
{
    protected InteractionController Controller
    {
        get;
    }

    protected EditorSession Editor
    {
        get;
    }

    protected ViewportTransform View
    {
        get;
    }

    protected EditGesture(InteractionController controller)
    {
        Controller = controller;
        Editor = controller.Editor;
        View = Editor.State.Viewport;
        Editor.BeginPreview();
    }

    public abstract void Move(PointD screen, InputModifiers modifiers);

    protected abstract string Name
    {
        get;
    }

    public void Complete() => Controller.CommitPreview(Name);

    public void Cancel() => Editor.CancelPreview();
}
internal sealed class MoveGesture(InteractionController controller, PointD start) : EditGesture(controller)
{
    protected override string Name => "ROI 이동";

    public override void Move(PointD screen, InputModifiers modifiers)
    {
        var delta = (screen - start) / View.Scale;
        if ((screen - start).Length < 2)
        {
            delta = new();
        }

        if (modifiers.HasFlag(InputModifiers.Shift))
        {
            delta = Math.Abs(delta.X) > Math.Abs(delta.Y) ? new(delta.X, 0) : new(0, delta.Y);
        }

        Controller.PreviewTranslation(delta);
    }
}
internal sealed class ResizeGesture(InteractionController controller, Roi original, int handle, PointD start) : EditGesture(controller)
{
    protected override string Name => "ROI 크기 조절";

    public override void Move(PointD screen, InputModifiers modifiers)
        => Editor.PreviewResize(original.Id, (screen - start).Length < 2
            ? original.Geometry : HandleGeometry.Resize(original.Geometry, handle, View.ScreenToImage(screen)));
}
internal sealed class RepeatedResizeGesture(InteractionController controller, RepeatedRoiCell cell, RoiGeometry original, int handle, PointD start) : EditGesture(controller)
{
    protected override string Name => "ROI 크기 조절";
    public override void Move(PointD screen, InputModifiers modifiers)
        => Editor.PreviewRepeatedGeometry(cell, (screen - start).Length < 2 ? original
            : HandleGeometry.Resize(original, handle, View.ScreenToImage(screen)));
}
internal sealed class RotateGesture(InteractionController controller, PointD start, PointD pivot) : EditGesture(controller)
{
    private PointD _previous = controller.Editor.State.Viewport.ScreenToImage(start) - pivot;
    private double _accumulatedDegrees;

    protected override string Name => "ROI 회전";

    public override void Move(PointD screen, InputModifiers modifiers)
    {
        var current = View.ScreenToImage(screen) - pivot;
        if (current.Length < 1e-12)
        {
            return;
        }

        _accumulatedDegrees += Math.Atan2((_previous.X * current.Y) - (_previous.Y * current.X),
            (_previous.X * current.X) + (_previous.Y * current.Y)) * 180 / Math.PI;
        _previous = current;
        var degrees = _accumulatedDegrees;
        if (modifiers.HasFlag(InputModifiers.Shift))
        {
            degrees = Math.Round(degrees / 15) * 15;
        }

        Controller.PreviewRotation(pivot, degrees);
    }
}
internal sealed class DrawGesture(InteractionController controller, PointD start) : IGesture
{
    private readonly ViewportTransform _view = controller.Editor.State.Viewport;
    private RoiGeometry? _draft;

    public void Move(PointD screen, InputModifiers modifiers)
    {
        var image = controller.Editor.State.Image;
        var a = RoiImageBounds.Clamp(_view.ScreenToImage(start), image);
        var b = RoiImageBounds.Clamp(_view.ScreenToImage(screen), image);
        _draft = (screen - start).Length < 4 || Math.Abs(a.X - b.X) < .01 || Math.Abs(a.Y - b.Y) < .01 ? null
            : RoiGeometry.Create(controller.DrawShape, (a + b) / 2, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        var first = _view.ImageToScreen(a);
        var last = _view.ImageToScreen(b);
        controller.Show(new(Draft: _draft)
        {
            DragArea = _draft is null ? null : new(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Max(first.X, last.X), Math.Max(first.Y, last.Y))
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
internal sealed class MarqueeGesture(InteractionController controller, PointD start, bool additive) : IGesture
{
    private readonly DocumentState _baseline = controller.Editor.State;
    private BoundsD _bounds = new(start.X, start.Y, start.X, start.Y);

    public void Move(PointD screen, InputModifiers modifiers)
    {
        _bounds = new(Math.Min(start.X, screen.X), Math.Min(start.Y, screen.Y), Math.Max(start.X, screen.X), Math.Max(start.Y, screen.Y));
        controller.Show(new(Marquee: _bounds));
    }

    public void Complete()
    {
        var a = _baseline.Viewport.ScreenToImage(new(_bounds.Left, _bounds.Top));
        var b = _baseline.Viewport.ScreenToImage(new(_bounds.Right, _bounds.Bottom));
        controller.SelectRegion(_baseline, new(a.X, a.Y, b.X, b.Y), additive);
    }

    public void Cancel()
    {
    }
}
