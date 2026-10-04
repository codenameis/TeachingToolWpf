using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class EditingTests
{
    private static Guid Add(EditorSession editor, double x, double y, double width, double height, double angle = 0) =>
        editor.Add(new RoiGeometry(new(x, y), width, height, angle));

    [Fact]
    public void DeletingParentRemovesWholeTreeAndUndoRestoresIdsAndSelection()
    {
        var editor = new EditorSession();
        var parent = Add(editor, 100, 100, 180, 180);
        var child = editor.AddChild(parent, new(new(100, 100), 80, 80, 0));
        var grandchild = editor.AddChild(child, new(new(100, 100), 20, 20, 0));
        editor.Select([parent, grandchild]);
        editor.DeleteSelection();
        Assert.Empty(editor.State.Rois);
        editor.Undo();
        Assert.Equal(3, editor.State.Rois.Count);
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        Assert.Equal(child, editor.State.Rois[grandchild].ParentId);
        Assert.True(editor.State.Selection.SetEquals([parent, grandchild]));
        editor.Redo();
        Assert.Empty(editor.State.Rois);
    }

    [Fact]
    public void SelectingParentAndChildMovesDescendantExactlyOnce()
    {
        var editor = new EditorSession();
        var parent = Add(editor, 100, 100, 100, 100);
        var child = editor.AddChild(parent, new(new(100, 100), 10, 10, 0));
        editor.Select([parent, child]);
        editor.TranslateSelection(new(20, 30));
        Assert.Equal(new PointD(120, 130), editor.State.Rois[child].Geometry.Center);
        editor.Undo();
        Assert.Equal(new PointD(100, 100), editor.State.Rois[child].Geometry.Center);
    }

    [Fact]
    public void GroupAndUngroupPreserveChildrenAndAreReversible()
    {
        var editor = new EditorSession();
        var left = Add(editor, 50, 50, 20, 10, 45);
        var right = Add(editor, 150, 100, 40, 30, 125);
        var original = editor.State.Rois[left].Geometry;
        editor.Select([left, right]);
        var group = editor.GroupSelection();
        Assert.Equal(group, editor.State.Rois[left].ParentId);
        Assert.Equal(group, editor.State.Rois[right].ParentId);
        editor.UngroupSelection();
        Assert.Equal(2, editor.State.Rois.Count);
        Assert.Equal(original, editor.State.Rois[left].Geometry);
        editor.Undo();
        Assert.Equal(group, editor.State.Rois[left].ParentId);
        editor.Undo();
        Assert.Equal(2, editor.State.Rois.Count);
        Assert.True(editor.State.Selection.SetEquals([left, right]));
    }

    [Fact]
    public void UndoThenNoOpPreservesRedoButNewEditClearsIt()
    {
        var editor = new EditorSession();
        var id = Add(editor, 50, 50, 20, 20);
        editor.TranslateSelection(new(2, 0));
        editor.Undo();
        editor.TranslateSelection(new(0, 0));
        Assert.True(editor.CanRedo);
        editor.Rename(id, "검사 영역");
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void ResetUsesAbsoluteImageAngleAndRotatesChildren()
    {
        var editor = new EditorSession();
        var parent = Add(editor, 100, 100, 200, 200, 45);
        var child = editor.AddChild(parent, new(new(120, 100), 10, 10, 60));
        editor.Select([parent]);
        editor.ResetAngles();
        Assert.Equal(0, editor.State.Rois[parent].Geometry.Angle);
        Assert.Equal(15, editor.State.Rois[child].Geometry.Angle, 8);
        editor.Undo();
        Assert.Equal(60, editor.State.Rois[child].Geometry.Angle);
    }
}
