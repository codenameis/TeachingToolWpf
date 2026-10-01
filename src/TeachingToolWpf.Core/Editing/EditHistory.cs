using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

public sealed record RoiChange(Guid Id, Roi? Before, Roi? After);

public sealed class DocumentEdit
{
    public string Name { get; }
    public ImmutableArray<RoiChange> Changes { get; }
    private readonly ImmutableHashSet<Guid> beforeSelection;
    private readonly ImmutableHashSet<Guid> afterSelection;
    private readonly ImageReference? beforeImage;
    private readonly ImageReference? afterImage;
    private readonly ViewportTransform beforeView;
    private readonly ViewportTransform afterView;
    private readonly bool changesImage;
    public bool IsEmpty => Changes.IsEmpty && !changesImage;
    public long EstimatedBytes => 256 + Changes.Sum(c => 320L + ((c.Before?.Name.Length ?? 0) + (c.After?.Name.Length ?? 0)) * 2)
        + (beforeSelection.Count + afterSelection.Count) * 32L;

    public DocumentEdit(string name, DocumentState before, DocumentState after, bool replacesImage = false)
    {
        Name = name;
        Changes = before.Rois.Keys.Union(after.Rois.Keys)
            .Select(id => new RoiChange(id, before.Rois.GetValueOrDefault(id), after.Rois.GetValueOrDefault(id)))
            .Where(c => c.Before != c.After).ToImmutableArray();
        beforeSelection = before.Selection;
        afterSelection = after.Selection;
        beforeImage = before.Image;
        afterImage = after.Image;
        beforeView = before.Viewport;
        afterView = after.Viewport;
        changesImage = replacesImage || before.Image != after.Image;
    }

    public DocumentState Apply(DocumentState state, bool forward)
    {
        var rois = state.Rois.ToBuilder();
        foreach (var change in Changes)
        {
            var value = forward ? change.After : change.Before;
            if (value is null) rois.Remove(change.Id);
            else rois[change.Id] = value;
        }
        return state with
        {
            Rois = rois.ToImmutable(), Selection = forward ? afterSelection : beforeSelection,
            Image = changesImage ? (forward ? afterImage : beforeImage) : state.Image,
            Viewport = changesImage ? (forward ? afterView : beforeView) : state.Viewport
        };
    }
}

public sealed class EditHistory(int maxCount = 200, long byteBudget = 64L * 1024 * 1024)
{
    private readonly List<DocumentEdit> edits = [];
    private int cursor;
    public bool CanUndo => cursor > 0;
    public bool CanRedo => cursor < edits.Count;
    public string? UndoName => CanUndo ? edits[cursor - 1].Name : null;
    public string? RedoName => CanRedo ? edits[cursor].Name : null;
    public long Bytes => edits.Sum(e => e.EstimatedBytes);

    public void Push(DocumentEdit edit)
    {
        if (edit.IsEmpty) return;
        if (edit.EstimatedBytes > byteBudget) throw new InvalidOperationException("한 편집이 이력 메모리 예산을 초과합니다.");
        if (cursor < edits.Count) edits.RemoveRange(cursor, edits.Count - cursor);
        edits.Add(edit);
        cursor++;
        while (edits.Count > maxCount || Bytes > byteBudget) { edits.RemoveAt(0); cursor--; }
    }
    public DocumentState PreviewUndo(DocumentState state) => CanUndo ? edits[cursor - 1].Apply(state, false) : state;
    public DocumentState PreviewRedo(DocumentState state) => CanRedo ? edits[cursor].Apply(state, true) : state;
    public DocumentState Undo(DocumentState state) { var result = PreviewUndo(state); if (CanUndo) cursor--; return result; }
    public DocumentState Redo(DocumentState state) { var result = PreviewRedo(state); if (CanRedo) cursor++; return result; }
}
