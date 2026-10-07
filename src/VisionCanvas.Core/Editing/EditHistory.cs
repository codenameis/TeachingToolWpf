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
            estimatedBytes += 32L + (old?.SearchRange is null ? 0 : 32) + (next?.SearchRange is null ? 0 : 32);
            estimatedBytes += ((old?.Masks.Length ?? 0) + (next?.Masks.Length ?? 0)) * 64L;
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
        var geometry = _refitSpatial ? state.WithRigidRois(rois.ToImmutable(), ids, cancellationToken) : state.WithRois(rois.ToImmutable(), ids);
        var result = geometry with
        {
            Selection = forward ? _afterSelection : _beforeSelection,
            Image = _changesImage ? (forward ? _afterImage : _beforeImage) : state.Image,
            Viewport = _changesImage ? (forward ? _afterView : _beforeView) : state.Viewport
        };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal DocumentState RestoreCachedMetadata(DocumentState target, DocumentState source, bool forward)
    {
        // An opposite-direction target may be a source captured after a later selection change.
        // Restore the edit's recorded metadata exactly as Apply does, while retaining its ROI indexes.
        var selection = forward ? _afterSelection : _beforeSelection;
        if (!ReferenceEquals(target.Selection, selection))
        {
            target = target with
            {
                Selection = selection
            };
        }
        var image = _changesImage ? (forward ? _afterImage : _beforeImage) : source.Image;
        if (target.Image != image)
        {
            target = target with
            {
                Image = image
            };
        }
        var view = _changesImage ? (forward ? _afterView : _beforeView) : source.Viewport;
        return target.Viewport == view ? target : target with
        {
            Viewport = view
        };
    }

    internal bool MatchesRecordedSource(DocumentState source, bool forward, CancellationToken cancellationToken)
    {
        foreach (var change in Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Rois.GetValueOrDefault(change.Id) != (forward ? change.Before : change.After))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Immutable history instruction captured on the owner thread; preparation touches no mutable history.</summary>
public sealed class HistoryStep
{
    private readonly DocumentState? _cachedSource;
    private readonly DocumentState? _cachedTarget;

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

    internal HistoryStep(object historyIdentity, long revision, int cursor, DocumentEdit edit, bool forward,
        DocumentState? cachedSource = null, DocumentState? cachedTarget = null)
    {
        HistoryIdentity = historyIdentity;
        Revision = revision;
        Cursor = cursor;
        Edit = edit;
        Forward = forward;
        _cachedSource = cachedSource;
        _cachedTarget = cachedTarget;
    }

    public PreparedHistoryStep Prepare(DocumentState source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = _cachedSource is { } cachedSource && _cachedTarget is not null &&
            ReferenceEquals(cachedSource.Rois, source.Rois) && ReferenceEquals(cachedSource.Selection, source.Selection) && cachedSource.Image == source.Image;
        var target = cached
            ? Edit.RestoreCachedMetadata(_cachedTarget!, source, Forward)
            : Edit.Apply(source, Forward, cancellationToken);
        // Applying a delta accepts arbitrary source values, but that source is not always its inverse.
        // Only fallback preparation needs this check; committed and already checked pairs stay O(1).
        var canReuseSnapshotPair = cached || Edit.MatchesRecordedSource(source, Forward, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // A prepared result owns only its actual pair, never an older captured pair through its instruction.
        var step = _cachedSource is null ? this : new HistoryStep(HistoryIdentity, Revision, Cursor, Edit, Forward);
        return new(step, source, target, canReuseSnapshotPair);
    }
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

    internal bool CanReuseSnapshotPair
    {
        get;
    }

    internal PreparedHistoryStep(HistoryStep step, DocumentState source, DocumentState target, bool canReuseSnapshotPair)
    {
        Step = step;
        Source = source;
        Target = target;
        CanReuseSnapshotPair = canReuseSnapshotPair;
    }
}

public sealed class EditHistory(int maxCount = 200, long byteBudget = 1024L * 1024 * 1024)
{
    private readonly List<DocumentEdit> _edits = [];
    private readonly object _identity = new();
    private int _cursor;
    private long _revision;
    // One most-recent source/target pair, shared with preview preparation. Bytes remains the delta-history
    // budget; these immutable derived indexes are not a process-wide memory limit or a cache per edit.
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

    internal void Push(DocumentEdit edit, DocumentState before, DocumentState after)
    {
        if (edit.IsEmpty)
        {
            return;
        }
        Push(edit);
        if (CanUndo && ReferenceEquals(_edits[_cursor - 1], edit))
        {
            _prepared = new(new(_identity, _revision, _cursor, edit, false), after, edit.RestoreCachedMetadata(before, after, false), true);
        }
    }

    public HistoryStep? CaptureUndo() => CanUndo ? Capture(false) : null;

    public HistoryStep? CaptureRedo() => CanRedo ? Capture(true) : null;

    private HistoryStep Capture(bool forward)
    {
        var edit = _edits[forward ? _cursor : _cursor - 1];
        if (_prepared is { } cached && ReferenceEquals(cached.Step.Edit, edit))
        {
            var sameDirection = cached.Step.Forward == forward;
            return new(_identity, _revision, _cursor, edit, forward,
                sameDirection ? cached.Source : cached.Target, sameDirection ? cached.Target : cached.Source);
        }
        return new(_identity, _revision, _cursor, edit, forward);
    }

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
        _prepared = target.CanReuseSnapshotPair ? target : null;
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
        var prepared = step!.Prepare(state);
        _prepared = prepared.CanReuseSnapshotPair ? prepared : null;
        return prepared;
    }

    public DocumentState PreviewUndo(DocumentState state) => CanUndo ? Prepare(state, false).Target : state;

    public DocumentState PreviewRedo(DocumentState state) => CanRedo ? Prepare(state, true).Target : state;

    public DocumentState Undo(DocumentState state) => CanUndo ? Commit(Prepare(state, false), state) : state;

    public DocumentState Redo(DocumentState state) => CanRedo ? Commit(Prepare(state, true), state) : state;
}
