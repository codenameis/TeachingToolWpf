using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Interaction;

internal sealed class MinimapGesture : IGesture
{
    private readonly EditorSession editor;
    private readonly MinimapLayout map;
    private readonly ViewportTransform before;
    private readonly PointD start, grabOffset;
    private PointD last;
    public MinimapGesture(EditorSession editor, MinimapLayout map, PointD start)
    {
        this.editor = editor; this.map = map; this.start = last = start; before = editor.State.Viewport;
        grabOffset = map.VisibleBounds(before).Contains(start)
            ? before.ScreenToImage(new(map.ViewWidth / 2, map.ViewHeight / 2)) - map.MapToImage(start) : new();
        Move(start, InputModifiers.None);
    }
    public void Move(PointD screen, InputModifiers modifiers)
    {
        last = screen;
        editor.SetViewport(map.CenterView(before, map.MapToImage(screen) + grabOffset));
    }
    public void Complete()
    {
        if ((last - start).Length < 2) editor.SetViewport(map.CenterView(before, map.MapToImage(last)));
    }
    public void Cancel() => editor.SetViewport(before);
}
