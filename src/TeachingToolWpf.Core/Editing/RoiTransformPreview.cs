using System.Collections.Frozen;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Immutable selection and indexes reused by every sample of a rigid edit.</summary>
public sealed class RoiTransformSelection
{
    public DocumentState Before { get; }
    public IReadOnlySet<Guid> Affected { get; }
    private readonly Lazy<RoiSpatialIndex> moving, stationary;
    private RotationConstraint? rotationConstraint;
    public RoiSpatialIndex Moving => moving.Value;
    public RoiSpatialIndex Stationary => stationary.Value;
    public BoundsD Bounds { get; }
    public bool IsPrepared
    {
        get
        {
            var cached = Volatile.Read(ref rotationConstraint);
            return moving.IsValueCreated && stationary.IsValueCreated && cached is { } constraint
                && constraint.Pivot == Bounds.Center && constraint.Limits.IsValueCreated;
        }
    }
    public RoiTransformSelection(DocumentState before)
    {
        Before = before;
        // Expanded membership never changes during a gesture; freeze once instead of building persistent tree nodes.
        Affected = before.Selection.Count == before.Rois.Count ? before.Selection
            : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before)).ToFrozenSet();
        Bounds = before.SelectedSpatial.Bounds;
        var all = Affected.Count == before.Rois.Count;
        moving = new(() => all ? before.Snapshot.Spatial : Affected.SetEquals(before.Selection) ? before.SelectedSpatial : new(Affected.Select(id => before.Rois[id])));
        stationary = new(() => all ? new([]) : Affected.Count <= 64 ? before.Snapshot.Spatial.WithChanges(Affected.Select(id => (id, (Roi?)null)))
            : new(before.Rois.Values.Where(r => !Affected.Contains(r.Id))));
    }
    public PointD Constrain(PointD requested)
    {
        if (!requested.IsFinite) throw new ArgumentOutOfRangeException(nameof(requested));
        if (Before.Image is not { } image || Affected.Count == 0) return requested;
        var bounds = Moving.Bounds;
        var fraction = 1d;
        if (requested.X > 0) fraction = Math.Min(fraction, (image.Width - bounds.Right) / requested.X);
        if (requested.X < 0) fraction = Math.Min(fraction, -bounds.Left / requested.X);
        if (requested.Y > 0) fraction = Math.Min(fraction, (image.Height - bounds.Bottom) / requested.Y);
        if (requested.Y < 0) fraction = Math.Min(fraction, -bounds.Top / requested.Y);
        return requested * Math.Clamp(fraction, 0, 1);
    }
    public RotationRange RotationLimits(PointD pivot)
    {
        if (!pivot.IsFinite) throw new ArgumentOutOfRangeException(nameof(pivot));
        var cached = Volatile.Read(ref rotationConstraint);
        if (cached?.Pivot != pivot)
        {
            cached = new(pivot, new(() => Before.Image is { } image && Affected.Count != 0 ? Moving.RotationLimits(pivot, image) : RotationRange.Unbounded));
            Interlocked.Exchange(ref rotationConstraint, cached);
        }
        return cached.Limits.Value;
    }
    public bool PreservesHierarchy(RigidTransform2D transform)
    {
        if (Affected.Count != Before.Rois.Count) return false;
        // Coordinate-scaled containment tolerance can change beyond 1e6; rebuild there.
        const double extent = 1_000_000;
        static bool Within(BoundsD b) => Math.Max(Math.Abs(b.Left), Math.Abs(b.Right)) <= extent &&
            Math.Max(Math.Abs(b.Top), Math.Abs(b.Bottom)) <= extent;
        return Within(Moving.Bounds) && Within(transform.TransformBounds(Moving.Bounds));
    }
    public DocumentState Apply(RigidTransform2D transform, CancellationToken token = default)
    {
        if (transform.IsIdentity || Affected.Count == 0) return Before;
        var rois = Before.Rois.ToBuilder();
        foreach (var id in Affected)
        {
            token.ThrowIfCancellationRequested(); var roi = rois[id];
            rois[id] = roi with { Geometry = TransformGeometry(roi.Geometry, transform) };
        }
        return Affected.Count == Before.Rois.Count ? Before.WithRigidRois(rois.ToImmutable(), Affected)
            : Before.WithRois(rois.ToImmutable(), Affected);
    }
    internal static RoiGeometry TransformGeometry(RoiGeometry geometry, RigidTransform2D transform) => transform.IsIdentity ? geometry
        : geometry.WithFrame(transform.Apply(geometry.Center), geometry.Width, geometry.Height, geometry.Angle + transform.AngleDegrees);
    private sealed record RotationConstraint(PointD Pivot, Lazy<RotationRange> Limits);
}

public sealed record RoiTransformPreview(RoiTransformSelection Selection, RigidTransform2D Transform)
{
    public RoiGeometry Geometry(Roi roi) => Selection.Affected.Contains(roi.Id) ? RoiTransformSelection.TransformGeometry(roi.Geometry, Transform) : roi.Geometry;
}
