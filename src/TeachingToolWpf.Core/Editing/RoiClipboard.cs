using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>In-app value snapshot; retains neither source images nor edit history.</summary>
public sealed class RoiClipboard
{
    private ImmutableArray<Roi> entries = [];
    private int pastes;
    public bool HasContent => !entries.IsEmpty;
    public int Copy(DocumentState state)
    {
        var ids = RoiHierarchy.Subtree(state, RoiHierarchy.EffectiveRoots(state));
        if (ids.Count == 0) return 0;
        entries = state.Rois.Values.Where(r => ids.Contains(r.Id)).OrderBy(r => r.Order).ToImmutableArray();
        pastes = 0;
        return entries.Length;
    }
    internal ImmutableArray<Roi> Place(ImageReference image, PointD step)
    {
        if (!HasContent) return [];
        var bounds = BoundsD.FromPoints(entries.SelectMany(r => r.Geometry.Corners));
        var scale = Math.Min(1, Math.Min(image.Width / bounds.Width, image.Height / bounds.Height));
        var halfWidth = bounds.Width * scale / 2; var halfHeight = bounds.Height * scale / 2;
        var desired = bounds.Center + step * (pastes + 1);
        var center = new PointD(Math.Clamp(desired.X, halfWidth, image.Width - halfWidth),
            Math.Clamp(desired.Y, halfHeight, image.Height - halfHeight));
        return entries.Select(r => r with
        {
            Geometry = new(center + (r.Geometry.Center - bounds.Center) * scale,
                r.Geometry.Width * scale, r.Geometry.Height * scale, r.Geometry.Angle)
        }).ToImmutableArray();
    }
    internal void Pasted() => pastes++;
}
