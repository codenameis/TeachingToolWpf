using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

public sealed record RoiChange(Guid Id, Roi? Before, Roi? After);

public sealed class DocumentEdit
{
    public string Name
    {
        get;
    }

    public ImmutableArray<RoiChange> Changes
    {
        get;
    }

    private readonly ImmutableHashSet<Guid> _beforeSelection;
    private readonly ImmutableHashSet<Guid> _afterSelection;
    private readonly ImageReference? _beforeImage;
    private readonly ImageReference? _afterImage;
    private readonly ViewportTransform _beforeView;
    private readonly ViewportTransform _afterView;
    private readonly bool _changesImage;
    private readonly bool _refitSpatial;

    internal bool ChangesImage => _changesImage;

    public bool IsEmpty => Changes.IsEmpty && !_changesImage;

    public long EstimatedBytes
    {
        get;
    }

    public DocumentEdit(string name, DocumentState before, DocumentState after, bool replacesImage = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Name = name;
        _refitSpatial = after.Snapshot.IsRigidUpdate && ReferenceEquals(after.Snapshot.PreviousRois, before.Rois);
        IEnumerable<Guid> candidates = ReferenceEquals(before.Rois, after.Rois) ? [] :
            ReferenceEquals(before.Rois, after.Snapshot.PreviousRois) ? after.Snapshot.ChangedIds : before.Rois.Keys.Union(after.Rois.Keys);
        var changes = ImmutableArray.CreateBuilder<RoiChange>();
        var estimatedBytes = 256L;
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = before.Rois.GetValueOrDefault(id);
            var next = after.Rois.GetValueOrDefault(id);
            if (old == next)
            {
                continue;
            }

            changes.Add(new(id, old, next));
            estimatedBytes += 320L + (((old?.Name.Length ?? 0) + (next?.Name.Length ?? 0)) * 2);
            estimatedBytes += ((old?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? old.Geometry.Shape.Vertices.Length : 0)
                + (next?.Geometry.Shape.Kind == RoiShapeKind.Polygon ? next.Geometry.Shape.Vertices.Length : 0)) * 16L;
        }
        Changes = changes.ToImmutable();
        _beforeSelection = before.Selection;
        _afterSelection = after.Selection;
        _beforeImage = before.Image;
        _afterImage = after.Image;
        _beforeView = before.Viewport;
        _afterView = after.Viewport;
        _changesImage = replacesImage || before.Image != after.Image;
        EstimatedBytes = estimatedBytes + ((_beforeSelection.Count + _afterSelection.Count) * 32L);
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
            if (value is null)
            {
                rois.Remove(change.Id);
            }
            else
            {
                rois[change.Id] = value;
            }
        }
        var ids = Changes.Select(c => c.Id);
        var geometry = _refitSpatial ? state.WithRigidRois(rois.ToImmutable(), ids) : state.WithRois(rois.ToImmutable(), ids);
        var result = geometry with
        {
            Selection = forward ? _afterSelection : _beforeSelection,
            Image = _changesImage ? (forward ? _afterImage : _beforeImage) : state.Image,
            Viewport = _changesImage ? (forward ? _afterView : _beforeView) : state.Viewport
        };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>Immutable history instruction captured on the owner thread; preparation touches no mutable history.</summary>
public sealed class HistoryStep
{
    internal object HistoryIdentity
    {
        get;
    }

    internal long Revision
    {
        get;
    }

    internal int Cursor
    {
        get;
    }

    internal DocumentEdit Edit
    {
        get;
    }

    internal bool Forward
    {
        get;
    }

    public string Name => Edit.Name;

    internal HistoryStep(object historyIdentity, long revision, int cursor, DocumentEdit edit, bool forward)
    {
        HistoryIdentity = historyIdentity;
        Revision = revision;
        Cursor = cursor;
        Edit = edit;
        Forward = forward;
    }

    public PreparedHistoryStep Prepare(DocumentState source, CancellationToken cancellationToken = default)
        => new(this, source, Edit.Apply(source, Forward, cancellationToken));
}

