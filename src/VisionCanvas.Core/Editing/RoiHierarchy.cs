using System.Collections.Immutable;

namespace VisionCanvas.Core.Editing;

public static class RoiHierarchy
{
    public const int MaximumDepth = 4;

    public static string LevelName(int level) => level switch
    {
        0 => "ROI",
        1 => "ROI 그룹",
        2 => "모듈 그룹",
        3 => "루트 그룹",
        > 3 => $"초과 계층 ({level + 1}단계)",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static int Depth(IReadOnlyDictionary<Guid, Roi> rois, Guid id)
    {
        var depth = 1;
        for (var parent = rois[id].ParentId; parent is { } ancestor; parent = rois[ancestor].ParentId)
        {
            depth++;
        }
        return depth;
    }

    internal static ImmutableDictionary<Guid, int> BuildGroupLevels(IReadOnlyDictionary<Guid, Roi> rois)
    {
        var levels = new Dictionary<Guid, int>();
        foreach (var roi in rois.Values)
        {
            var level = roi.IsGroup ? 1 : 0;
            if (roi.IsGroup)
            {
                levels[roi.Id] = Math.Max(levels.GetValueOrDefault(roi.Id), level);
            }
            for (var parent = roi.ParentId; parent is { } id; parent = rois[id].ParentId)
            {
                level++;
                if (levels.GetValueOrDefault(id) >= level)
                {
                    break;
                }
                levels[id] = level;
            }
        }
        return levels.ToImmutableDictionary();
    }

    public static IEnumerable<Guid> EffectiveRoots(DocumentState state) => EffectiveRoots(state.Rois, state.Selection);

    internal static IEnumerable<Guid> EffectiveRoots(IReadOnlyDictionary<Guid, Roi> rois, IReadOnlySet<Guid> selection) => selection.Where(id =>
    {
        var parent = rois[id].ParentId;
        while (parent is { } ancestor)
        {
            if (selection.Contains(ancestor))
            {
                return false;
            }

            parent = rois[ancestor].ParentId;
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

            var descendants = children.GetValueOrDefault(id, []);
            if (!descendants.IsEmpty)
            {
                // 자식 수를 이미 알고 있으므로 큰 그룹에서 큐와 집합의 반복 확장을 피한다.
                var pending = queue.Count + descendants.Length;
                queue.EnsureCapacity(pending);
                result.EnsureCapacity(result.Count + pending);
            }
            foreach (var child in descendants)
            {
                queue.Enqueue(child);
            }
        }
        return result;
    }

    public static bool IsDescendant(DocumentState state, Guid child, Guid ancestor)
    {
        for (var parent = state.Rois[child].ParentId; parent is { } id; parent = state.Rois[id].ParentId)
        {
            if (id == ancestor)
            {
                return true;
            }
        }

        return false;
    }
}
