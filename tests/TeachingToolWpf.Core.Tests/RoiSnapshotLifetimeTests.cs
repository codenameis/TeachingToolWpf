using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Core.Tests;

public class RoiSnapshotLifetimeTests
{
    [Fact]
    public void DerivedCachePopulationDoesNotChangeStateEqualityOrHash()
    {
        var editor = new EditorSession();
        editor.Add(new(new(100, 100), 20, 20, 0));
        var first = editor.State;
        var copy = first with
        {
            Viewport = first.Viewport
        };
        var hash = first.GetHashCode();
        _ = first.SelectedSpatial;
        _ = first.Snapshot.Children;
        Assert.Equal(first, copy);
        Assert.Equal(hash, first.GetHashCode());
    }

    [Fact]
    public void DeferredIndexesDoNotRetainUnboundedRenameHistory()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(100, 100), 20, 20, 0));
        var oldMap = RenameAndObserve(editor, id);
        for (var i = 0;
i < 2000;
i++)
        {
            editor.Rename(id, $"R{i}");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(oldMap.TryGetTarget(out _));
        Assert.Empty(editor.State.Snapshot.Children);
        Assert.Equal("R1999", Assert.Single(editor.State.Snapshot.Spatial.At(new(100, 100))).Name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ImmutableDictionary<Guid, Roi>> RenameAndObserve(EditorSession editor, Guid id)
    {
        editor.Rename(id, "first");
        return new(editor.State.Rois);
    }

    [Fact]
    public void ChildMovementUndoRedoKeepsExplicitHierarchyAndIndexesCurrent()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 100, 100, 0));
        var child = editor.AddChild(parent, new(new(100, 100), 10, 10, 0));
        editor.TranslateSelection(new(200, 0));
        for (var i = 0;
i < 2000;
i++)
        {
            editor.Undo();
            editor.Redo();
        }
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        Assert.Equal(child, Assert.Single(editor.State.Snapshot.Children[parent]));
        editor.Undo();
        Assert.Equal(child, Assert.Single(editor.State.Snapshot.Children[parent]));
        Assert.Contains(child, editor.State.Snapshot.Spatial.At(new(100, 100)).Select(r => r.Id));
    }
}
