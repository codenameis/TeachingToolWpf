using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>EditorSession을 직접 동기 호출하는 기본 명령 싱크다.</summary>
public sealed class EditorSessionCommandSink(EditorSession editor) : IEditorCommandSink
{
    private readonly EditorSession _editor = editor ?? throw new ArgumentNullException(nameof(editor));

    public void BeginPreview() => _editor.BeginPreview();

    public void PreviewTranslation(PointD delta, bool allowSlide)
        => _editor.PreviewTranslation(delta, allowSlide);

    public void PreviewRotation(PointD pivot, double degrees)
        => _editor.PreviewRotation(pivot, degrees);

    public void PreviewResize(Guid id, RoiGeometry geometry)
        => _editor.PreviewResize(id, geometry);

    public void CommitPreview(string name)
        => _editor.CommitPreview(name);

    public void CancelPreview()
        => _editor.CancelPreview();

    public void Select(IEnumerable<Guid> ids)
        => _editor.Select(ids);

    public void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var ids = EditorSession.FindContainedOrdinaryIds(baseline, bounds);
        Select(additive ? baseline.Selection.Union(ids) : ids);
    }

    public void Add(RoiGeometry geometry)
        => _editor.Add(geometry);

    public void AddParent(RoiGeometry geometry)
        => _editor.AddParent(geometry);

    public void AddChild(Guid parentId, RoiGeometry geometry)
        => _editor.AddChild(parentId, geometry);

    public void AddMask(Guid id, MaskRegion mask)
        => _editor.AddMask(id, mask);

    public void TranslateSelection(PointD delta)
        => _editor.TranslateSelection(delta);

    public void SetViewport(ViewportTransform viewport)
        => _editor.SetViewport(viewport);
}
