using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>
    /// 저장된 일반 ROI 문서를 검증하고 준비한다. 아직 현재 문서나 이력은 바꾸지 않는다.
    /// 첫 로딩은 시작 상태가 되고, 이후 로딩은 Undo/Redo 한 건이 된다. 반복 격자는 이 API의 입력이 아니다.
    /// </summary>
    public Task<PreparedEditorChange> PrepareDocumentAsync(
        ImageReference image,
        IEnumerable<Roi> rois,
        ViewportTransform? viewport = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(rois);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(image.Key) || image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("모델 이미지에는 식별자와 양수 크기가 필요합니다.", nameof(image));
        }

        ThrowIfPreviewing();
        // 호출 후 원본 목록이 바뀌어도 준비 중인 문서가 달라지지 않도록 목록만 복사한다.
        var entries = rois.ToImmutableArray();
        var isInitialDocument = State.Image is null && State.RoiCount == 0;
        return PrepareAsync(new EditorOperations.LoadDocumentOperation(image, entries, viewport, _nextOrder, isInitialDocument), token);
    }
}
