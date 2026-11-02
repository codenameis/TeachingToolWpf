using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>하나의 ROI가 편집 전후에 어떻게 달라졌는지 보관한다. null은 해당 ROI가 없음을 뜻한다.</summary>
public sealed record RoiChange(Guid Id, Roi? Before, Roi? After);

/// <summary>한 번의 편집 차이와 선택·이미지 정보를 보관하고, 정방향 또는 역방향으로 적용한다.</summary>
public sealed class DocumentEdit
{
    public string Name { get; }

    public ImmutableArray<RoiChange> Changes { get; }

    private readonly RoiIdSet _beforeSelection;
    private readonly RoiIdSet _afterSelection;
    private readonly ImageReference? _beforeImage;
    private readonly ImageReference? _afterImage;
    private readonly ViewportTransform _beforeView;
    private readonly ViewportTransform _afterView;
    private readonly bool _changesImage;
    private readonly bool _refitSpatial;

    internal bool ChangesImage => _changesImage;

    public bool IsEmpty => Changes.IsEmpty && !_changesImage;

    public long EstimatedBytes { get; }

    public DocumentEdit(string name, DocumentState before, DocumentState after, bool replacesImage = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Name = name;
        _refitSpatial = after.Snapshot.IsRigidUpdate && ReferenceEquals(after.Snapshot.PreviousRois, before.Rois);
        var candidates = ResolveCandidateIds(before, after);
        var initialCapacity = candidates.TryGetNonEnumeratedCount(out var count) ? count : 0;
        var changes = ImmutableArray.CreateBuilder<RoiChange>(initialCapacity);
        var estimatedBytes = 256L;
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = before.Rois.GetValueOrDefault(id);
            var next = after.Rois.GetValueOrDefault(id);
            if (old == next)
            {
                continue;
            }

            changes.Add(new(id, old, next));
            estimatedBytes += EstimateChangeBytes(old, next);
        }

        Changes = changes.Count == changes.Capacity ? changes.MoveToImmutable() : changes.ToImmutable();
        _beforeSelection = before.Selection;
        _afterSelection = after.Selection;
        _beforeImage = before.Image;
        _afterImage = after.Image;
        _beforeView = before.Viewport;
        _afterView = after.Viewport;
        _changesImage = replacesImage || before.Image != after.Image;
        EstimatedBytes = estimatedBytes + ((_beforeSelection.Count + _afterSelection.Count) * 32L);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IEnumerable<Guid> ResolveCandidateIds(DocumentState before, DocumentState after)
    {
        if (ReferenceEquals(before.Rois, after.Rois))
        {
            return [];
        }

        if (ReferenceEquals(before.Rois, after.Snapshot.PreviousRois))
        {
            return after.Snapshot.ChangedIds;
        }

        return before.Rois.Keys.Union(after.Rois.Keys);
    }

    private static long EstimateChangeBytes(Roi? old, Roi? next)
    {
        var nameChars = (old?.Name.Length ?? 0) + (next?.Name.Length ?? 0);
        var searchBytes = (old?.SearchRange is null ? 0 : 32) + (next?.SearchRange is null ? 0 : 32);
        var maskCount = (old?.Masks.Length ?? 0) + (next?.Masks.Length ?? 0);
        var oldPolygonVertices = old?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? old.Geometry.Shape.Vertices.Length : 0;
        var nextPolygonVertices = next?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? next.Geometry.Shape.Vertices.Length : 0;

        return 352L
            + (nameChars * 2L)
            + searchBytes
            + (maskCount * 64L)
            + ((oldPolygonVertices + nextPolygonVertices) * 16L);
    }

    public DocumentState Apply(DocumentState state, bool forward, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rois = state.Rois.ToBuilder();
        foreach (var change in Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = forward ? change.After : change.Before;
            if (value is null)
            {
                rois.Remove(change.Id);
            }
            else
            {
                rois[change.Id] = value;
            }
        }

        var ids = Changes.Select(c => c.Id);
        var updatedRois = rois.ToImmutable();
        var geometry = _refitSpatial
            ? state.WithRigidRois(updatedRois, ids, cancellationToken)
            : state.WithRois(updatedRois, ids);

        var targetSelection = forward ? _afterSelection : _beforeSelection;
        var result = geometry.WithSelection(targetSelection);
        if (_changesImage)
        {
            var targetImage = forward ? _afterImage : _beforeImage;
            var targetView = forward ? _afterView : _beforeView;
            result = result.WithImage(targetImage, targetView);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal DocumentState RestoreCachedMetadata(DocumentState target, DocumentState source, bool forward)
    {
        // 반대 방향으로 재사용하는 상태에는 편집 뒤 바뀐 선택이 들어 있을 수 있다.
        // ROI 색인은 재사용하되 선택·이미지·뷰는 Apply와 같은 규칙으로 복원한다.
        var selection = forward ? _afterSelection : _beforeSelection;
        if (!ReferenceEquals(target.Selection, selection))
        {
            target = target.WithSelection(selection);
        }

        var image = source.Image;
        var view = source.Viewport;
        if (_changesImage)
        {
            image = forward ? _afterImage : _beforeImage;
            view = forward ? _afterView : _beforeView;
        }

        return target.WithImage(image, view);
    }

    internal bool MatchesRecordedSource(DocumentState source, bool forward, CancellationToken cancellationToken)
    {
        foreach (var change in Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = forward ? change.Before : change.After;
            if (source.Rois.GetValueOrDefault(change.Id) != expected)
            {
                return false;
            }
        }

        return true;
    }
}
