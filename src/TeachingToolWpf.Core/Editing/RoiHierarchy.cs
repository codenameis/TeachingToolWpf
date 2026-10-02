using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public static class RoiHierarchy
{
    public static DocumentState Rebuild(DocumentState before, DocumentState next)
    {
        if (!ReferenceEquals(next.Snapshot.PreviousRois, before.Rois)) return next with { Rois = Rebuild(next.Rois) };
        var changed = next.Snapshot.ChangedIds.ToHashSet();
        var index = next.Snapshot.Spatial;
        var affected = new HashSet<Guid>(changed);
        var allChanged = changed.Count >= next.Rois.Count && next.Rois.Keys.All(changed.Contains);
        foreach (var id in changed)
        {
            foreach (var child in before.Snapshot.Children.GetValueOrDefault(id, [])) affected.Add(child);
            if (allChanged) continue;
            if (!next.Rois.TryGetValue(id, out var roi)) continue;
            if (before.Rois.TryGetValue(id, out var old) && old.Geometry == roi.Geometry) continue;
            foreach (var child in index.Intersecting(roi.Geometry.Bounds))
                if (child.Geometry.Area < roi.Geometry.Area) affected.Add(child.Id);
        }
        var result = next.Rois.ToBuilder();
        foreach (var id in affected)
        {
            if (!next.Rois.TryGetValue(id, out var child)) continue;
            var parent = index.At(child.Geometry.Center, child.Geometry.Area).Where(r => r.Geometry.Contains(child.Geometry))
                .OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id).FirstOrDefault()?.Id;
            if (child.ParentId != parent) { result[id] = child with { ParentId = parent }; changed.Add(id); }
        }
        var rois = result.ToImmutable();
        return ReferenceEquals(rois, next.Rois) ? next : next.WithRoisFrom(before, rois, changed);
    }
    public static ImmutableDictionary<Guid, Roi> Rebuild(ImmutableDictionary<Guid, Roi> rois)
    {
        var index = new RoiSpatialIndex(rois.Values);
        var result = rois.ToBuilder();
        foreach (var child in rois.Values)
        {
            Guid? parent = null;
            foreach (var candidate in index.At(child.Geometry.Center, child.Geometry.Area).OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id))
            {
                if (!candidate.Geometry.Contains(child.Geometry.Center)) continue;
                if (!candidate.Geometry.Contains(child.Geometry)) continue;
                parent = candidate.Id;
                break;
            }
            if (child.ParentId != parent) result[child.Id] = child with { ParentId = parent };
        }
        return result.ToImmutable();
    }

    public static IEnumerable<Guid> EffectiveRoots(DocumentState state) => state.Selection.Where(id =>
    {
        var parent = state.Rois[id].ParentId;
        while (parent is { } p)
        {
            if (state.Selection.Contains(p)) return false;
            parent = state.Rois[p].ParentId;
        }
        return true;
    });

    public static HashSet<Guid> Subtree(DocumentState state, IEnumerable<Guid> roots)
    {
        var children = state.Snapshot.Children;
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.TryDequeue(out var id))
        {
            if (!result.Add(id)) continue;
            foreach (var child in children.GetValueOrDefault(id, [])) queue.Enqueue(child);
        }
        return result;
    }

    public static bool IsDescendant(DocumentState state, Guid child, Guid ancestor)
    {
        for (var parent = state.Rois[child].ParentId; parent is { } id; parent = state.Rois[id].ParentId)
            if (id == ancestor) return true;
        return false;
    }
}
