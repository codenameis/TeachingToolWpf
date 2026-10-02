using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class GroupEligibilityTests
{
    [Fact] public void SelectedAncestorsSuppressDescendantsButUnselectedAncestorsDoNot()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(300, 300), 300, 300, 0));
        var middle = editor.Add(new(new(300, 300), 100, 100, 0));
        var child = editor.Add(new(new(300, 300), 20, 20, 0));
        var sibling = editor.Add(new(new(380, 300), 20, 20, 0));

        editor.Select([]); Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
        editor.Select([child]); Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
        editor.Select([parent, child]); Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
        editor.Select([middle, child]); Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
        editor.Select([child, sibling]); Assert.True(editor.State.SelectionFacts.HasMultipleRoots);
        editor.Select(editor.State.Rois.Keys); Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
    }

    [Fact] public void NavigationReusesEligibilityAndGroupingUndoRedoRestoreIt()
    {
        var editor = new EditorSession();
        editor.Add(new(new(100, 100), 20, 20, 0));
        editor.Add(new(new(200, 100), 20, 20, 0));
        editor.Select(editor.State.Rois.Keys);
        var before = editor.State;
        var facts = before.SelectionFacts;
        Assert.True(facts.HasMultipleRoots);

        editor.SetViewport(ViewportTransform.Identity.Pan(new(30, 40)));
        Assert.Same(facts, editor.State.SelectionFacts);
        Assert.True(editor.State.SelectionFacts.HasMultipleRoots);
        editor.GroupSelection();
        editor.Select(editor.State.Rois.Keys);
        Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
        Assert.True(before.SelectionFacts.HasMultipleRoots);

        editor.Undo(); Assert.True(editor.State.SelectionFacts.HasMultipleRoots);
        editor.Redo(); Assert.False(editor.State.SelectionFacts.HasMultipleRoots);
    }
}
