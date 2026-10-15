using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>Immutable hierarchy changes shared by synchronous and prepared session commands.</summary>
internal static class RoiGroupEditor
{
    // One existing parent receives the independent leaves. Multiple parents need a new outer group.
    private static Guid? FindParent(RoiSnapshot snapshot, IReadOnlyList<Guid> roots)
    {
        Guid? parent = null;
        foreach (var id in roots)
        {
            if (snapshot.GroupLevels.GetValueOrDefault(id) == 0)
            {
                continue;
            }
            if (parent is not null)
            {
                return null;
            }
            parent = id;
        }
        return parent;
    }

    internal static bool CanGroup(RoiSnapshot snapshot, IReadOnlyList<Guid> roots)
        => GroupError(snapshot, roots, FindParent(snapshot, roots)) is null;

    private static string? GroupError(RoiSnapshot snapshot, IReadOnlyList<Guid> roots, Guid? parent)
    {
        if (roots.Count < 2)
        {
            return "독립적인 ROI를 2개 이상 선택하세요.";
        }
        foreach (var id in roots)
        {
            if (id == parent)
            {
                continue;
            }
            if (snapshot.Rois[id].ParentId is not null)
            {
                return "이미 부모가 있는 ROI는 다시 그룹화할 수 없습니다.";
            }
            var parentDepth = parent is { } target ? RoiHierarchy.Depth(snapshot.Rois, target) : 1;
            if (parentDepth + snapshot.GroupLevels.GetValueOrDefault(id) + 1 > RoiHierarchy.MaximumDepth)
            {
                return "ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.";
            }
        }
        return null;
    }

    public static DocumentState Attach(DocumentState before, IReadOnlyList<Guid> roots, Guid parentId, CancellationToken token)
    {
        if (!before.Rois.ContainsKey(parentId) || roots.Count == 0)
        {
            throw new ArgumentException("연결할 ROI와 부모를 선택하세요.");
        }
        var parentDepth = RoiHierarchy.Depth(before.Rois, parentId);
        var rois = before.Rois.ToBuilder();
        foreach (var id in roots)
        {
            token.ThrowIfCancellationRequested();
            if (!before.Rois.TryGetValue(id, out var roi) || roi.ParentId is not null)
            {
                throw new InvalidOperationException("부모가 없는 ROI만 연결할 수 있습니다.");
            }
            if (id == parentId || RoiHierarchy.IsDescendant(before, parentId, id))
            {
                throw new InvalidOperationException("자기 자신이나 자손을 부모로 연결할 수 없습니다.");
            }
            if (parentDepth + before.Snapshot.GroupLevels.GetValueOrDefault(id) + 1 > RoiHierarchy.MaximumDepth)
            {
                throw new InvalidOperationException("ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.");
            }
            if (before.IsGroupRoi(id) && !before.Rois[parentId].Geometry.Covers(roi.Geometry))
            {
                throw new InvalidOperationException("연결할 ROI를 먼저 상위 그룹 도형 안으로 옮겨 주세요.");
            }
            rois[id] = roi with
            {
                ParentId = parentId
            };
        }
        var parent = before.Rois[parentId];
        var members = before.Snapshot.Children.GetValueOrDefault(parentId, []).Concat(roots).Select(id =>
        {
            token.ThrowIfCancellationRequested();
            return rois[id].Geometry;
        });
        var expanded = parent.Geometry.ExpandToCover(members);
        if (parent.ParentId is { } ancestor && !before.Rois[ancestor].Geometry.Covers(expanded))
        {
            throw new InvalidOperationException("확장한 ROI 그룹이 상위 그룹을 벗어나므로 그룹화할 수 없습니다.");
        }
        if (before.Image is { } image && !RoiImageBounds.Fits(expanded.Bounds, image))
        {
            throw new InvalidOperationException("확장한 ROI 그룹이 이미지를 벗어나므로 그룹화할 수 없습니다.");
        }
        rois[parentId] = parent with
        {
            Geometry = expanded
        };
        var changed = roots.Append(parentId).ToArray();
        var next = before.WithRois(rois.ToImmutable(), changed) with
        {
            Selection = [parentId]
        };
        RoiGroupBounds.Validate(next, changed, before);
        return next;
    }

    public static DocumentState Group(DocumentState before, long order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!before.RepeatedSelection.IsEmpty)
        {
            throw new InvalidOperationException("그룹화는 일반 ROI 선택에서 사용할 수 있습니다.");
        }

        var roots = RoiHierarchy.EffectiveRoots(before).Select(id =>
        {
            token.ThrowIfCancellationRequested();
            return id;
        }).ToArray();
        var parent = FindParent(before.Snapshot, roots);
        if (GroupError(before.Snapshot, roots, parent) is { } error)
        {
            throw new InvalidOperationException(error);
        }
        if (parent is { } existingParent)
        {
            return Attach(before, roots.Where(id => id != existingParent).ToArray(), existingParent, token);
        }

        IReadOnlySet<Guid> members = before.Selection.Count == before.Rois.Count ? before.Selection
            : RoiHierarchy.Subtree(before, roots, token);
        var bounds = Bounds(before, members, token);
        var padding = Math.Max(1, Math.Max(Math.Abs(bounds.Center.X), Math.Abs(bounds.Center.Y)) * 1e-10);
        var level = roots.Max(id => before.Snapshot.GroupLevels.GetValueOrDefault(id)) + 1;
        var group = new Roi(Guid.NewGuid(), $"{RoiHierarchy.LevelName(level)} {order:000}",
            RoiImageBounds.ClipNew(new(bounds.Center, bounds.Width + (padding * 2), bounds.Height + (padding * 2), 0), before.Image),
            null, true, order);
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
        var next = before.WithRois(rois.ToImmutable(), roots.Append(group.Id)) with
        {
            Selection = [group.Id]
        };
        RoiGroupBounds.Validate(next, roots.Append(group.Id), before);
        return next;
    }

    public static DocumentState Ungroup(DocumentState before, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!before.RepeatedSelection.IsEmpty)
        {
            throw new InvalidOperationException("그룹 해제는 일반 ROI 선택에서 사용할 수 있습니다.");
        }

        var groups = before.Selection.Where(id =>
        {
            token.ThrowIfCancellationRequested();
            return before.IsGroupRoi(id);
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
