namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public Task<PreparedEditorHistory> PrepareHistoryAsync(bool redo, CancellationToken token = default)
    {
        ThrowIfPreviewing();

        var step = (redo ? _history.CaptureRedo() : _history.CaptureUndo()) ?? throw new InvalidOperationException("복원할 이력이 없습니다.");
        var before = State;
        var revision = _mutationRevision;
        return EditPreparation.Run(() =>
        {
            var prepared = step.Prepare(before, token);
            prepared.Target.Warm(token);
            return new PreparedEditorHistory(this, revision, prepared);
        }, token);
    }

    public void ValidatePreparedHistory(PreparedEditorHistory prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
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
