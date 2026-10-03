using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public sealed record Roi(Guid Id, string Name, RoiGeometry Geometry, Guid? ParentId, bool IsGroup, long Order)
{
    public FilterSettings Filter { get; init; } = new(RoiFilterKind.None);
}
public sealed record ImageReference(string Key, string Name, int Width, int Height, string Version = "");

/// <summary>Immutable image-space geometry prevents precision drift during reparenting.</summary>
public sealed record DocumentState(
    ImmutableDictionary<Guid, Roi> Rois,
    ImmutableHashSet<Guid> Selection,
    ImageReference? Image,
    ViewportTransform Viewport)
{
    private RoiSnapshot snapshot = new(Rois);
    private ImmutableHashSet<Guid> selection = Selection;
    private Lazy<RoiSpatialIndex>? selectedSpatial;
    private Lazy<RoiSelectionFacts>? selectionFacts;
    private Lazy<RoiTransformSelection>? transformSelection;
    private ImageReference? image = Image;
    public ImageReference? Image { get => image; init { image = value; transformSelection = null; } }
    public ImmutableHashSet<Guid> Selection { get => selection; init { selection = value; selectedSpatial = null; selectionFacts = null; transformSelection = null; } }
    public ImmutableDictionary<Guid, Roi> Rois { get => snapshot.Rois; init { snapshot = new(value); selectedSpatial = null; selectionFacts = null; transformSelection = null; } }
    public RoiSnapshot Snapshot => snapshot;
    public bool IsTransformSelectionPrepared
    {
        get
        {
            var value = Volatile.Read(ref transformSelection);
            return value?.IsValueCreated == true && value.Value.IsPrepared;
        }
    }
    public RoiTransformSelection TransformSelection
    {
        get
        {
            var value = Volatile.Read(ref transformSelection);
            if (value is null)
            {
                value = new(() => new(this));
                value = Interlocked.CompareExchange(ref transformSelection, value, null) ?? value;
            }
            return value.Value;
        }
    }
    public RoiSelectionFacts SelectionFacts
    {
        get
        {
            var value = Volatile.Read(ref selectionFacts);
            if (value is null)
            {
                value = new(() => new(Rois, Selection));
                value = Interlocked.CompareExchange(ref selectionFacts, value, null) ?? value;
            }
            return value.Value;
        }
    }
    public RoiSpatialIndex SelectedSpatial
    {
        get
        {
            if (Selection.Count == Rois.Count) return Snapshot.Spatial;
            var value = Volatile.Read(ref selectedSpatial);
            if (value is null)
            {
                value = new(() => new RoiSpatialIndex(Selection.Select(id => Rois[id])));
                value = Interlocked.CompareExchange(ref selectedSpatial, value, null) ?? value;
            }
            return value.Value;
        }
    }
    /// <summary>changedIds must include every added, removed, geometric or metadata change.</summary>
    public DocumentState WithRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => ReferenceEquals(Rois, rois) ? this : this with { snapshot = new(snapshot, rois, changedIds.Distinct().ToImmutableArray()), selectedSpatial = null, selectionFacts = null, transformSelection = null };
    // A whole-document rigid edit preserves BVH membership and spatial neighborhoods; refit without sorting.
    internal DocumentState WithRigidRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => ReferenceEquals(Rois, rois) ? this : this with { snapshot = new(snapshot, rois, changedIds.ToImmutableArray(), refitSpatial: true),
            selectedSpatial = null, selectionFacts = null, transformSelection = null };
    internal DocumentState WithRoisFrom(DocumentState before, ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => this with { snapshot = new(before.snapshot, rois, changedIds.Distinct().ToImmutableArray(), snapshot), selectedSpatial = null, selectionFacts = null, transformSelection = null };
    public bool Equals(DocumentState? other) => other is not null && ReferenceEquals(Rois, other.Rois) &&
        ReferenceEquals(Selection, other.Selection) && Image == other.Image && Viewport == other.Viewport;
    public override int GetHashCode() => HashCode.Combine(Rois, Selection, Image, Viewport);
    public static DocumentState Empty => new(ImmutableDictionary<Guid, Roi>.Empty,
        ImmutableHashSet<Guid>.Empty, null, ViewportTransform.Identity);
}
