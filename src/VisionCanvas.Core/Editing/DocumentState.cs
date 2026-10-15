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
}

/// <summary>Immutable image-space geometry prevents precision drift during reparenting.</summary>
public sealed partial record DocumentState(
    ImmutableDictionary<Guid, Roi> Rois,
    RoiIdSet Selection,
    ImageReference? Image,
    ViewportTransform Viewport)
{
    private RoiSnapshot _snapshot = new(Rois);
    private RoiIdSet _selection = Selection;
    private Lazy<RoiSpatialIndex>? _selectedSpatial;
    private Lazy<RoiSelectionFacts>? _selectionFacts;
    private Lazy<RoiTransformSelection>? _transformSelection;
    private ImageReference? _image = Image;

    public ImageReference? Image
    {
        get => _image;
        init
        {
            _image = value;
            _transformSelection = null;
        }
    }

    public RoiIdSet Selection
    {
        get => _selection;
        init
        {
            _selection = value;
            _selectedSpatial = null;
            _selectionFacts = null;
            _transformSelection = null;
        }
    }

    public ImmutableDictionary<Guid, Roi> Rois
    {
        get => _snapshot.Rois;
        init
        {
            _snapshot = new(value);
            _selectedSpatial = null;
            _selectionFacts = null;
            _transformSelection = null;
        }
    }

    public RoiSnapshot Snapshot => _snapshot;

    public bool IsGroupRoi(Guid id) => Rois[id].IsGroup || Snapshot.Children.ContainsKey(id);

    public bool IsTransformSelectionPrepared
    {
        get
        {
            var value = Volatile.Read(ref _transformSelection);
            return value?.IsValueCreated == true && value.Value.IsPrepared;
        }
    }

    public RoiTransformSelection TransformSelection
    {
        get
        {
            var value = Volatile.Read(ref _transformSelection);
            if (value is null)
            {
                value = new(() => new(this));
                value = Interlocked.CompareExchange(ref _transformSelection, value, null) ?? value;
            }
            return value.Value;
        }
    }

    public RoiSelectionFacts SelectionFacts
    {
        get
        {
            var value = Volatile.Read(ref _selectionFacts);
            if (value is null)
            {
                value = new(() => new(Rois, Selection, Snapshot));
                value = Interlocked.CompareExchange(ref _selectionFacts, value, null) ?? value;
            }
            return value.Value;
        }
    }

    public RoiSpatialIndex SelectedSpatial
    {
        get
        {
            if (Selection.Count == Rois.Count)
            {
                return Snapshot.Spatial;
            }

            var value = Volatile.Read(ref _selectedSpatial);
            if (value is null)
            {
                value = new(() => Snapshot.IsSpatialCreated && Selection.Count > 64 && Selection.Count > Rois.Count / 8
                    ? Snapshot.Spatial.Filter(Selection, include: true)
                    : new RoiSpatialIndex(Selection.Select(id => Rois[id])));
                value = Interlocked.CompareExchange(ref _selectedSpatial, value, null) ?? value;
            }
            return value.Value;
        }
    }

    /// <summary>changedIds must include every added, removed, geometric or metadata change.</summary>
    public DocumentState WithRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => ReferenceEquals(Rois, rois) ? this : this with
        {
            _snapshot = new(_snapshot, rois, changedIds.Distinct().ToImmutableArray()),
            _selectedSpatial = null,
            _selectionFacts = null,
            _transformSelection = null
        };
    // Whole-document edits and large coherent groups preserve BVH membership; refit bounds without sorting.
    internal DocumentState WithRigidRois(ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds, CancellationToken token = default)
        => ReferenceEquals(Rois, rois) ? this : this with
        {
            _snapshot = new(_snapshot, rois, changedIds.ToImmutableArray(), refitSpatial: true, token: token),
            _selectedSpatial = null,
            _selectionFacts = null,
            _transformSelection = null
        };

    internal DocumentState WithRoisFrom(DocumentState before, ImmutableDictionary<Guid, Roi> rois, IEnumerable<Guid> changedIds)
        => this with
        {
            _snapshot = new(before._snapshot, rois, changedIds.Distinct().ToImmutableArray(), _snapshot),
            _selectedSpatial = null,
            _selectionFacts = null,
            _transformSelection = null
        };

    public bool Equals(DocumentState? other) => other is not null && ReferenceEquals(Rois, other.Rois) &&
        ReferenceEquals(Selection, other.Selection) && ReferenceEquals(RepeatedRois, other.RepeatedRois) &&
        ReferenceEquals(RepeatedSelection, other.RepeatedSelection) && Image == other.Image && Viewport == other.Viewport;

    public override int GetHashCode() => HashCode.Combine(Rois, Selection, RepeatedRois, RepeatedSelection, Image, Viewport);

    public static DocumentState Empty => new(ImmutableDictionary<Guid, Roi>.Empty,
        ImmutableHashSet<Guid>.Empty, null, ViewportTransform.Identity);
}
