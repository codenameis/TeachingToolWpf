using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public sealed record Roi(Guid Id, string Name, RotatedRect Geometry, Guid? ParentId, bool IsGroup, long Order)
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
    public ImmutableHashSet<Guid> Selection { get => selection; init { selection = value; selectedSpatial = null; } }
    public ImmutableDictionary<Guid, Roi> Rois { get => snapshot.Rois; init { snapshot = new(value); selectedSpatial = null; } }
    public RoiSnapshot Snapshot => snapshot;
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
    public DocumentState WithRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => ReferenceEquals(Rois, rois) ? this : this with { snapshot = new(snapshot, rois, changedIds.Distinct().ToImmutableArray()), selectedSpatial = null };
    internal DocumentState WithRoisFrom(DocumentState before, ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => this with { snapshot = new(before.snapshot, rois, changedIds.Distinct().ToImmutableArray(), snapshot), selectedSpatial = null };
    public static DocumentState Empty => new(ImmutableDictionary<Guid, Roi>.Empty,
        ImmutableHashSet<Guid>.Empty, null, ViewportTransform.Identity);
}
