using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Presentation;

public sealed partial class EditorViewModel
{
    private (object Version, RoiRows Rows)? preparedRows;
    public Task EditCompletion { get; private set; } = Task.CompletedTask;

    private async Task PrepareRowsAsync(DocumentState state, CancellationToken token)
    {
        if (ReferenceEquals(rowVersion, state.Snapshot.RowVersion)) return;
        var rows = await EditPreparation.Run(() => new RoiRows(RoiRow.Build(state, token)), token);
        token.ThrowIfCancellationRequested();
        preparedRows = (state.Snapshot.RowVersion, rows);
    }

    private Task PrepareEditAsync(string name, Func<CancellationToken, Task<PreparedEditorChange>> prepare)
        => RunEditAsync(name, async token =>
        {
            var prepared = await prepare(token);
            await PrepareRowsAsync(prepared.State, token);
            token.ThrowIfCancellationRequested();
            Session.CommitPrepared(prepared);
        });

    private async Task RunEditAsync(string name, Func<CancellationToken, Task> action)
    {
        if (editCancellation is not null || disposed) return;
        using var cancellation = new CancellationTokenSource(); editCancellation = cancellation;
        Status = $"{name} 준비 중… 화면 이동·확대 가능 · Esc로 취소"; Refresh();
        try { await action(cancellation.Token); if (!disposed) Status = $"{name} 완료"; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !Session.IsPreviewing) { if (!disposed) Status = $"{name} 취소"; }
        finally
        {
            preparedRows = null; editCancellation = null;
            if (!disposed) { if (Session.IsPreviewing) Session.CancelPreview(); previewPreparation.Cancel(); Refresh(); }
        }
    }

    private void CompletePreview(string name)
    {
        if (previewPreparation.HasPending)
        {
            EditCompletion = ObserveEditAsync(PrepareEditAsync(name, async token =>
            {
                await previewPreparation.FlushAsync(token);
                return await Session.PreparePreviewAsync(name, token);
            }));
            return;
        }
        var changedCount = Session.TransformPreview?.Selection.Affected.Count ?? Session.State.Selection.Count;
        if (changedCount <= 256 || !Session.HasPreviewChanges) { Session.CommitPreview(name); return; }
        EditCompletion = ObserveEditAsync(PrepareEditAsync(name, token => Session.PreparePreviewAsync(name, token)));
    }

    private void CompleteSelection(IEnumerable<Guid> ids)
    {
        if (Session.State.Rois.Count <= 4096) { Session.Select(ids); return; }
        EditCompletion = ObserveEditAsync(PrepareEditAsync("영역 선택", token => Session.PrepareSelectionAsync(ids, token)));
    }

    // Event boundary owns error reporting; the task remains available to diagnostics and shutdown.
    private async Task ObserveEditAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) { if (!disposed) Fail(ex); }
    }
}
