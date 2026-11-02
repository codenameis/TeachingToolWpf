using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class RepeatedDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedScaledPastePreservesOrderAndValidatesOnlyCopiedCells(bool onlyOverride)
    {
        var editor = Create();
        var pitch = onlyOverride ? .025 : 1;
        var geometries = CreateGridGeometries(2, 1, pitch, new(4000, 4000));
        var ids = editor.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        editor.SetGeometry(ids[0], new(new(4000, 4000), .01, .01, 0));
        if (onlyOverride)
        {
            editor.SetGeometry(ids[1], new(new(4001, 4000), 1, 1, 0));
        }

        var fullImageRoi = editor.Add(new(new(4096, 4096), 8192, 8192, 0));
        editor.Select([ids[1], fullImageRoi]);
        var clipboard = new RoiClipboard();
        Assert.Equal(2, clipboard.Copy(editor.State));
        editor.SetImage(new("small", "small", 2048, 2048), ViewportTransform.Identity);
        editor.Paste(clipboard);
        Assert.Equal(2, editor.State.RoiCount);
        var pastedRois = editor.State.Rois.Values.OrderBy(r => r.Order).ToArray();
        Assert.True(pastedRois[0].Order < pastedRois[1].Order);
        var geometry = pastedRois[0].Geometry;
        var resized = geometry.WithFrame(geometry.Center, 1, 1, 17);
        editor.SetGeometry(pastedRois[0].Id, resized);
        Assert.Equal(resized, editor.State.Rois[pastedRois[0].Id].Geometry);
        editor.Undo();
        Assert.Equal(geometry, editor.State.Rois[pastedRois[0].Id].Geometry);
    }

    [Fact]
    public void BatchGridSupportsGroupAndUngroupWithUndoRedo()
    {
        var editor = Create();
        var geometries = CreateGridGeometries(11, 11, 20, new(4096, 4096));
        var added = editor.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        Assert.Equal(121, editor.State.RoiCount);
        Assert.Equal(121, editor.State.SelectionCount);
        Assert.True(editor.State.SelectionFacts.CanGroup);

        var groupId = editor.GroupSelection();
        Assert.Equal(122, editor.State.RoiCount);
        Assert.Equal(groupId, Assert.Single(editor.State.Selection));
        Assert.True(editor.State.IsGroupRoi(groupId));
        Assert.All(added, id => Assert.Equal(groupId, editor.State.Rois[id].ParentId));

        editor.UngroupSelection();
        Assert.Equal(121, editor.State.RoiCount);
        Assert.All(added, id => Assert.Null(editor.State.Rois[id].ParentId));

        editor.Undo();
        Assert.Equal(122, editor.State.RoiCount);
        Assert.True(editor.State.IsGroupRoi(groupId));

        editor.Redo();
        Assert.Equal(121, editor.State.RoiCount);
    }

    [Fact]
    public async Task BatchGridPartialEditCopyPasteAndUndoUseOneDocumentHistory()
    {
        var editor = Create();
        var ordinary = editor.Add(new(new(100, 100), 20, 20, 0));
        const int sideCount = 31;
        var geometries = CreateGridGeometries(sideCount, sideCount, 10, new(4096, 4096));
        var added = editor.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        var originalRois = editor.State.Rois;
        var totalBatch = sideCount * sideCount;
        var halfBatch = (sideCount / 2) * sideCount;
        Assert.Equal(totalBatch + 1, editor.State.RoiCount);
        editor.Select([]);
        editor.CommitPrepared(await editor.PrepareRegionSelectionAsync(new(0, 0, 4096, 8192)));
        Assert.Equal(halfBatch + 1, editor.State.SelectionCount);
        Assert.Contains(ordinary, editor.State.Selection);
        var selectedId = added[0];
        var unselectedId = added[^1];
        editor.TranslateSelection(new(10, 20));
        var movedRois = editor.State.Rois;
        Assert.Equal(originalRois[selectedId].Geometry.Center + new PointD(10, 20), movedRois[selectedId].Geometry.Center);
        Assert.Equal(originalRois[unselectedId].Geometry, movedRois[unselectedId].Geometry);
        Assert.Equal(new PointD(110, 120), editor.State.Rois[ordinary].Geometry.Center);

        var clipboard = new RoiClipboard();
        Assert.Equal(halfBatch + 1, clipboard.Copy(editor.State));
        var beforePaste = editor.State;
        var paste = await editor.PreparePasteAsync(clipboard);
        editor.CommitPreparedPaste(paste);
        Assert.Equal(totalBatch + 1 + halfBatch + 1, editor.State.RoiCount);
        Assert.Equal(halfBatch + 1, editor.State.SelectionCount);
        editor.Undo();
        Assert.Equal(beforePaste.RoiCount, editor.State.RoiCount);
        Assert.Same(movedRois, editor.State.Rois);
        editor.Undo();
        Assert.Equal(originalRois, editor.State.Rois);
        Assert.Equal(new PointD(100, 100), editor.State.Rois[ordinary].Geometry.Center);
        editor.Redo();
        editor.Redo();
        Assert.Equal(totalBatch + 1 + halfBatch + 1, editor.State.RoiCount);
    }

    [Fact]
    public void IndividualEditDeleteAndImageReplacementRestoreIdentityAndSelection()
    {
        var editor = Create();
        var geometries = CreateGridGeometries(21, 21, 10, new(4096, 4096));
        var added = editor.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        var cellId = added[added.Length / 2];
        editor.Select([cellId]);
        var old = editor.State.Rois[cellId].Geometry;
        var next = new RoiGeometry(new(200, 200), 30, 40, 27);
        editor.SetGeometry(cellId, next);
        Assert.Equal(next, editor.State.Rois[cellId].Geometry);
        editor.DeleteSelection();
        Assert.Equal(added.Length - 1, editor.State.RoiCount);
        Assert.Equal(0, editor.State.SelectionCount);
        Assert.False(editor.State.Rois.ContainsKey(cellId));
        editor.Undo();
        Assert.Equal(cellId, Assert.Single(editor.State.Selection));
        editor.Undo();
        Assert.Equal(old, editor.State.Rois[cellId].Geometry);
        editor.SetImage(new("next", "next", 2048, 2048), ViewportTransform.Identity);
        Assert.Equal(0, editor.State.RoiCount);
        editor.Undo();
        Assert.Equal(added.Length, editor.State.RoiCount);
        Assert.Equal(cellId, Assert.Single(editor.State.Selection));
    }

    [Fact]
    public async Task PreparedChangesRejectNewEditsAndClipboardChanges()
    {
        var editor = Create();
        var geometries = CreateGridGeometries(11, 11, 5, new(400, 400));
        var added = editor.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        var prepared = await editor.PrepareRegionSelectionAsync(new(0, 0, 400, 8192));
        editor.SetGeometry(added[12], new(new(150, 150), 5, 5, 0));
        Assert.Throws<InvalidOperationException>(() => editor.CommitPrepared(prepared));
        editor.SelectAll();
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        var paste = await editor.PreparePasteAsync(clipboard);
        clipboard.Copy(editor.State);
        Assert.Throws<InvalidOperationException>(() => editor.CommitPreparedPaste(paste));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.PrepareRegionSelectionAsync(new(0, 0, 8192, 8192), token: cancellation.Token));
        Assert.Equal(121, editor.State.RoiCount);
    }

    [Fact]
    public void PartialRotationPreviewCancelAndCommitPreserveUnselectedGeometry()
    {
        var editor = Create();
        var geometries = CreateGridGeometries(31, 29, 5, new(400, 400));
        var added = editor.AddRange(geometries, selectAdded: true, isNameVisible: false).ToArray();
        editor.SelectRegion(new(0, 0, 400, 8192));
        var before = editor.State;
        var pivot = before.TransformSelection.Bounds.Center;
        editor.BeginPreview();
        editor.PreviewRotation(pivot, 43);
        editor.CancelPreview();
        Assert.Same(before.Rois, editor.State.Rois);
        editor.BeginPreview();
        editor.PreviewRotation(pivot, 43);
        editor.CommitPreview("부분 회전");
        var transform = new RigidTransform2D(pivot, 43);
        foreach (var id in added)
        {
            var old = before.Rois[id].Geometry;
            var actual = editor.State.Rois[id].Geometry;
            var expected = before.Selection.Contains(id) ? transform.Apply(old.Center) : old.Center;
            Assert.Equal(expected.X, actual.Center.X, 8);
            Assert.Equal(expected.Y, actual.Center.Y, 8);
        }
    }

    private static RoiGeometry[] CreateGridGeometries(int columns, int rows, double pitch, PointD center)
    {
        var width = Math.Max(pitch * .65, 1e-4);
        var halfColumns = (columns - 1) / 2d;
        var halfRows = (rows - 1) / 2d;
        var geometries = new RoiGeometry[columns * rows];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            var y = center.Y + ((row - halfRows) * pitch);
            for (var column = 0; column < columns; column++)
            {
                var x = center.X + ((column - halfColumns) * pitch);
                geometries[index++] = new RoiGeometry(new(x, y), width, width, 0);
            }
        }
        return geometries;
    }

    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 8192, 8192), ViewportTransform.Identity);
        return editor;
    }
}
