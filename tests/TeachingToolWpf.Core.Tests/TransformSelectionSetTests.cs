using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class TransformSelectionSetTests
{
    [Fact]
    public void ExpandedMembershipContainsEachDescendantOnceAndExcludesOtherRoots()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(400, 400), 200, 200, 0));
        var child = editor.AddChild(parent, new(new(410, 400), 80, 80, 0));
        var grandchild = editor.AddChild(child, new(new(420, 400), 20, 20, 0));
        var other = editor.Add(new(new(800, 800), 20, 20, 0));
        editor.Select([parent, child]);
        var before = editor.State;
        var selection = before.TransformSelection;
        Assert.True(selection.Affected.SetEquals([parent, child, grandchild]));
        Assert.Equal(3, selection.Affected.Count);
        Assert.False(selection.Affected.Contains(other));
        var delta = new PointD(13, -7);
        var after = selection.Apply(new(default, 0, delta));
        foreach (var id in selection.Affected)
        {
            Assert.Equal(before.Rois[id].Geometry.Center + delta, after.Rois[id].Geometry.Center);
        }

        Assert.Same(before.Rois[other], after.Rois[other]);
        editor.Select([other]);
        Assert.True(selection.Affected.SetEquals([parent, child, grandchild]));
        Assert.True(editor.State.TransformSelection.Affected.SetEquals([other]));
    }

    [Fact]
    public void WholeDocumentSelectionReusesExistingImmutableMembership()
    {
        var editor = new EditorSession();
        editor.AddRange(Enumerable.Range(0, 20).Select(index => new RoiGeometry(new(index * 30, 100), 10, 10, 0)));
        editor.Select(editor.State.Rois.Keys);
        var state = editor.State;
        Assert.Same(state.Selection, state.TransformSelection.Affected);
        Assert.Same(state.Snapshot.Spatial, state.TransformSelection.Moving);
        Assert.Empty(state.TransformSelection.Stationary.Intersecting(state.Snapshot.Spatial.Bounds));
    }

    [Fact]
    public void EmptyMembershipLeavesOriginalDocumentUnchanged()
    {
        var editor = new EditorSession();
        editor.Add(new(new(400, 400), 20, 20, 0));
        editor.Select([]);
        var state = editor.State;
        var selection = state.TransformSelection;
        Assert.Empty(selection.Affected);
        Assert.Same(state, selection.Apply(new(new(400, 400), 30, new(10, 20))));
        Assert.Equal(RotationRange.Unbounded, selection.RotationLimits(default));
    }
}
