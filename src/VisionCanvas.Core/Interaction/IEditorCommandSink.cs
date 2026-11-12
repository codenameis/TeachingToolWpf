using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>입력 제스처와 컨트롤러가 생성한 미리보기·선택·도형·뷰포트 명령을 편집 세션이나 비동기 준비 파이프라인으로 전달한다.</summary>
public interface IEditorCommandSink
{
    void BeginPreview();

    void PreviewTranslation(PointD delta, bool allowSlide);

    void PreviewRotation(PointD pivot, double degrees);

    void PreviewResize(Guid id, RoiGeometry geometry);

    void CommitPreview(string name);

    void CancelPreview();

    void Select(IEnumerable<Guid> ids);

    void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive);

    void Add(RoiGeometry geometry);

    void AddParent(RoiGeometry geometry);

    void AddChild(Guid parentId, RoiGeometry geometry);

    void AddMask(Guid id, MaskRegion mask);

    void TranslateSelection(PointD delta);

    void SetViewport(ViewportTransform viewport);
}
