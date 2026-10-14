using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class ExplicitHierarchyTests
{
    [Fact]
    public void OutsideDescendantStillConstrainsParentAtImageEdge()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var parent = editor.Add(new(new(100, 100), 20, 20, 0));
        var child = editor.AddChild(parent, new(new(990, 500), 20, 20, 0));
        editor.Select([parent]);
        var before = editor.State;
        editor.TranslateSelection(new(100, 0));
        Assert.Equal(before.Rois, editor.State.Rois);
        editor.RotateSelection(-30, new(100, 100));
        Assert.All(editor.State.Rois.Values, roi => Assert.True(roi.Geometry.Bounds.Right <= 1000 + 1e-6));
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
    }

    [Fact]
    public async Task NestedGroupsUngroupTogetherAndCopiedChildrenResolveLaterParents()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var root = editor.Add(new(new(500, 500), 800, 800, 0));
        var left = editor.Add(new(new(400, 500), 20, 20, 0));
        var right = editor.Add(new(new(450, 500), 20, 20, 0));
        var other = editor.Add(new(new(600, 500), 20, 20, 0));
        var otherChild = editor.AddChild(other, new(new(600, 500), 10, 10, 0));
        editor.Select([left, right]);
        var inner = editor.GroupSelection();
        editor.Select([inner, other]);
        var outer = editor.GroupSelection();
        editor.CommitPrepared(await editor.PrepareAttachAsync([outer], root));
        editor.Select([outer]);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        editor.Paste(clipboard);
        var copiedOuter = editor.State.Selection.Single();
        Assert.Null(editor.State.Rois[copiedOuter].ParentId);
        Assert.Equal(6, RoiHierarchy.Subtree(editor.State, [copiedOuter]).Count);
        editor.Undo();
        editor.Select([inner, outer]);
        var before = editor.State;
        editor.UngroupSelection();
        Assert.All(new[] { left, right, other }, id => Assert.Equal(root, editor.State.Rois[id].ParentId));
        editor.Undo();
        Assert.Equal(before.Rois, editor.State.Rois);
        editor.Redo();
        Assert.Equal(5, editor.State.Rois.Count);
        Assert.Equal(other, editor.State.Rois[otherChild].ParentId);
    }

    [Fact]
    public void BatchHierarchyIsExplicitAndInvalidReferencesAreAtomic()
    {
        var editor = new EditorSession();
        editor.AddRange(new RoiCreation[]
        {
            new(new(new(100, 100), 10, 10, 0)),
            new(new(new(300, 300), 10, 10, 0), 0),
            new(new(new(500, 500), 10, 10, 0), 1)
        });
        var rois = editor.State.Rois.Values.OrderBy(r => r.Order).ToArray();
        Assert.Equal(rois[0].Id, rois[1].ParentId);
        Assert.Equal(rois[1].Id, rois[2].ParentId);
        editor.Undo();
        var before = editor.State;
        Assert.Throws<ArgumentException>(() => editor.AddRange(new RoiCreation[]
        {
            new(new(new(10, 10), 10, 10, 0)),
            new(new(new(10, 10), 10, 10, 0), 1)
        }));
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(3, editor.State.Rois.Count);
    }

    [Fact]
    public void PolygonChildUsesCapturedParentAndMissingParentPreservesDraft()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 100, 100, 0));
        var input = new InteractionController(editor)
        {
            DrawShape = RoiShapeKind.Polygon,
            DrawParentId = parent,
            Tool = EditorTool.Draw
        };
        foreach (var point in new PointD[] { new(80, 80), new(120, 80), new(100, 120) })
        {
            input.Down(point);
            input.Up(point);
        }

        editor.DeleteSelection();
        var before = editor.State;
        Assert.Throws<ArgumentException>(input.CompletePolygon);
        Assert.Same(before, editor.State);
        Assert.Equal(3, input.PolygonPointCount);
        editor.Undo();
        input.CompletePolygon();
        var child = editor.State.Selection.Single();
        Assert.Equal(parent, editor.State.Rois[child].ParentId);
        Assert.Equal(EditorTool.Select, input.Tool);
        editor.Undo();
        Assert.Single(editor.State.Rois);
    }

    [Fact]
    public async Task OverlapNeverCreatesParentsThroughAnyEditPath()
    {
        var editor = new EditorSession();
        var outer = editor.Add(new(new(100, 100), 400, 400, 0));
        var inner = editor.Add(new(new(100, 100), 20, 20, 0));
        Assert.Null(editor.State.Rois[inner].ParentId);
        editor.TranslateSelection(new(10, 10));
        editor.RotateSelection(30, new(100, 100));
        editor.SetGeometry(inner, new(new(100, 100), 30, 30, 5));
        editor.BeginPreview();
        editor.PreviewTranslation(new(5, 5));
        editor.CommitPrepared(await editor.PreparePreviewAsync("이동"));
        editor.CommitPrepared(await editor.PrepareTranslationAsync(new(-5, -5)));
        editor.CommitPrepared(await editor.PrepareRotationAsync(10, new(100, 100)));
        Assert.Null(editor.State.Rois[inner].ParentId);
        editor.Select([outer]);
        editor.DeleteSelection();
        Assert.True(editor.State.Rois.ContainsKey(inner));
    }

    [Fact]
    public void ExplicitChildOutsideParentFollowsParentAndRestoresThroughHistory()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 20, 20, 0));
        var child = editor.AddChild(parent, new(new(300, 300), 10, 10, 0));
        var leaf = editor.AddChild(child, new(new(400, 400), 5, 5, 0));
        editor.Select([parent, leaf]);
        var before = editor.State;
        editor.TranslateSelection(new(20, 30));
        Assert.Equal(new PointD(320, 330), editor.State.Rois[child].Geometry.Center);
        Assert.Equal(new PointD(420, 430), editor.State.Rois[leaf].Geometry.Center);
        editor.DeleteSelection();
        Assert.Empty(editor.State.Rois);
        editor.Undo();
        editor.Undo();
        Assert.Equal(before.Rois.OrderBy(p => p.Key), editor.State.Rois.OrderBy(p => p.Key));
        editor.Redo();
        editor.Redo();
        Assert.Empty(editor.State.Rois);
    }

    [Fact]
    public void ExistingParentPreventsRegroupingAndPreservesUnrelatedRoi()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(100, 100), 500, 500, 0));
        var left = editor.AddChild(parent, new(new(50, 100), 20, 20, 0));
        var right = editor.AddChild(parent, new(new(150, 100), 20, 20, 0));
        var untouched = editor.Add(new(new(100, 100), 10, 10, 0));
        editor.Select([left, right]);
        var before = editor.State;
        Assert.Throws<InvalidOperationException>(() => editor.GroupSelection());
        Assert.Same(before, editor.State);
        Assert.Null(editor.State.Rois[untouched].ParentId);
        Assert.Equal(parent, editor.State.Rois[left].ParentId);
        Assert.Equal(parent, editor.State.Rois[right].ParentId);
    }

    [Fact]
    public void MissingParentDoesNotChangeDocumentOrRedo()
    {
        var editor = new EditorSession();
        editor.Add(new(new(100, 100), 100, 100, 0));
        editor.Undo();
        var before = editor.State;
        Assert.Throws<ArgumentException>(() => editor.AddChild(Guid.NewGuid(), new(new(0, 0), 10, 10, 0)));
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
    }
}
