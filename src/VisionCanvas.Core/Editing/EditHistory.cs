namespace VisionCanvas.Core.Editing;

/// <summary>확정한 편집의 순서, 현재 위치와 메모리 예산을 관리한다.</summary>
public sealed class EditHistory(int maxCount = 200, long byteBudget = 1024L * 1024 * 1024)
{
    private readonly List<DocumentEdit> _edits = [];
    private readonly object _identity = new();
    // cursor 앞은 적용된 편집, cursor부터는 Redo로 다시 적용할 편집이다.
    private int _cursor;
    private long _revision;
    // 최근 시작/목표 상태 한 쌍만 미리보기 준비와 공유한다. 편집마다 캐시를 쌓지 않는다.
    // Bytes는 편집 차이의 추정 예산이며, 공유 색인을 포함한 프로세스 전체 메모리 상한이 아니다.
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

        ValidateBudget(edit);

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

    internal void ValidateBudget(DocumentEdit edit)
    {
        if (edit.EstimatedBytes > byteBudget)
        {
            throw new InvalidOperationException("한 편집이 이력 메모리 예산을 초과합니다.");
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
        if (!Matches(step) || !target.Source.SameContent(current) ||
            !target.Source.SameSelection(current) || target.Source.Image != current.Image)
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
