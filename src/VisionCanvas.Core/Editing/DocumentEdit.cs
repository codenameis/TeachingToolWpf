using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>하나의 ROI가 편집 전후에 어떻게 달라졌는지 보관한다. null은 해당 ROI가 없음을 뜻한다.</summary>
public sealed record RoiChange(Guid Id, Roi? Before, Roi? After);

/// <summary>한 번의 편집 차이와 선택·이미지 정보를 보관하고, 정방향 또는 역방향으로 적용한다.</summary>
public sealed class DocumentEdit
{
    public string Name
    {
        get;
    }

    public ImmutableArray<RoiChange> Changes
    {
        get;
    }

    private readonly RoiIdSet _beforeSelection;
    private readonly RoiIdSet _afterSelection;
    private readonly ImageReference? _beforeImage;
    private readonly ImageReference? _afterImage;
    private readonly ViewportTransform _beforeView;
    private readonly ViewportTransform _afterView;
    private readonly bool _changesImage;
    private readonly bool _refitSpatial;
    private readonly RepeatedRoiEdit _repeated;

    internal bool ChangesImage => _changesImage;

    public bool IsEmpty => Changes.IsEmpty && _repeated.IsEmpty && !_changesImage;

    public long EstimatedBytes
    {
        get;
    }

    public DocumentEdit(string name, DocumentState before, DocumentState after, bool replacesImage = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Name = name;
        _repeated = new(before, after);
        _refitSpatial = after.Snapshot.IsRigidUpdate && ReferenceEquals(after.Snapshot.PreviousRois, before.Rois);
        IEnumerable<Guid> candidates = ReferenceEquals(before.Rois, after.Rois) ? [] :
            ReferenceEquals(before.Rois, after.Snapshot.PreviousRois) ? after.Snapshot.ChangedIds : before.Rois.Keys.Union(after.Rois.Keys);
        var changes = ImmutableArray.CreateBuilder<RoiChange>(candidates.TryGetNonEnumeratedCount(out var count) ? count : 0);
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
            estimatedBytes += 320L + (((old?.Name.Length ?? 0) + (next?.Name.Length ?? 0)) * 2);
            estimatedBytes += 32L + (old?.SearchRange is null ? 0 : 32) + (next?.SearchRange is null ? 0 : 32);
            estimatedBytes += ((old?.Masks.Length ?? 0) + (next?.Masks.Length ?? 0)) * 64L;
            estimatedBytes += ((old?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? old.Geometry.Shape.Vertices.Length : 0)
                + (next?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? next.Geometry.Shape.Vertices.Length : 0)) * 16L;
        }
        Changes = changes.Count == changes.Capacity ? changes.MoveToImmutable() : changes.ToImmutable();
        _beforeSelection = before.Selection;
        _afterSelection = after.Selection;
        _beforeImage = before.Image;
        _afterImage = after.Image;
        _beforeView = before.Viewport;
        _afterView = after.Viewport;
        _changesImage = replacesImage || before.Image != after.Image;
        EstimatedBytes = estimatedBytes + ((_beforeSelection.Count + _afterSelection.Count) * 32L) + _repeated.EstimatedBytes;
        cancellationToken.ThrowIfCancellationRequested();
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
        var geometry = _refitSpatial ? state.WithRigidRois(rois.ToImmutable(), ids, cancellationToken) : state.WithRois(rois.ToImmutable(), ids);
        var result = geometry with
        {
            Selection = forward ? _afterSelection : _beforeSelection,
            Image = _changesImage ? (forward ? _afterImage : _beforeImage) : state.Image,
            Viewport = _changesImage ? (forward ? _afterView : _beforeView) : state.Viewport
        };
        cancellationToken.ThrowIfCancellationRequested();
        return _repeated.Apply(result, forward);
    }

    internal DocumentState RestoreCachedMetadata(DocumentState target, DocumentState source, bool forward)
    {
        target = _repeated.RestoreSelection(target, forward);
        // 반대 방향으로 재사용하는 상태에는 편집 뒤 바뀐 선택이 들어 있을 수 있다.
        // ROI 색인은 재사용하되 선택·이미지·뷰는 Apply와 같은 규칙으로 복원한다.
        var selection = forward ? _afterSelection : _beforeSelection;
        if (!ReferenceEquals(target.Selection, selection))
        {
            target = target with
            {
                Selection = selection
            };
        }
        var image = _changesImage ? (forward ? _afterImage : _beforeImage) : source.Image;
        if (target.Image != image)
        {
            target = target with
            {
                Image = image
            };
        }
        var view = _changesImage ? (forward ? _afterView : _beforeView) : source.Viewport;
        return target.Viewport == view ? target : target with
        {
            Viewport = view
        };
    }

    internal bool MatchesRecordedSource(DocumentState source, bool forward, CancellationToken cancellationToken)
    {
        if (!_repeated.Matches(source, forward))
        {
            return false;
        }

        foreach (var change in Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Rois.GetValueOrDefault(change.Id) != (forward ? change.Before : change.After))
            {
                return false;
            }
        }
        return true;
    }
}
