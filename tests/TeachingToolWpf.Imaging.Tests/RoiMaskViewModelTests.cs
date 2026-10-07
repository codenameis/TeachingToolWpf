using TeachingToolWpf.Presentation;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class RoiMaskViewModelTests
{
    [Fact]
    public void ApplyingDisplayedValuesPreservesExactMaskAndOnlyChangesEditedAxis()
    {
        var session = new EditorSession();
        var input = new InteractionController(session);
        var model = new RoiMaskViewModel(session, input, _ => { });
        var id = session.Add(new(new(200, 200), 100, 100, 0));
        var mask = new MaskRegion(MaskShape.Rectangle, new(.123456789, .234567891, .678912345, .789123456));
        session.AddMask(id, mask);
        model.Update(true);
        var bytes = session.HistoryBytes;
        model.Apply.Execute(null);
        Assert.Equal(mask, session.State.Rois[id].Masks[0]);
        Assert.Equal(bytes, session.HistoryBytes);

        model.X = "20";
        model.Apply.Execute(null);
        var bounds = session.State.Rois[id].Masks[0].Bounds;
        Assert.Equal(.2, bounds.Left);
        Assert.Equal(mask.Bounds.Width, bounds.Width, 14);
        Assert.Equal(mask.Bounds.Top, bounds.Top);
        Assert.Equal(mask.Bounds.Bottom, bounds.Bottom);
    }

    [Fact]
    public void CommandsDraftValidationAndVisibilityKeepSeparateContracts()
    {
        var session = new EditorSession();
        var input = new InteractionController(session);
        var model = new RoiMaskViewModel(session, input, _ => { });
        model.Update(true);
        Assert.False(model.DrawRectangle.CanExecute(null));
        var id = session.Add(new(new(200, 200), 100, 100, 0));
        model.Update(true);
        model.DrawEllipse.Execute(null);
        Assert.Equal(EditorTool.Mask, input.Tool);
        session.AddMask(id, new(MaskShape.Ellipse, new(.1, .2, .6, .7)));
        model.Update(true);
        var bytes = session.HistoryBytes;
        model.ShowMasks = false;
        model.Update(true);
        Assert.False(model.ShowMasks);
        Assert.Equal(bytes, session.HistoryBytes);
        model.X = "NaN";
        model.Apply.Execute(null);
        Assert.NotEmpty(model.Error);
        Assert.Equal(bytes, session.HistoryBytes);
        model.X = "20";
        model.Apply.Execute(null);
        Assert.Empty(model.Error);
        Assert.Equal(.2, session.State.Rois[id].Masks[0].Bounds.Left);
        session.Undo();
        Assert.Equal(.1, session.State.Rois[id].Masks[0].Bounds.Left);
        model.Update(false);
        Assert.False(model.Apply.CanExecute(null));
    }
}
