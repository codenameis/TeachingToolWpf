using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Core.Tests;

public class RepeatedDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedScaledPastePreservesOrderAndValidatesOnlyCopiedCells(bool onlyOverride)
    {
        var editor = Create();
        var id = editor.AddRepeatedGrid(new(2, 1, onlyOverride ? .025 : 1, new(4000, 4000)));
        editor.SetRepeatedGeometry(new(id, 0), new(new(4000, 4000), .01, .01, 0));
        if (onlyOverride)
        {
            editor.SetRepeatedGeometry(new(id, 1), new(new(4001, 4000), 1, 1, 0));
        }

        editor.Add(new(new(4096, 4096), 8192, 8192, 0));
        editor.SelectRepeated(new(id, 1), additive: true);
        var clipboard = new RoiClipboard();
        Assert.Equal(2, clipboard.Copy(editor.State));
        editor.SetImage(new("small", "small", 2048, 2048), ViewportTransform.Identity);
        editor.Paste(clipboard);
        Assert.Equal(2, editor.State.RoiCount);
        var batch = Assert.Single(editor.State.RepeatedRois).Value;
        Assert.True(batch.Order < Assert.Single(editor.State.Rois).Value.Order);
        Assert.Single(batch.Cells.Ranges);
        Assert.Equal(onlyOverride ? .25 : .1, batch.Geometry(1).Height, 8);
        Assert.False(batch.Cells.Contains(0));
        var geometry = batch.Geometry(1);
        var resized = geometry.WithFrame(geometry.Center, 1, 1, 17);
        editor.SetRepeatedGeometry(new(batch.Id, 1), resized);
        Assert.Equal(resized, editor.State.RepeatedRois[batch.Id].Geometry(1));
        editor.Undo();
        Assert.Equal(geometry, editor.State.RepeatedRois[batch.Id].Geometry(1));
    }

    [Fact]
    public void FragmentedHistoryAccountsForRetainedGeometryAndIndexCaches()
    {
        var batch = new RepeatedRoiBatch(Guid.NewGuid(), "fragmented", 1, new(10001, 10001, .25, new(4096, 4096)));
        var cells = RoiCellSet.Create(Enumerable.Range(0, 5000).Select(index => new RoiCellSet.Range(index * 2, (index * 2) + 1)));
        var partial = batch.Slice(cells)!;
        var before = DocumentState.Empty;
        var after = before with
        {
            RepeatedRois = before.RepeatedRois.Add(partial.Id, partial)
        };
        var edit = new DocumentEdit("fragmented", before, after);
        Assert.True(edit.EstimatedBytes > 1024 * 1024);
        var history = new EditHistory(byteBudget: 1024 * 1024);
        Assert.Throws<InvalidOperationException>(() => history.Push(edit));
    }

    [Fact]
    public async Task HundredMillionPartialEditCopyPasteAndUndoUseOneDocumentHistory()
    {
        var editor = Create();
        var ordinary = editor.Add(new(new(100, 100), 20, 20, 0));
        var id = editor.AddRepeatedGrid(new(10001, 10001, .25, new(4096, 4096)));
        var original = editor.State.RepeatedRois[id];
        Assert.Equal(100020002, editor.State.RoiCount);
        editor.Select([]);
        editor.CommitPrepared(await editor.PrepareRegionSelectionAsync(new(0, 0, 4096, 8192)));
        Assert.Equal(50005001, editor.State.SelectionCount);
        Assert.Contains(ordinary, editor.State.Selection);
        Assert.Equal(10001, editor.State.RepeatedSelection[id].Ranges.Length);
        var selectedIndex = 10001 * 5000;
        var unselectedIndex = selectedIndex + 9000;
        editor.TranslateSelection(new(10, 20));
        var moved = editor.State.RepeatedRois[id];
        Assert.Equal(original.Geometry(selectedIndex).Center + new PointD(10, 20), moved.Geometry(selectedIndex).Center);
        Assert.Equal(original.Geometry(unselectedIndex), moved.Geometry(unselectedIndex));
        Assert.Equal(2, moved.Parts.Length);
        Assert.Equal(new PointD(110, 120), editor.State.Rois[ordinary].Geometry.Center);
        Assert.True(editor.HistoryBytes < 1024 * 1024);

        var clipboard = new RoiClipboard();
        Assert.Equal(50005001, clipboard.Copy(editor.State));
        var beforePaste = editor.State;
        var paste = await editor.PreparePasteAsync(clipboard);
        editor.CommitPreparedPaste(paste);
        Assert.Equal(150025003, editor.State.RoiCount);
        Assert.Equal(50005001, editor.State.SelectionCount);
        var copiedId = Assert.Single(editor.State.RepeatedSelection).Key;
        Assert.NotEqual(id, copiedId);
        Assert.Equal(50005000, editor.State.RepeatedRois[copiedId].Count);
        Assert.Single(editor.State.RepeatedRois[copiedId].Parts);
        editor.Undo();
        Assert.Equal(beforePaste.RoiCount, editor.State.RoiCount);
        Assert.Same(moved, editor.State.RepeatedRois[id]);
        editor.Undo();
        Assert.Same(original, editor.State.RepeatedRois[id]);
        Assert.Equal(new PointD(100, 100), editor.State.Rois[ordinary].Geometry.Center);
        editor.Redo();
        editor.Redo();
        Assert.Equal(150025003, editor.State.RoiCount);
        Assert.Equal(copiedId, Assert.Single(editor.State.RepeatedSelection).Key);
    }

    [Fact]
    public void IndividualEditDeleteAndImageReplacementRestoreIdentityAndSelection()
    {
        var editor = Create();
        var id = editor.AddRepeatedGrid(new(10001, 10001, .25, new(4096, 4096)));
        var cell = new RepeatedRoiCell(id, 60000000);
        editor.SelectRepeated(cell);
        var old = editor.State.RepeatedRois[id].Geometry(cell.Index);
        var next = new RoiGeometry(new(200, 200), 30, 40, 27);
        editor.SetRepeatedGeometry(cell, next);
        Assert.Equal(next, editor.State.RepeatedRois[id].Geometry(cell.Index));
        editor.DeleteSelection();
        Assert.Equal(100020000, editor.State.RoiCount);
        Assert.Equal(0, editor.State.SelectionCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.State.RepeatedRois[id].Geometry(cell.Index));
        editor.Undo();
        Assert.Equal(cell, editor.State.SingleRepeatedCell);
        editor.Undo();
        Assert.Equal(old, editor.State.RepeatedRois[id].Geometry(cell.Index));
        editor.SetImage(new("next", "next", 2048, 2048), ViewportTransform.Identity);
        Assert.Equal(0, editor.State.RoiCount);
        editor.Undo();
        Assert.Equal(100020001, editor.State.RoiCount);
        Assert.Equal(cell, editor.State.SingleRepeatedCell);
    }

    [Fact]
    public async Task PreparedChangesRejectNewRepeatedEditsAndClipboardChanges()
    {
        var editor = Create();
        var id = editor.AddRepeatedGrid(new(11, 11, 5, new(400, 400)));
        var prepared = await editor.PrepareRegionSelectionAsync(new(0, 0, 400, 8192));
        editor.SetRepeatedGeometry(new(id, 12), new(new(150, 150), 5, 5, 0));
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
        var id = editor.AddRepeatedGrid(new(31, 29, 5, new(400, 400)));
        editor.SelectRegion(new(0, 0, 400, 8192));
        var before = editor.State;
        var pivot = before.TransformSelection.Bounds.Center;
        editor.BeginPreview();
        editor.PreviewRotation(pivot, 43);
        editor.CancelPreview();
        Assert.Same(before.RepeatedRois, editor.State.RepeatedRois);
        editor.BeginPreview();
        editor.PreviewRotation(pivot, 43);
        editor.CommitPreview("부분 회전");
        var transform = new RigidTransform2D(pivot, 43);
        for (var index = 0; index < 31 * 29; index++)
        {
            var old = before.RepeatedRois[id].Geometry(index);
            var actual = editor.State.RepeatedRois[id].Geometry(index);
            var expected = before.RepeatedSelection[id].Contains(index) ? transform.Apply(old.Center) : old.Center;
            Assert.Equal(expected.X, actual.Center.X, 8);
            Assert.Equal(expected.Y, actual.Center.Y, 8);
        }
    }

    private static EditorSession Create()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 8192, 8192), ViewportTransform.Identity);
        return editor;
    }
}
