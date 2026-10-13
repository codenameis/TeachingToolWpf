using TeachingToolWpf.Presentation;
using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class RoiPropertiesTests
{
    [Fact]
    public void RepeatedCellGeometryUsesTheCommonPropertiesAndHistory()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("image", "image", 8192, 8192), VisionGeometry.ViewportTransform.Identity);
        var id = editor.AddRepeatedGrid(new(10001, 10001, .25, new(4096, 4096)));
        var cell = new RepeatedRoiCell(id, 50010000);
        editor.SelectRepeated(cell);
        var original = editor.State.RepeatedRois[id].Geometry(cell.Index);
        var properties = new RoiPropertiesViewModel(editor);
        properties.Refresh();
        Assert.True(properties.HasSingleSelection);
        Assert.False(properties.CanEditMetadata);
        Assert.False(properties.CanChangeType);
        properties.X = "4010";
        properties.Y = "4020";
        properties.Width = "40";
        properties.Height = "20";
        properties.Angle = "35";
        properties.Apply();
        Assert.Equal(new VisionGeometry.RoiGeometry(new(4010, 4020), 40, 20, 35), editor.State.RepeatedRois[id].Geometry(cell.Index));
        editor.Undo();
        Assert.Equal(original, editor.State.RepeatedRois[id].Geometry(cell.Index));
        properties.X = "invalid";
        Assert.Throws<ArgumentException>(properties.Apply);
        Assert.True(editor.CanRedo);
        editor.SelectAll();
        properties.Refresh();
        Assert.False(properties.HasSingleSelection);
    }

    [Fact]
    public void NumericDraftCommitsOnceAndInvalidInputPreservesDocumentAndRedo()
    {
        var editor = new EditorSession();
        var id = editor.Add(new(new(200, 200), 40, 40, 0));
        var properties = new RoiPropertiesViewModel(editor);
        properties.Refresh();
        properties.SelectedType = 1;
        properties.SearchHorizontal = "0";
        properties.SearchHorizontal = "0.";
        properties.SearchHorizontal = "0.72";
        properties.SearchVertical = "12.5";
        properties.IsNameVisible = false;
        Assert.Null(editor.State.Rois[id].SearchRange);
        properties.Apply();
        Assert.Equal(new RoiSearchRange(.72, 12.5), editor.State.Rois[id].SearchRange);
        Assert.False(editor.State.Rois[id].IsNameVisible);
        editor.Undo();
        var before = editor.State;
        properties.SearchVertical = "invalid";
        Assert.Throws<ArgumentException>(properties.Apply);
        Assert.Same(before, editor.State);
        Assert.True(editor.CanRedo);
        editor.Redo();
        properties.Refresh();
        Assert.Equal(1, properties.SelectedType);
        Assert.False(properties.IsNameVisible);
    }

    [Fact]
    public void ParentAllowsNameAndVisibilityButNoInspectionType()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(200, 200), 100, 100, 0));
        editor.AddChild(parent, new(new(200, 200), 10, 10, 0));
        editor.Select([parent]);
        var properties = new RoiPropertiesViewModel(editor);
        properties.Refresh();
        Assert.True(properties.IsGrouping);
        Assert.False(properties.CanChangeType);
        properties.Name = "조립 그룹";
        properties.IsNameVisible = false;
        properties.Apply();
        Assert.Equal("조립 그룹", editor.State.Rois[parent].Name);
        Assert.False(editor.State.Rois[parent].IsNameVisible);
        Assert.Null(editor.State.Rois[parent].SearchRange);
        editor.Undo();
        properties.Refresh();
        Assert.True(properties.IsNameVisible);
    }
}
