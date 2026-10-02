using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Interaction;

internal interface IGesture
{
    void Move(PointD screen, InputModifiers modifiers);
    void Complete();
    void Cancel();
}
internal sealed class NoGesture : IGesture
{
    public void Move(PointD screen, InputModifiers modifiers) { }
    public void Complete() { }
    public void Cancel() { }
}
internal sealed class PanGesture(EditorSession editor, PointD start) : IGesture
{
    private readonly ViewportTransform view = editor.State.Viewport;
    public void Move(PointD screen, InputModifiers modifiers) => editor.SetViewport(view.Pan(screen - start));
    public void Complete() { }
    public void Cancel() => editor.SetViewport(view);
}
internal abstract class EditGesture : IGesture
{
    protected EditorSession Editor { get; }
    protected ViewportTransform View { get; }
    protected EditGesture(EditorSession editor) { Editor = editor; View = editor.State.Viewport; editor.BeginPreview(); }
    public abstract void Move(PointD screen, InputModifiers modifiers);
    protected abstract string Name { get; }
    public void Complete() => Editor.CommitPreview(Name);
    public void Cancel() => Editor.CancelPreview();
}
internal sealed class MoveGesture(EditorSession editor, PointD start) : EditGesture(editor)
{
    protected override string Name => "ROI 이동";
    public override void Move(PointD screen, InputModifiers modifiers)
    {
        var delta = (screen - start) / View.Scale;
        if ((screen - start).Length < 2) delta = new();
        if (modifiers.HasFlag(InputModifiers.Shift)) delta = Math.Abs(delta.X) > Math.Abs(delta.Y) ? new(delta.X, 0) : new(0, delta.Y);
        Editor.PreviewTransform(roi => roi.Geometry.Translate(delta));
    }
}
internal sealed class ResizeGesture(EditorSession editor, Roi original, int handle) : EditGesture(editor)
{
    protected override string Name => "ROI 크기 조절";
    public override void Move(PointD screen, InputModifiers modifiers)
        => Editor.PreviewResize(original.Id, HandleGeometry.Resize(original.Geometry, handle, View.ScreenToImage(screen)));
}
internal sealed class RotateGesture(EditorSession editor, PointD start, PointD pivot) : EditGesture(editor)
{
    protected override string Name => "ROI 회전";
    public override void Move(PointD screen, InputModifiers modifiers)
    {
        var a = View.ScreenToImage(start) - pivot; var b = View.ScreenToImage(screen) - pivot;
        var degrees = (Math.Atan2(b.Y, b.X) - Math.Atan2(a.Y, a.X)) * 180 / Math.PI;
        if (modifiers.HasFlag(InputModifiers.Shift)) degrees = Math.Round(degrees / 15) * 15;
        Editor.PreviewTransform(roi => roi.Geometry.RotateAround(pivot, degrees));
    }
}
internal sealed class DrawGesture(InteractionController controller, PointD start) : IGesture
{
    private readonly ViewportTransform view = controller.Editor.State.Viewport;
    private RotatedRect? draft;
    public void Move(PointD screen, InputModifiers modifiers)
    {
        var image = controller.Editor.State.Image;
        var a = RoiImageBounds.Clamp(view.ScreenToImage(start), image);
        var b = RoiImageBounds.Clamp(view.ScreenToImage(screen), image);
        draft = (screen - start).Length < 4 || Math.Abs(a.X - b.X) < .01 || Math.Abs(a.Y - b.Y) < .01 ? null
            : new((a + b) / 2, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y), 0);
        controller.Show(new(Draft: draft));
    }
    public void Complete()
    {
        if (draft is null) return;
        controller.Editor.Add(draft);
        controller.Tool = EditorTool.Select;
    }
    public void Cancel() { }
}
internal sealed class MarqueeGesture(InteractionController controller, PointD start, bool additive) : IGesture
{
    private readonly DocumentState baseline = controller.Editor.State;
    private BoundsD bounds = new(start.X, start.Y, start.X, start.Y);
    public void Move(PointD screen, InputModifiers modifiers)
    {
        bounds = new(Math.Min(start.X, screen.X), Math.Min(start.Y, screen.Y), Math.Max(start.X, screen.X), Math.Max(start.Y, screen.Y));
        controller.Show(new(Marquee: bounds));
    }
    public void Complete()
    {
        var a = baseline.Viewport.ScreenToImage(new(bounds.Left, bounds.Top));
        var b = baseline.Viewport.ScreenToImage(new(bounds.Right, bounds.Bottom));
        var ids = baseline.Snapshot.Spatial.Intersecting(new(a.X, a.Y, b.X, b.Y))
            .Where(r => r.Geometry.Corners.All(p => bounds.Contains(baseline.Viewport.ImageToScreen(p)))).Select(r => r.Id);
        controller.Editor.Select(additive ? baseline.Selection.Union(ids) : ids);
    }
    public void Cancel() { }
}
