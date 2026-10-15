using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>
    /// 저장된 일반 ROI 문서를 검증하고 준비한다. 아직 현재 문서나 이력은 바꾸지 않는다.
    /// 첫 로딩은 시작 상태가 되고, 이후 로딩은 Undo/Redo 한 건이 된다. 반복 격자는 이 API의 입력이 아니다.
    /// </summary>
    public Task<PreparedEditorChange> PrepareDocumentAsync(ImageReference image, IEnumerable<Roi> rois,
        ViewportTransform? viewport = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(rois);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(image.Key) || image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("모델 이미지에는 식별자와 양수 크기가 필요합니다.", nameof(image));
        }
        if (_previewStart is not null)
        {
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        }
        // 호출 후 원본 목록이 바뀌어도 준비 중인 문서가 달라지지 않도록 목록만 복사한다.
        var entries = rois.ToImmutableArray();
        var before = State;
        var revision = _mutationRevision;
        var epoch = _previewEpoch;
        var order = _nextOrder;
        return EditPreparation.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var dictionary = entries.ToImmutableDictionary(roi => roi.Id);
            var orders = new HashSet<long>();
            foreach (var roi in entries)
            {
                token.ThrowIfCancellationRequested();
                if (roi.Id == Guid.Empty || string.IsNullOrWhiteSpace(roi.Name) || roi.Name.Length > 256 ||
                    roi.Order <= 0 || roi.Order == long.MaxValue || !orders.Add(roi.Order) || !RoiImageBounds.Fits(roi.Geometry.Bounds, image))
                {
                    throw new ArgumentException("ROI의 ID·이름·순서·이미지 경계가 올바르지 않습니다.", nameof(rois));
                }
                roi.Filter.Validate();
                _ = roi.SearchRange?.Expand(roi.Geometry);
                if (roi.Masks.IsDefault || roi.Masks.Length > MaximumMasksPerRoi || roi.Masks.Any(mask => mask is null))
                {
                    throw new ArgumentException("ROI 마스크 목록이 올바르지 않습니다.", nameof(rois));
                }
                // 최대 깊이로 탐색을 제한하므로 잘못된 순환 연결도 무한 반복하지 않는다.
                var depth = 1;
                for (var parent = roi.ParentId; parent is { } id; parent = dictionary[id].ParentId)
                {
                    if (!dictionary.ContainsKey(id) || ++depth > RoiHierarchy.MaximumDepth)
                    {
                        throw new ArgumentException("부모가 없거나 ROI 계층이 순환/최대 4단계를 초과합니다.", nameof(rois));
                    }
                }
            }
            var view = viewport ?? before.Viewport;
            if (!double.IsFinite(view.Scale) || view.Scale <= 0 || !view.Offset.IsFinite)
            {
                throw new ArgumentException("화면 배율과 위치가 올바르지 않습니다.", nameof(viewport));
            }
            var next = new DocumentState(dictionary, [], image, view);
            if (entries.Any(roi => next.IsGroupRoi(roi.Id) && (roi.SearchRange is not null || !roi.Masks.IsEmpty)))
            {
                throw new ArgumentException("그룹에는 검색 범위나 마스크를 지정할 수 없습니다.", nameof(rois));
            }
            RoiGroupBounds.Validate(next, dictionary.Keys);
            Warm(next, token);
            var edit = before.Image is null && before.RoiCount == 0 ? null : new DocumentEdit("모델 로딩", before, next, replacesImage: true, cancellationToken: token);
            return new PreparedEditorChange(this, revision, epoch, before, next, edit,
                (order, Math.Max(order, entries.IsEmpty ? 0 : entries.Max(roi => roi.Order))), restoresViewport: true);
        }, token);
    }
}
