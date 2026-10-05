using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>Immutable hierarchy changes shared by synchronous and prepared session commands.</summary>
internal static class RoiGrouping
{
    public static DocumentState Group(DocumentState before, long order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var roots = RoiHierarchy.EffectiveRoots(before).Select(id =>
        {
            token.ThrowIfCancellationRequested();
            return id;
        }).ToArray();
        if (roots.Length < 2)
        {
            throw new InvalidOperationException("독립적인 ROI를 2개 이상 선택하세요.");
        }

        IReadOnlySet<Guid> members = before.Selection.Count == before.Rois.Count ? before.Selection
            : RoiHierarchy.Subtree(before, roots, token);
        var bounds = Bounds(before, members, token);
        var padding = Math.Max(1, Math.Max(Math.Abs(bounds.Center.X), Math.Abs(bounds.Center.Y)) * 1e-10);
        var parent = before.Rois[roots[0]].ParentId;
        if (parent is not null)
        {
            foreach (var id in roots)
            {
                token.ThrowIfCancellationRequested();
                if (before.Rois[id].ParentId != parent)
                {
                    parent = null;
                    break;
                }
            }
        }

        var group = new Roi(Guid.NewGuid(), $"그룹 {order:000}",
            RoiImageBounds.ClipNew(new(bounds.Center, bounds.Width + (padding * 2), bounds.Height + (padding * 2), 0), before.Image),
            parent, true, order);
        var rois = before.Rois.ToBuilder();
        rois.Add(group.Id, group);
        foreach (var id in roots)
        {
            token.ThrowIfCancellationRequested();
            rois[id] = rois[id] with
            {
                ParentId = group.Id
            };
        }

        token.ThrowIfCancellationRequested();
        return before.WithRois(rois.ToImmutable(), roots.Append(group.Id)) with
        {
            Selection = [group.Id]
        };
    }

    public static DocumentState Ungroup(DocumentState before, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var groups = before.Selection.Where(id =>
        {
            token.ThrowIfCancellationRequested();
            return before.Rois[id].IsGroup;
        }).ToHashSet();
        if (groups.Count == 0)
        {
            return before;
        }

        var hierarchy = before.Snapshot.Children;
        var children = ImmutableHashSet.CreateBuilder<Guid>();
        var rois = before.Rois.ToBuilder();
        foreach (var id in groups)
        {
            token.ThrowIfCancellationRequested();
            rois.Remove(id);
            foreach (var child in hierarchy.GetValueOrDefault(id, []))
            {
                token.ThrowIfCancellationRequested();
                children.Add(child);
            }
        }

        children.ExceptWith(groups);
        foreach (var id in children)
        {
            token.ThrowIfCancellationRequested();
            var parent = before.Rois[id].ParentId;
            while (parent is { } ancestor && groups.Contains(ancestor))
            {
                token.ThrowIfCancellationRequested();
                parent = before.Rois[ancestor].ParentId;
            }

            rois[id] = rois[id] with
            {
                ParentId = parent
            };
        }

        token.ThrowIfCancellationRequested();
        return before.WithRois(rois.ToImmutable(), groups.Concat(children)) with
        {
            Selection = children.ToImmutable()
        };
    }

    private static BoundsD Bounds(DocumentState before, IReadOnlySet<Guid> members, CancellationToken token)
    {
        if (members.Count == before.Rois.Count && before.Snapshot.IsSpatialCreated)
        {
            return before.Snapshot.Spatial.Bounds;
        }

        var values = members.Count == before.Rois.Count ? before.Rois.Values : members.Select(id => before.Rois[id]);
        using var enumerator = values.GetEnumerator();
        enumerator.MoveNext();
        var bounds = enumerator.Current.Geometry.Bounds;
        while (enumerator.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            var next = enumerator.Current.Geometry.Bounds;
            bounds = new(Math.Min(bounds.Left, next.Left), Math.Min(bounds.Top, next.Top),
                Math.Max(bounds.Right, next.Right), Math.Max(bounds.Bottom, next.Bottom));
        }
        return bounds;
    }
}
