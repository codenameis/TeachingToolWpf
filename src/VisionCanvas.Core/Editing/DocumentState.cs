using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

public sealed record Roi(Guid Id, string Name, RoiGeometry Geometry, Guid? ParentId, bool IsGroup, long Order)
{
    public ImmutableArray<MaskRegion> Masks { get; init; } = [];

    public RoiSearchRange? SearchRange
    {
        get; init;
    }

    public bool IsNameVisible { get; init; } = true;

    public FilterSettings Filter
    {
        get;
        init;
    } = new(RoiFilterKind.None);

    public ImmutableArray<FilterSettings> FilterPipeline { get; init; } = [];

    public ImmutableArray<FilterSettings> EffectiveFilterPipeline =>
        !FilterPipeline.IsDefaultOrEmpty
            ? FilterPipeline
            : (Filter.Kind != RoiFilterKind.None && Filter.IsEnabled ? [Filter] : []);

    public bool HasActiveFilter =>
        !FilterPipeline.IsDefaultOrEmpty
            ? FilterPipeline.Any(static s => s.IsEnabled && s.Kind != RoiFilterKind.None)
            : (Filter.IsEnabled && Filter.Kind != RoiFilterKind.None);
}

/// <summary>Immutable image-space geometry prevents precision drift during reparenting.</summary>
public sealed record DocumentState
{
    public RoiSnapshot Snapshot { get; }

    public RoiSelectionSnapshot SelectionSnapshot { get; }

    public ImageReference? Image { get; }

    public ViewportTransform Viewport { get; init; }

    public ImmutableDictionary<Guid, Roi> Rois => Snapshot.Rois;

    public RoiIdSet Selection => SelectionSnapshot.Selection;

    public int RoiCount => Rois.Count;

    public int SelectionCount => Selection.Count;

    public DocumentState(
        ImmutableDictionary<Guid, Roi> rois,
        RoiIdSet selection,
        ImageReference? image,
        ViewportTransform viewport)
        : this(new RoiSnapshot(rois), selection, image, viewport)
    {
    }

    public DocumentState(
        RoiSnapshot snapshot,
        RoiIdSet selection,
        ImageReference? image,
        ViewportTransform viewport)
        : this(snapshot, new RoiSelectionSnapshot(snapshot, selection, image), image, viewport)
    {
    }

    public DocumentState(
        RoiSnapshot snapshot,
        RoiSelectionSnapshot selectionSnapshot,
        ImageReference? image,
        ViewportTransform viewport)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectionSnapshot);
        Snapshot = snapshot;
        SelectionSnapshot = selectionSnapshot;
        Image = image;
        Viewport = viewport;
    }

    private DocumentState(DocumentState original)
    {
        Snapshot = original.Snapshot;
        SelectionSnapshot = original.SelectionSnapshot;
        Image = original.Image;
        Viewport = original.Viewport;
    }

    public bool IsGroupRoi(Guid id) => Rois[id].IsGroup || Snapshot.Children.ContainsKey(id);

    public bool IsTransformSelectionPrepared => SelectionSnapshot.IsTransformSelectionPrepared;

    public RoiTransformSelection TransformSelection => SelectionSnapshot.GetTransformSelection(this);

    public RoiSelectionFacts SelectionFacts => SelectionSnapshot.SelectionFacts;

    public RoiSpatialIndex SelectedSpatial => SelectionSnapshot.SelectedSpatial;

    public bool SameContent(DocumentState other) => ReferenceEquals(Rois, other.Rois);

    public bool SameSelection(DocumentState other) => ReferenceEquals(Selection, other.Selection);

    public DocumentState WithViewport(ViewportTransform viewport)
        => Viewport == viewport ? this : new DocumentState(Snapshot, SelectionSnapshot, Image, viewport);

    public DocumentState WithSelection(RoiIdSet selection)
        => ReferenceEquals(Selection, selection) ? this : new DocumentState(Snapshot, selection, Image, Viewport);

    public DocumentState WithImage(ImageReference? image, ViewportTransform? viewport = null)
    {
        var nextViewport = viewport ?? Viewport;
        if (Image == image && Viewport == nextViewport)
        {
            return this;
        }

        return new DocumentState(Snapshot, SelectionSnapshot.WithImage(image), image, nextViewport);
    }

    /// <summary>changedIds must include every added, removed, geometric or metadata change.</summary>
    public DocumentState WithRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
    {
        if (ReferenceEquals(Rois, rois))
        {
            return this;
        }

        var nextSnapshot = new RoiSnapshot(Snapshot, rois, changedIds.Distinct().ToImmutableArray());
        return new DocumentState(nextSnapshot, Selection, Image, Viewport);
    }

    // Whole-document edits and large coherent groups preserve BVH membership; refit bounds without sorting.
    internal DocumentState WithRigidRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds, CancellationToken token = default)
    {
        if (ReferenceEquals(Rois, rois))
        {
            return this;
        }

        var nextSnapshot = new RoiSnapshot(Snapshot, rois, changedIds.ToImmutableArray(), refitSpatial: true, token: token);
        return new DocumentState(nextSnapshot, Selection, Image, Viewport);
    }

    internal DocumentState WithRoisFrom(DocumentState before, ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
    {
        var nextSnapshot = new RoiSnapshot(before.Snapshot, rois, changedIds.Distinct().ToImmutableArray(), Snapshot);
        return new DocumentState(nextSnapshot, Selection, Image, Viewport);
    }

    /// <summary>입력·속성·렌더러가 즉시 참조하는 지연 공간 인덱스와 선택 통계를 백그라운드에서 미리 준비한다.</summary>
    public void Warm(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = Snapshot.Spatial;
        token.ThrowIfCancellationRequested();
        _ = Snapshot.Children;
        token.ThrowIfCancellationRequested();
        _ = Snapshot.SearchSpatial;
        token.ThrowIfCancellationRequested();
        _ = SelectionFacts.HasMultipleRoots;
        _ = SelectionFacts.CanGroup;
        _ = Snapshot.GroupLevels;
        token.ThrowIfCancellationRequested();
        _ = SelectedSpatial;
        if (SelectionCount != 0)
        {
            TransformSelection.Warm(token);
        }

        token.ThrowIfCancellationRequested();
    }

    public bool Equals(DocumentState? other)
        => other is not null
        && ReferenceEquals(Rois, other.Rois)
        && ReferenceEquals(Selection, other.Selection)
        && Image == other.Image
        && Viewport == other.Viewport;

    public override int GetHashCode() => HashCode.Combine(Rois, Selection, Image, Viewport);

    public static DocumentState Empty => new(
        ImmutableDictionary<Guid, Roi>.Empty,
        ImmutableHashSet<Guid>.Empty,
        null,
        ViewportTransform.Identity);
}