/// <summary>Immutable result awaiting owner-thread validation; image and GPU storage are never copied.</summary>
public sealed class PreparedHistoryStep
{
    internal HistoryStep Step
    {
        get;
    }

    internal DocumentState Source
    {
        get;
    }

    public DocumentState Target
    {
        get;
    }

    internal PreparedHistoryStep(HistoryStep step, DocumentState source, DocumentState target)
    {
        Step = step;
        Source = source;
        Target = target;
    }
}

public sealed class EditHistory(int maxCount = 200, long byteBudget = 1024L * 1024 * 1024)
{
    private readonly List<DocumentEdit> _edits = [];
    private readonly object _identity = new();
    private int _cursor;
    private long _revision;
    private PreparedHistoryStep? _prepared;

    public bool CanUndo => _cursor > 0;

    public bool CanRedo => _cursor < _edits.Count;

    public string? UndoName => CanUndo ? _edits[_cursor - 1].Name : null;

    public string? RedoName => CanRedo ? _edits[_cursor].Name : null;

    public long Bytes => _edits.Sum(e => e.EstimatedBytes);

    public void Push(DocumentEdit edit)
    {
        if (edit.IsEmpty)
        {
            return;
        }

        if (edit.EstimatedBytes > byteBudget)
        {
            throw new InvalidOperationException("한 편집이 이력 메모리 예산을 초과합니다.");
        }

        if (_cursor < _edits.Count)
        {
            _edits.RemoveRange(_cursor, _edits.Count - _cursor);
        }

        _edits.Add(edit);
        _cursor++;
        while (_edits.Count > maxCount || Bytes > byteBudget)
        {
            _edits.RemoveAt(0);
            _cursor--;
        }
        _revision++;
        _prepared = null;
    }

    public HistoryStep? CaptureUndo() => CanUndo ? new(_identity, _revision, _cursor, _edits[_cursor - 1], false) : null;

    public HistoryStep? CaptureRedo() => CanRedo ? new(_identity, _revision, _cursor, _edits[_cursor], true) : null;

    public void Validate(PreparedHistoryStep target, DocumentState current)
    {
        var step = target.Step;
        if (!Matches(step) || !ReferenceEquals(target.Source.Rois, current.Rois) ||
            !ReferenceEquals(target.Source.Selection, current.Selection) || target.Source.Image != current.Image)
        {
            throw new InvalidOperationException("이력 준비 이후 문서 또는 이력이 변경되었습니다. 다시 시도하세요.");
        }
    }

    public DocumentState Commit(PreparedHistoryStep target, DocumentState current)
    {
        Validate(target, current);
        var step = target.Step;
        var result = !step.Edit.ChangesImage && target.Target.Viewport != current.Viewport
            ? target.Target with
            {
                Viewport = current.Viewport
            } : target.Target;
        _cursor += step.Forward ? 1 : -1;
        _revision++;
        _prepared = null;
        return result;
    }

    private bool Matches(HistoryStep step)
    {
        var editIndex = step.Forward ? _cursor : _cursor - 1;
        return ReferenceEquals(step.HistoryIdentity, _identity) && step.Revision == _revision && step.Cursor == _cursor &&
            editIndex >= 0 && editIndex < _edits.Count && ReferenceEquals(step.Edit, _edits[editIndex]);
    }

    private PreparedHistoryStep Prepare(DocumentState state, bool forward)
    {
        if (_prepared is { } existing && ReferenceEquals(existing.Source, state) && existing.Step.Forward == forward && Matches(existing.Step))
        {
            return existing;
        }

        var step = forward ? CaptureRedo() : CaptureUndo();
        return _prepared = step!.Prepare(state);
    }

    public DocumentState PreviewUndo(DocumentState state) => CanUndo ? Prepare(state, false).Target : state;

    public DocumentState PreviewRedo(DocumentState state) => CanRedo ? Prepare(state, true).Target : state;

    public DocumentState Undo(DocumentState state) => CanUndo ? Commit(Prepare(state, false), state) : state;

    public DocumentState Redo(DocumentState state) => CanRedo ? Commit(Prepare(state, true), state) : state;
}
