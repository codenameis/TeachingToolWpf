namespace VisionCanvas.Core.Editing;

public static class RoiHierarchy
{
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
