using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public sealed class RoiSearchRangeTests
{
    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Triangle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void SearchRangePreservesActualShapeAndFollowsRigidPreview(RoiShapeKind kind)
    {
        var editor = new EditorSession();
        var geometry = RoiGeometry.Create(kind, new(200, 200), 100, 80);
        var id = editor.Add(geometry);
        editor.SetProperties(id, "검색", geometry, new(20, 10), false);
        var range = Assert.Single(editor.State.Snapshot.SearchSpatial.At(new(200, 200))).Geometry;
        Assert.Equal(geometry.Width + 40, range.Width);
        Assert.Equal(geometry.Height + 20, range.Height);
        Assert.Equal(RoiShapeKind.Rectangle, range.Shape.Kind);
        Assert.Equal(geometry, editor.State.Rois[id].Geometry);
        editor.BeginPreview();
        editor.PreviewTranslation(new(30, 15));
        var moving = Assert.Single(editor.TransformPreview!.Selection.MovingSearch.At(new(200, 200)));
        Assert.Equal(geometry.Center + new PointD(30, 15), editor.TransformPreview.Geometry(moving).Center);
        editor.CancelPreview();
        editor.RotateSelection(37, geometry.Center);
        Assert.Equal(37, Assert.Single(editor.State.Snapshot.SearchSpatial.At(geometry.Center)).Geometry.Angle);
        editor.Undo();
        Assert.Equal(range, Assert.Single(editor.State.Snapshot.SearchSpatial.At(geometry.Center)).Geometry);
    }

    [Fact]
    public void PropertyEditIsAtomicAndUndoRedoRestoresTypeNameAndVisibility()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(100, 100), 20, 30, 0));
        var before = editor.State.Rois[id];
        editor.SetProperties(id, "부품 이름", before.Geometry, new(25, 10), false);
        var after = editor.State.Rois[id];
        Assert.Equal(id, Assert.Single(editor.State.Snapshot.SearchSpatial.At(new(130, 100))).Id);
        Assert.Empty(editor.State.Snapshot.Spatial.At(new(130, 100)));
        editor.Undo();
        Assert.Equal(before, editor.State.Rois[id]);
        Assert.Empty(editor.State.Snapshot.SearchSpatial.At(new(130, 100)));
        var bytes = editor.HistoryBytes;
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetProperties(id, "invalid", before.Geometry, new(double.MaxValue, 0), false));
        Assert.Equal(before, editor.State.Rois[id]);
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.True(editor.CanRedo);
        editor.Redo();
        Assert.Equal(after, editor.State.Rois[id]);
        editor.SetProperties(id, after.Name, after.Geometry, null, true);
        Assert.Empty(editor.State.Snapshot.SearchSpatial.At(new(130, 100)));
        editor.Undo();
        Assert.Equal(after, editor.State.Rois[id]);
    }

    [Fact]
    public void AddingAndDeletingChildSuppressesParentSearchWithoutLosingUndoState()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(200, 200), 100, 100, 0));
        editor.SetProperties(id, "부모", editor.State.Rois[id].Geometry, new(20, 20), true);
        Assert.Single(editor.State.Snapshot.SearchSpatial.At(new(200, 200)));
        var child = editor.AddChild(id, new(new(200, 200), 10, 10, 0));
        Assert.True(editor.State.IsGroupRoi(id));
        Assert.Empty(editor.State.Snapshot.SearchSpatial.At(new(200, 200)));
        Assert.Throws<ArgumentException>(() => editor.SetProperties(id, "부모", editor.State.Rois[id].Geometry, new(40, 40), true));
        editor.Select([child]);
        editor.DeleteSelection();
        Assert.False(editor.State.IsGroupRoi(id));
        Assert.Single(editor.State.Snapshot.SearchSpatial.At(new(200, 200)));
        editor.Undo();
        Assert.Empty(editor.State.Snapshot.SearchSpatial.At(new(200, 200)));
        editor.Select([id]);
        editor.DeleteSelection();
        Assert.Empty(editor.State.Rois);
        editor.Undo();
        Assert.Equal(2, editor.State.Rois.Count);
        Assert.True(editor.State.IsGroupRoi(id));
    }

    [Fact]
    public void ClipboardPreservesNumericMarginsAndNameVisibility()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 1000, 1000), ViewportTransform.Identity);
        var id = editor.Add(new(new(200, 200), 100, 80, 23));
        editor.SetProperties(id, "검색 부품", editor.State.Rois[id].Geometry, new(35.5, 12.25), false);
        var clipboard = new RoiClipboard();
        clipboard.Copy(editor.State);
        editor.Paste(clipboard);
        var copied = editor.State.Rois[Assert.Single(editor.State.Selection)];
        Assert.NotEqual(id, copied.Id);
        Assert.Equal(new RoiSearchRange(35.5, 12.25), copied.SearchRange);
        Assert.False(copied.IsNameVisible);
        var range = editor.State.Snapshot.SearchSpatial.At(copied.Geometry.Center).Single(roi => roi.Id == copied.Id);
        Assert.Equal(copied.Geometry.Angle, range.Geometry.Angle);
        editor.Undo();
        Assert.Single(editor.State.Rois);
        editor.Redo();
        Assert.Equal(copied, editor.State.Rois[copied.Id]);
    }

    [Fact]
    public void RenameRetainsSearchGeometryBlocksAndPublishesCurrentMetadata()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(100, 100), 20, 30, 0));
        editor.SetProperties(id, "before", editor.State.Rois[id].Geometry, new(10, 10), true);
        var spatial = editor.State.Snapshot.SearchSpatial;
        var block = Assert.Single(spatial.Blocks(spatial.Bounds));
        editor.Rename(id, "after");
        var updated = editor.State.Snapshot.SearchSpatial;
        Assert.Same(block.Identity, Assert.Single(updated.Blocks(updated.Bounds)).Identity);
        Assert.Equal("after", Assert.Single(updated.At(new(100, 100))).Name);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(1, -2)]
    [InlineData(double.NaN, 2)]
    [InlineData(1, double.PositiveInfinity)]
    public void InvalidMarginsAreRejected(double horizontal, double vertical)
        => Assert.Throws<ArgumentException>(() => new RoiSearchRange(horizontal, vertical));
}
