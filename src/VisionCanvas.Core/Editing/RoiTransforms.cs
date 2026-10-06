using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public static class RoiTransforms
{
    public static DocumentState ResetAngles(DocumentState state)
    {
        var roots = RoiHierarchy.EffectiveRoots(state).ToArray();
        if (roots.Length == 0)
        {
            return state;
        }

        var targets = roots.ToDictionary(id => id, id =>
        {
            var rect = state.Rois[id].Geometry;
            return rect.WithFrame(rect.Center, rect.Width, rect.Height, 0);
        });
        var reset = ApplyCore(state, targets);
        if (ReferenceEquals(reset, state))
        {
            return state;
        }

        if (state.Image is not { } image || RoiImageBounds.Contains(reset, reset.Snapshot.ChangedIds))
        {
            return reset;
        }
        // Zero is an exact command: fit the transformed subtree as one unit if zero would cross an edge.
        var affected = RoiHierarchy.Subtree(state, roots);
        var bounds = new RoiSpatialIndex(affected.Select(id => reset.Rois[id])).Bounds;
        var scale = Math.Min(1, Math.Min(image.Width / bounds.Width, image.Height / bounds.Height));
        var halfWidth = Math.Min(image.Width / 2d, bounds.Width * scale / 2);
        var halfHeight = Math.Min(image.Height / 2d, bounds.Height * scale / 2);
        var center = new PointD(Math.Clamp(bounds.Center.X, halfWidth, image.Width - halfWidth), Math.Clamp(bounds.Center.Y, halfHeight, image.Height - halfHeight));
        var rois = reset.Rois.ToBuilder();
        foreach (var id in affected)
        {
            var roi = rois[id];
            var rect = roi.Geometry;
            rois[id] = roi with
            {
                Geometry = rect.WithFrame(center + ((rect.Center - bounds.Center) * scale), rect.Width * scale, rect.Height * scale, rect.Angle)
            };
        }
        return state.WithRois(rois.ToImmutable(), affected);
    }

    public static DocumentState Apply(DocumentState state, IEnumerable<Guid> roots, Func<Roi, RoiGeometry> transform)
    {
        var targets = roots.Select(id => (Id: id, Geometry: transform(state.Rois[id])))
            .Where(target => target.Geometry != state.Rois[target.Id].Geometry).ToDictionary(target => target.Id, target => target.Geometry);
        if (targets.Count == 0)
        {
            return state;
        }

        var requested = ApplyCore(state, targets);
        if (state.Image is null || RoiImageBounds.Contains(requested, requested.Snapshot.ChangedIds))
        {
            return requested;
        }
        // One fraction for the entire edit preserves multi-selection and descendant relationships.
        var low = 0d;
        var high = 1d;
        var accepted = state;
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var fraction = (low + high) / 2;
            var partial = targets.ToDictionary(pair => pair.Key,
                pair => RoiImageBounds.Interpolate(state.Rois[pair.Key].Geometry, pair.Value, fraction));
            var candidate = ApplyCore(state, partial);
            if (ReferenceEquals(candidate, state) || RoiImageBounds.Contains(candidate, candidate.Snapshot.ChangedIds))
            {
                low = fraction;
                accepted = candidate;
            }
            else
            {
                high = fraction;
            }
        }
        return accepted;
    }

    private static DocumentState ApplyCore(DocumentState state, IReadOnlyDictionary<Guid, RoiGeometry> targets)
    {
        var result = state.Rois.ToBuilder();
        var changed = new HashSet<Guid>();
        foreach (var (rootId, target) in targets)
        {
            var root = state.Rois[rootId];
            var rotation = target.Angle - root.Geometry.Angle;
            var translation = target.Center - root.Geometry.Center;
            foreach (var id in RoiHierarchy.Subtree(state, [rootId]))
            {
                var roi = state.Rois[id];
                var geometry = id == rootId ? target : roi.Geometry.RotateAround(root.Geometry.Center, rotation).Translate(translation);
                if (roi.Geometry == geometry)
                {
                    continue;
                }

                result[id] = roi with
                {
                    Geometry = geometry
                };
                changed.Add(id);
            }
        }
        return state.WithRois(result.ToImmutable(), changed);
    }
}
