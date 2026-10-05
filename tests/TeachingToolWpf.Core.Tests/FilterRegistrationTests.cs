using VisionCanvas.Core.Editing;

namespace TeachingToolWpf.Core.Tests;

public class FilterRegistrationTests
{
    [Fact]
    public void RegistrationIsPerRoiAndSurvivesDeleteUndoAndImageUndo()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample", "sample", 1000, 1000), new(1, new()));
        var first = editor.Add(new(new(100, 100), 40, 40, 0));
        var second = editor.Add(new(new(200, 200), 40, 40, 0));
        editor.SetFilters(new(RoiFilterKind.Edge, EdgeLow: 20, EdgeHigh: 80));
        Assert.Equal(RoiFilterKind.None, editor.State.Rois[first].Filter.Kind);
        editor.Undo();
        Assert.Equal(RoiFilterKind.None, editor.State.Rois[second].Filter.Kind);
        editor.Redo();
        Assert.Equal(20, editor.State.Rois[second].Filter.EdgeLow);
        editor.DeleteSelection();
        editor.Undo();
        Assert.Equal(RoiFilterKind.Edge, editor.State.Rois[second].Filter.Kind);
        editor.SetImage(new("other", "other", 2000, 2000), new(1, new()));
        editor.Undo();
        Assert.Equal(80, editor.State.Rois[second].Filter.EdgeHigh);
    }

    [Fact]
    public void InvalidOrUnchangedRegistrationDoesNotChangeHistory()
    {
        var editor = new EditorSession();
        editor.Add(new(new(100, 100), 40, 40, 0));
        editor.SetFilters(new(RoiFilterKind.Binarize));
        var before = editor.State;
        var bytes = editor.HistoryBytes;
        Assert.Throws<ArgumentException>(() => editor.SetFilters(new(RoiFilterKind.Edge, EdgeLow: 200, EdgeHigh: 20)));
        editor.SetFilters(new(RoiFilterKind.Binarize));
        Assert.Equal(bytes, editor.HistoryBytes);
        Assert.Equal(before, editor.State);
    }
}
