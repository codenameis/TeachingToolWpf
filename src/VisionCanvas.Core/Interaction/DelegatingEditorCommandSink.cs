using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>특정 명령만 비동기 호스트나 콜백으로 위임하고 나머지는 기본 싱크로 전달한다.</summary>
public sealed class DelegatingEditorCommandSink(
    EditorSession editor,
    Action<string>? commitPreview = null,
    Action<IEnumerable<Guid>>? selectRois = null,
    Action<PointD, bool>? previewTranslation = null,
    Action<PointD, double>? previewRotation = null,
    Action<BoundsD, bool>? selectRegion = null) : IEditorCommandSink
{
    private readonly EditorSession _editor = editor ?? throw new ArgumentNullException(nameof(editor));

    public void BeginPreview() => _editor.BeginPreview();

    public void PreviewTranslation(PointD delta, bool allowSlide)
    {
        if (previewTranslation is null)
        {
            _editor.PreviewTranslation(delta, allowSlide);
        }
        else
        {
            previewTranslation(delta, allowSlide);
        }
    }

    public void PreviewRotation(PointD pivot, double degrees)
    {
        if (previewRotation is null)
        {
            _editor.PreviewRotation(pivot, degrees);
        }
        else
        {
            previewRotation(pivot, degrees);
        }
    }

    public void PreviewResize(Guid id, RoiGeometry geometry)
        => _editor.PreviewResize(id, geometry);

    public void CommitPreview(string name)
    {
        if (commitPreview is null)
        {
            _editor.CommitPreview(name);
        }
        else
        {
            commitPreview(name);
        }
    }

    public void CancelPreview()
        => _editor.CancelPreview();

    public void Select(IEnumerable<Guid> ids)
    {
        if (selectRois is null)
        {
            _editor.Select(ids);
        }
        else
        {
            selectRois(ids);
        }
    }

    public void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (selectRegion is not null)
        {
            selectRegion(bounds, additive);
            return;
        }

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
