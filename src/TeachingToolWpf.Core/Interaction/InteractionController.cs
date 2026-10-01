using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Interaction;

public enum EditorTool { Select, Pan, Draw }
[Flags] public enum InputModifiers { None = 0, Control = 1, Shift = 2, Alt = 4 }
public sealed record InteractionOverlay(Guid? Hover = null, RotatedRect? Draft = null, BoundsD? Marquee = null);

/// <summary>Chooses an input state; each gesture owns its preview and commit/cancel behavior.</summary>
public sealed class InteractionController(EditorSession editor)
{
    private IGesture? gesture;
    private EditorTool tool;
    public EditorTool Tool { get => tool; set { Cancel(); tool = value; Changed?.Invoke(); } }
    public InteractionOverlay Overlay { get; private set; } = new();
    public bool IsDragging => gesture is not null;
    public event Action? Changed;
    internal EditorSession Editor => editor;
    internal void Show(InteractionOverlay overlay) { Overlay = overlay; Changed?.Invoke(); }

    public void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
    {
        Cancel();
        if (middle || Tool == EditorTool.Pan) gesture = new PanGesture(editor, screen);
        else if (Tool == EditorTool.Draw) gesture = new DrawGesture(this, screen);
        else gesture = StartSelection(screen, modifiers);
        Changed?.Invoke();
    }
    private IGesture StartSelection(PointD screen, InputModifiers modifiers)
    {
        var state = editor.State;
        if (state.Selection.Count == 1)
        {
            var selected = state.Rois[state.Selection.Single()];
            if ((HandleGeometry.RotationHandle(selected.Geometry, state.Viewport) - screen).Length < 10)
                return new RotateGesture(editor, screen, selected.Geometry.Center);
            var handles = HandleGeometry.ResizeHandles(selected.Geometry, state.Viewport);
            for (var i = 0; i < handles.Count; i++) if ((handles[i] - screen).Length < 9) return new ResizeGesture(editor, selected, i);
        }
        else if (state.Selection.Count > 1)
        {
            var bounds = SelectionBounds(state);
            if ((state.Viewport.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28) - screen).Length < 10)
                return new RotateGesture(editor, screen, bounds.Center);
        }
        var hit = Hit(screen, modifiers.HasFlag(InputModifiers.Alt));
        if (hit is { } id)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.Select(state.Selection.Contains(id) ? state.Selection.Remove(id) : state.Selection.Add(id));
                return new NoGesture();
            }
            if (!state.Selection.Contains(id)) editor.Select(modifiers.HasFlag(InputModifiers.Shift) ? state.Selection.Add(id) : [id]);
            return new MoveGesture(editor, screen);
        }
        return new MarqueeGesture(this, screen, modifiers != InputModifiers.None);
    }
    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        if (gesture is { } active) active.Move(screen, modifiers);
        else { var hit = Hit(screen); if (Overlay.Hover != hit) Show(Overlay with { Hover = hit }); }
    }
    public void Up(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        var active = gesture; gesture = null;
        try { active?.Move(screen, modifiers); active?.Complete(); }
        catch { active?.Cancel(); throw; }
        finally { Show(new()); }
    }
    public void Cancel() { var active = gesture; gesture = null; active?.Cancel(); Show(new()); }
    public void Wheel(PointD screen, double notches)
    { if (gesture is null) editor.SetViewport(editor.State.Viewport.ZoomAt(screen, Math.Pow(1.2, notches))); }
    private Guid? Hit(PointD screen, bool cycle = false)
    {
        var point = editor.State.Viewport.ScreenToImage(screen);
        var hits = editor.State.Rois.Values.Where(r => r.Geometry.Contains(point, 3 / editor.State.Viewport.Scale))
            .OrderBy(r => r.Geometry.Width * r.Geometry.Height).ThenByDescending(r => r.Order).Select(r => r.Id).ToArray();
        if (hits.Length == 0) return null;
        if (cycle && editor.State.Selection.Count == 1)
        {
            var index = Array.IndexOf(hits, editor.State.Selection.Single());
            return hits[(index + 1) % hits.Length];
        }
        return hits[0];
    }
    internal static BoundsD SelectionBounds(DocumentState state)
        => BoundsD.FromPoints(state.Selection.SelectMany(id => state.Rois[id].Geometry.Corners));
}
