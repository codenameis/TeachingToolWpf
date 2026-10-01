using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public static class RoiHierarchy
{
    public static ImmutableDictionary<Guid, Roi> Rebuild(ImmutableDictionary<Guid, Roi> rois)
    {
        var index = new RoiSpatialIndex(rois.Values);
        var result = rois.ToBuilder();
        foreach (var child in rois.Values)
        {
            Guid? parent = null;
            foreach (var candidate in index.At(child.Geometry.Center).Where(r => r.Geometry.Area > child.Geometry.Area).OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id))
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
        var children = state.Rois.Values.Where(r => r.ParentId.HasValue).ToLookup(r => r.ParentId!.Value, r => r.Id);
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.TryDequeue(out var id))
        {
            if (!result.Add(id)) continue;
            foreach (var child in children[id]) queue.Enqueue(child);
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
