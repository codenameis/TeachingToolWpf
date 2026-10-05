using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class SelectionFactsTests
{
    [Fact]
    public void NavigationReusesFactsWhileMetadataSelectionAndUndoInvalidateThem()
    {
        var editor = new EditorSession();
        var first = editor.Add(new(new(100, 100), 20, 20, 0));
        var second = editor.Add(new(new(200, 100), 20, 20, 0));
        editor.SetFilters(new(RoiFilterKind.Edge));
        editor.Select([first, second]);
        var before = editor.State;
        var facts = before.SelectionFacts;
        Assert.Equal(first, facts.First!.Id);
        Assert.True(facts.HasMixedFilters);
        Assert.False(facts.HasGroup);
        Assert.Equal(second, Assert.Single(facts.RegisteredFilters).Id);
        editor.SetViewport(editor.State.Viewport.Pan(new(30, 40)));
        Assert.Same(facts, editor.State.SelectionFacts);
        editor.SetFilters(new(RoiFilterKind.None));
        Assert.Empty(editor.State.SelectionFacts.RegisteredFilters);
        Assert.False(editor.State.SelectionFacts.HasMixedFilters);
        Assert.Single(before.SelectionFacts.RegisteredFilters);
        editor.Undo();
        Assert.True(editor.State.SelectionFacts.HasMixedFilters);
        var parent = editor.GroupSelection();
        Assert.True(editor.State.SelectionFacts.HasGroup);
        Assert.Equal(parent, editor.State.SelectionFacts.First!.Id);
        editor.Select([]);
        Assert.Null(editor.State.SelectionFacts.First);
        Assert.False(editor.State.SelectionFacts.HasGroup);
    }
}
