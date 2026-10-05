namespace VisionCanvas.Core.Editing;

public sealed class PreparedEditorHistory
{
    internal EditorSession Owner
    {
        get;
    }

    internal long Revision
    {
        get;
    }

    internal PreparedHistoryStep Step
    {
        get;
    }

    public DocumentState State => Step.Target;

    internal PreparedEditorHistory(EditorSession owner, long revision, PreparedHistoryStep step)
    {
        Owner = owner;
        Revision = revision;
        Step = step;
    }
}

public sealed partial class EditorSession
{
    public Task<PreparedEditorHistory> PrepareHistoryAsync(bool redo, CancellationToken token = default)
    {
        if (IsPreviewing)
        {
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        }

        var step = (redo ? _history.CaptureRedo() : _history.CaptureUndo()) ?? throw new InvalidOperationException("복원할 이력이 없습니다.");
        var before = State;
        var revision = _mutationRevision;
        return EditPreparation.Run(() =>
        {
            var prepared = step.Prepare(before, token);
            Warm(prepared.Target, token);
            return new PreparedEditorHistory(this, revision, prepared);
        }, token);
    }

    public void ValidatePreparedHistory(PreparedEditorHistory prepared)
    {
        if (!ReferenceEquals(prepared.Owner, this) || prepared.Revision != _mutationRevision || IsPreviewing)
        {
            throw new InvalidOperationException("이력 준비 중 문서가 변경되었습니다. 다시 시도하세요.");
        }

        _history.Validate(prepared.Step, State);
    }

    public void CommitPreparedHistory(PreparedEditorHistory prepared)
    {
        ValidatePreparedHistory(prepared);
        Publish(_history.Commit(prepared.Step, State));
    }
}
