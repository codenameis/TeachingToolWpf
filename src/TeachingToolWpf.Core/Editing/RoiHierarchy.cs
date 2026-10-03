using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public static class RoiHierarchy
{
    public static DocumentState Rebuild(DocumentState before, DocumentState next, CancellationToken token = default)
    {
        if (!ReferenceEquals(next.Snapshot.PreviousRois, before.Rois))
        {
            return next with
            {
                Rois = Rebuild(next.Rois, token)
            };
        }

        var changed = next.Snapshot.ChangedIds.ToHashSet();
        var index = next.Snapshot.Spatial;
        // A bulk paste can overlap every existing ROI. Querying descendants once per added
        // ROI then becomes quadratic; a single full parent pass uses the same exact rule.
        var evaluateAll = changed.Count > 64 && changed.Count > next.Rois.Count / 8;
        var affected = evaluateAll ? new HashSet<Guid>(next.Rois.Keys) : new HashSet<Guid>(changed);
        foreach (var id in changed)
        {
            if (evaluateAll)
            {
                break;
            }

            foreach (var child in before.Snapshot.Children.GetValueOrDefault(id, []))
            {
                affected.Add(child);
            }

            if (!next.Rois.TryGetValue(id, out var roi))
            {
                continue;
            }

            if (before.Rois.TryGetValue(id, out var old) && old.Geometry == roi.Geometry)
            {
                continue;
            }

            foreach (var child in index.Intersecting(roi.Geometry.Bounds))
            {
                if (child.Geometry.Area < roi.Geometry.Area)
                {
                    affected.Add(child.Id);
                }
            }
        }
        var result = next.Rois.ToBuilder();
        foreach (var id in affected)
        {
            token.ThrowIfCancellationRequested();
            if (!next.Rois.TryGetValue(id, out var child))
            {
                continue;
            }

            var parent = index.At(child.Geometry.ContainmentAnchor, child.Geometry.Area).Where(r => r.Geometry.Contains(child.Geometry))
                .OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id).FirstOrDefault()?.Id;
            if (child.ParentId != parent)
            {
                result[id] = child with
                {
                    ParentId = parent
                };
                changed.Add(id);
            }
        }
        var rois = result.ToImmutable();
        return ReferenceEquals(rois, next.Rois) ? next : next.WithRoisFrom(before, rois, changed);
    }

    public static ImmutableDictionary<Guid, Roi> Rebuild(ImmutableDictionary<Guid, Roi> rois, CancellationToken token = default)
    {
        var index = new RoiSpatialIndex(rois.Values);
        var result = rois.ToBuilder();
        foreach (var child in rois.Values)
        {
            token.ThrowIfCancellationRequested();
            Guid? parent = null;
            foreach (var candidate in index.At(child.Geometry.ContainmentAnchor, child.Geometry.Area).OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id))
            {
                if (!candidate.Geometry.Contains(child.Geometry.Center))
                {
                    continue;
                }

                if (!candidate.Geometry.Contains(child.Geometry))
                {
                    continue;
                }

                parent = candidate.Id;
                break;
            }
            if (child.ParentId != parent)
            {
                result[child.Id] = child with
                {
                    ParentId = parent
                };
            }
        }
        return result.ToImmutable();
    }

    public static IEnumerable<Guid> EffectiveRoots(DocumentState state) => state.Selection.Where(id =>
    {
        var parent = state.Rois[id].ParentId;
        while (parent is { } p)
        {
            if (state.Selection.Contains(p))
            {
                return false;
            }

            parent = state.Rois[p].ParentId;
        }
        return true;
    });

    public static HashSet<Guid> Subtree(DocumentState state, IEnumerable<Guid> roots, CancellationToken token = default)
    {
        var children = state.Snapshot.Children;
        var result = new HashSet<Guid>();
        var queue = new Queue<Guid>(roots);
        while (queue.TryDequeue(out var id))
        {
            token.ThrowIfCancellationRequested();
            if (!result.Add(id))
            {
                continue;
            }

            foreach (var child in children.GetValueOrDefault(id, []))
            {
                queue.Enqueue(child);
            }
        }
        return result;
    }

    public static bool IsDescendant(DocumentState state, Guid child, Guid ancestor)
    {
        for (var parent = state.Rois[child].ParentId;
parent is { } id;
parent = state.Rois[id].ParentId)
        {
            if (id == ancestor)
            {
                return true;
            }
        }

        return false;
    }
}
