using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>입력 제스처가 생성한 미리보기·선택·확정 명령을 편집 세션이나 비동기 준비 파이프라인으로 전달한다.</summary>
public interface IEditorCommandSink
{
    void PreviewTranslation(PointD delta, bool allowSlide);

    void PreviewRotation(PointD pivot, double degrees);

    void CommitPreview(string name);

    void Select(IEnumerable<Guid> ids);

    void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive);
}

/// <summary>EditorSession을 직접 동기 호출하는 기본 명령 싱크다.</summary>
public sealed class EditorSessionCommandSink(EditorSession editor) : IEditorCommandSink
{
    public void PreviewTranslation(PointD delta, bool allowSlide)
        => editor.PreviewTranslation(delta, allowSlide);

    public void PreviewRotation(PointD pivot, double degrees)
        => editor.PreviewRotation(pivot, degrees);

    public void CommitPreview(string name)
        => editor.CommitPreview(name);

    public void Select(IEnumerable<Guid> ids)
        => editor.Select(ids);

    public void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
    {
        var ids = EditorSession.FindContainedOrdinaryIds(baseline, bounds);
        Select(additive ? baseline.Selection.Union(ids) : ids);
    }
}

/// <summary>특정 명령만 비동기 호스트나 콜백으로 위임하고 나머지는 기본 싱크로 전달한다.</summary>
public sealed class DelegatingEditorCommandSink(
    EditorSession editor,
    Action<string>? commitPreview = null,
    Action<IEnumerable<Guid>>? selectRois = null,
    Action<PointD, bool>? previewTranslation = null,
    Action<PointD, double>? previewRotation = null,
    Action<BoundsD, bool>? selectRegion = null) : IEditorCommandSink
{
    public void PreviewTranslation(PointD delta, bool allowSlide)
    {
        if (previewTranslation is null)
        {
            editor.PreviewTranslation(delta, allowSlide);
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
            editor.PreviewRotation(pivot, degrees);
        }
        else
        {
            previewRotation(pivot, degrees);
        }
    }

    public void CommitPreview(string name)
    {
        if (commitPreview is null)
        {
            editor.CommitPreview(name);
        }
        else
        {
            commitPreview(name);
        }
    }

    public void Select(IEnumerable<Guid> ids)
    {
        if (selectRois is null)
        {
            editor.Select(ids);
        }
        else
        {
            selectRois(ids);
        }
    }

    public void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
    {
        if (selectRegion is not null)
        {
            selectRegion(bounds, additive);
            return;
        }

        var ids = EditorSession.FindContainedOrdinaryIds(baseline, bounds);
        Select(additive ? baseline.Selection.Union(ids) : ids);
    }
}
