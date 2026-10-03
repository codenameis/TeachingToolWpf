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
    private readonly bool refitSpatial;
    internal bool ChangesImage => changesImage;
    public bool IsEmpty => Changes.IsEmpty && !changesImage;
    public long EstimatedBytes { get; }

    public DocumentEdit(string name, DocumentState before, DocumentState after, bool replacesImage = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Name = name;
        refitSpatial = after.Snapshot.IsRigidUpdate && ReferenceEquals(after.Snapshot.PreviousRois, before.Rois);
        IEnumerable<Guid> candidates = ReferenceEquals(before.Rois, after.Rois) ? [] :
            ReferenceEquals(before.Rois, after.Snapshot.PreviousRois) ? after.Snapshot.ChangedIds : before.Rois.Keys.Union(after.Rois.Keys);
        var changes = ImmutableArray.CreateBuilder<RoiChange>();
        var estimatedBytes = 256L;
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = before.Rois.GetValueOrDefault(id); var next = after.Rois.GetValueOrDefault(id);
            if (old == next) continue;
            changes.Add(new(id, old, next));
            estimatedBytes += 320L + ((old?.Name.Length ?? 0) + (next?.Name.Length ?? 0)) * 2;
            estimatedBytes += ((old?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? old.Geometry.Shape.Vertices.Length : 0)
                + (next?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? next.Geometry.Shape.Vertices.Length : 0)) * 16L;
        }
        Changes = changes.ToImmutable();
        beforeSelection = before.Selection;
        afterSelection = after.Selection;
        beforeImage = before.Image;
        afterImage = after.Image;
        beforeView = before.Viewport;
        afterView = after.Viewport;
        changesImage = replacesImage || before.Image != after.Image;
        EstimatedBytes = estimatedBytes + (beforeSelection.Count + afterSelection.Count) * 32L;
        cancellationToken.ThrowIfCancellationRequested();
    }

    public DocumentState Apply(DocumentState state, bool forward, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rois = state.Rois.ToBuilder();
        foreach (var change in Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = forward ? change.After : change.Before;
            if (value is null) rois.Remove(change.Id);
            else rois[change.Id] = value;
        }
        var ids = Changes.Select(c => c.Id);
        var geometry = refitSpatial ? state.WithRigidRois(rois.ToImmutable(), ids) : state.WithRois(rois.ToImmutable(), ids);
        var result = geometry with
        {
            Selection = forward ? afterSelection : beforeSelection,
            Image = changesImage ? (forward ? afterImage : beforeImage) : state.Image,
            Viewport = changesImage ? (forward ? afterView : beforeView) : state.Viewport
        };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>Immutable history instruction captured on the owner thread; preparation touches no mutable history.</summary>
public sealed class HistoryStep
{
    internal object HistoryIdentity { get; }
    internal long Revision { get; }
    internal int Cursor { get; }
    internal DocumentEdit Edit { get; }
    internal bool Forward { get; }
    public string Name => Edit.Name;
    internal HistoryStep(object historyIdentity, long revision, int cursor, DocumentEdit edit, bool forward)
    { HistoryIdentity = historyIdentity; Revision = revision; Cursor = cursor; Edit = edit; Forward = forward; }
    public PreparedHistoryStep Prepare(DocumentState source, CancellationToken cancellationToken = default)
        => new(this, source, Edit.Apply(source, Forward, cancellationToken));
}

/// <summary>Immutable result awaiting owner-thread validation; image and GPU storage are never copied.</summary>
public sealed class PreparedHistoryStep
{
    internal HistoryStep Step { get; }
    internal DocumentState Source { get; }
    public DocumentState Target { get; }
    internal PreparedHistoryStep(HistoryStep step, DocumentState source, DocumentState target)
    { Step = step; Source = source; Target = target; }
}

public sealed class EditHistory(int maxCount = 200, long byteBudget = 1024L * 1024 * 1024)
{
    private readonly List<DocumentEdit> edits = [];
    private readonly object identity = new();
    private int cursor;
    private long revision;
    private PreparedHistoryStep? prepared;
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
        revision++; prepared = null;
    }
    public HistoryStep? CaptureUndo() => CanUndo ? new(identity, revision, cursor, edits[cursor - 1], false) : null;
    public HistoryStep? CaptureRedo() => CanRedo ? new(identity, revision, cursor, edits[cursor], true) : null;
    public void Validate(PreparedHistoryStep target, DocumentState current)
    {
        var step = target.Step;
        if (!Matches(step) || !ReferenceEquals(target.Source.Rois, current.Rois) ||
            !ReferenceEquals(target.Source.Selection, current.Selection) || target.Source.Image != current.Image)
            throw new InvalidOperationException("이력 준비 이후 문서 또는 이력이 변경되었습니다. 다시 시도하세요.");
    }
    public DocumentState Commit(PreparedHistoryStep target, DocumentState current)
    {
        Validate(target, current);
        var step = target.Step;
        var result = !step.Edit.ChangesImage && target.Target.Viewport != current.Viewport
            ? target.Target with { Viewport = current.Viewport } : target.Target;
        cursor += step.Forward ? 1 : -1;
        revision++; prepared = null;
        return result;
    }
    private bool Matches(HistoryStep step)
    {
        var editIndex = step.Forward ? cursor : cursor - 1;
        return ReferenceEquals(step.HistoryIdentity, identity) && step.Revision == revision && step.Cursor == cursor &&
            editIndex >= 0 && editIndex < edits.Count && ReferenceEquals(step.Edit, edits[editIndex]);
    }
    private PreparedHistoryStep Prepare(DocumentState state, bool forward)
    {
        if (prepared is { } existing && ReferenceEquals(existing.Source, state) && existing.Step.Forward == forward && Matches(existing.Step)) return existing;
        var step = forward ? CaptureRedo() : CaptureUndo();
        return prepared = step!.Prepare(state);
    }
    public DocumentState PreviewUndo(DocumentState state) => CanUndo ? Prepare(state, false).Target : state;
    public DocumentState PreviewRedo(DocumentState state) => CanRedo ? Prepare(state, true).Target : state;
    public DocumentState Undo(DocumentState state) => CanUndo ? Commit(Prepare(state, false), state) : state;
    public DocumentState Redo(DocumentState state) => CanRedo ? Commit(Prepare(state, true), state) : state;
}
