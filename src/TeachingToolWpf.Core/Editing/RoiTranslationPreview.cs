using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Immutable geometry selection shared by all samples of one translation gesture.</summary>
public sealed class RoiTranslationSelection
{
    public DocumentState Before { get; }
    public ImmutableHashSet<Guid> Affected { get; }
    private readonly Lazy<RoiSpatialIndex> moving, stationary;
    public RoiSpatialIndex Moving => moving.Value;
    public RoiSpatialIndex Stationary => stationary.Value;
    public BoundsD Bounds { get; }
    public RoiTranslationSelection(DocumentState before)
    {
        Before = before;
        Affected = before.Selection.Count == before.Rois.Count ? before.Selection : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before)).ToImmutableHashSet();
        // Valid descendants are inside their selected ancestor, so the selected bounds cover them too.
        Bounds = before.SelectedSpatial.Bounds;
        var all = Affected.Count == before.Rois.Count;
        moving = new(() => all ? before.Snapshot.Spatial : Affected.SetEquals(before.Selection) ? before.SelectedSpatial : new(Affected.Select(id => before.Rois[id])));
        stationary = new(() => all ? new([]) : Affected.Count <= 64 ? before.Snapshot.Spatial.WithChanges(Affected.Select(id => (id, (Roi?)null)))
            : new(before.Rois.Values.Where(r => !Affected.Contains(r.Id))));
    }
    public PointD Constrain(PointD requested)
    {
        if (!requested.IsFinite) throw new ArgumentOutOfRangeException(nameof(requested));
        if (Before.Image is not { } image || Affected.IsEmpty) return requested;
        var fraction = 1d;
        if (requested.X > 0) fraction = Math.Min(fraction, (image.Width - Bounds.Right) / requested.X);
        if (requested.X < 0) fraction = Math.Min(fraction, -Bounds.Left / requested.X);
        if (requested.Y > 0) fraction = Math.Min(fraction, (image.Height - Bounds.Bottom) / requested.Y);
        if (requested.Y < 0) fraction = Math.Min(fraction, -Bounds.Top / requested.Y);
        return requested * Math.Clamp(fraction, 0, 1);
    }
    public bool PreservesHierarchy(PointD delta)
    {
        if (Affected.Count != Before.Rois.Count) return false;
        // Contains uses coordinate-scaled tolerance above 1e6. At such coordinates a common
        // translation can change the strict-parent threshold, so retain the exact rebuild path.
        const double fixedToleranceExtent = 1_000_000;
        return Math.Max(Math.Abs(Bounds.Left), Math.Abs(Bounds.Right)) <= fixedToleranceExtent &&
            Math.Max(Math.Abs(Bounds.Top), Math.Abs(Bounds.Bottom)) <= fixedToleranceExtent &&
            Math.Max(Math.Abs(Bounds.Left + delta.X), Math.Abs(Bounds.Right + delta.X)) <= fixedToleranceExtent &&
            Math.Max(Math.Abs(Bounds.Top + delta.Y), Math.Abs(Bounds.Bottom + delta.Y)) <= fixedToleranceExtent;
    }
    public DocumentState Apply(PointD delta, CancellationToken token = default)
    {
        if (delta == default || Affected.IsEmpty) return Before;
        var rois = Before.Rois.ToBuilder();
        foreach (var id in Affected) { token.ThrowIfCancellationRequested(); var roi = rois[id]; rois[id] = roi with { Geometry = roi.Geometry.Translate(delta) }; }
        return Before.WithRois(rois.ToImmutable(), Affected);
    }
}

public sealed record RoiTranslationPreview(RoiTranslationSelection Selection, PointD Delta)
{
    public RotatedRect Geometry(Roi roi) => Selection.Affected.Contains(roi.Id) ? roi.Geometry.Translate(Delta) : roi.Geometry;
}
