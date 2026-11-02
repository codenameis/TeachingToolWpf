using System.Windows.Input;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation;
using VisionGeometry;

namespace VisionCanvas.Tests;

public sealed class ViewportNavigationTests
{
    [Fact]
    public void ListFocusKeepsSelectionAndResolvesCurrentGeometryAndStaleTargets()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 2000, 2000), ViewportTransform.Identity);
        var target = editor.Add(new(new(1000, 1000), 100, 80, 25));
        var selected = editor.Add(new(new(200, 200), 40, 40, 0));
        var input = new InteractionController(editor);
        var navigation = new ViewportNavigationViewModel(editor, input, new(800, 600), () => true);
        navigation.RefreshTargets();
        var rows = navigation.Targets;
        var before = editor.State;
        var history = editor.HistoryBytes;
        navigation.Target = rows.Single(item => item.Id == target);
        Assert.Equal(selected, editor.State.Selection.Single());
        Assert.Equal(history, editor.HistoryBytes);
        Assert.True(editor.State.Viewport.Scale > before.Viewport.Scale);
        navigation.FitCommand.Execute(null);
        Assert.Equal(ViewportTransform.Fit(2000, 2000, 800, 600), editor.State.Viewport);
        editor.SetGeometry(target, new(new(1400, 1200), 80, 50, 0));
        navigation.RefreshTargets();
        Assert.Same(rows, navigation.Targets);
        navigation.FocusCommand.Execute(null);
        Assert.True((editor.State.Viewport.ImageToScreen(new(1400, 1200)) - new PointD(400, 300)).Length < 1e-9);
        navigation.IsLocked = true;
        before = editor.State;
        navigation.FitCommand.Execute(null);
        navigation.Target = rows.Single(item => item.Id == selected);
        Assert.Same(before, editor.State);
        Assert.False(ViewportKeyboard.Nudge(input, Key.Right, false, false));
        navigation.IsLocked = false;
        Assert.True(ViewportKeyboard.Nudge(input, Key.Down, false, true));
        Assert.Equal(new PointD(200, 210), editor.State.Rois[selected].Geometry.Center);
        editor.Select([target]);
        editor.DeleteSelection();
        navigation.Refresh();
        Assert.Null(navigation.Target);
        Assert.False(navigation.FocusCommand.CanExecute(null));
    }

    [Fact]
    public void BatchAddedRoisCanBeFocusedAndNudgedIndividually()
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("test", "test", 8192, 8192), ViewportTransform.Identity);
        var ids = editor.AddRange([new RoiGeometry(new(4096, 4096), 10, 10, 0), new RoiGeometry(new(4120, 4096), 10, 10, 0)], selectAdded: true);
        var input = new InteractionController(editor);
        var navigation = new ViewportNavigationViewModel(editor, input, new(800, 600), () => true);
        navigation.RefreshTargets();
        Assert.Equal(2, navigation.Targets.Count);
        navigation.Target = navigation.Targets[0];
        editor.Select([ids[0]]);
        var geometry = editor.State.Rois[ids[0]].Geometry;
        Assert.True(ViewportKeyboard.Nudge(input, Key.Right, false, true));
        Assert.Equal(geometry.Center + new PointD(10, 0), editor.State.Rois[ids[0]].Geometry.Center);
        editor.Undo();
        Assert.Equal(geometry, editor.State.Rois[ids[0]].Geometry);
        editor.Redo();
        Assert.Equal(2, editor.State.RoiCount);
        Assert.Equal(1, editor.State.SelectionCount);
    }
}
