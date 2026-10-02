using TeachingToolWpf.Core.Editing;

namespace TeachingToolWpf.Presentation;

public sealed partial class EditorViewModel
{
    private const int SynchronousPreviewRoiLimit = 256;
    private const int SynchronousSelectionDocumentLimit = 4096;
    private (object Version, RoiRows Rows)? preparedRows;
    public Task EditCompletion { get; private set; } = Task.CompletedTask;

    private async Task PrepareRowsAsync(DocumentState state, CancellationToken token)
    {
        if (ReferenceEquals(rowVersion, state.Snapshot.RowVersion))
            return;

        var rows = await EditPreparation.Run(() => new RoiRows(RoiRow.Build(state, token)), token);
        token.ThrowIfCancellationRequested();
        preparedRows = (state.Snapshot.RowVersion, rows);
    }

    private Task PrepareEditAsync(string name, Func<CancellationToken, Task<PreparedEditorChange>> prepare)
    {
        return RunEditAsync(name, async token =>
        {
            var prepared = await prepare(token);
            await PrepareRowsAsync(prepared.State, token);
            token.ThrowIfCancellationRequested();

            // 문서와 목록 준비가 모두 성공한 뒤 UI 스레드에서 유효성을 검사하고 한 번 게시한다.
            Session.CommitPrepared(prepared);
        });
    }

    private async Task RunEditAsync(string name, Func<CancellationToken, Task> action)
    {
        if (editCancellation is not null || disposed)
            return;
        using var cancellation = new CancellationTokenSource();
        editCancellation = cancellation;
        Status = $"{name} 준비 중… 화면 이동·확대 가능 · Esc로 취소";
        Refresh();

        try
        {
            await action(cancellation.Token);
            if (!disposed)
                Status = $"{name} 완료";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || !Session.IsPreviewing)
        {
            if (!disposed)
                Status = $"{name} 취소";
        }
        finally
        {
            preparedRows = null;
            editCancellation = null;
            if (!disposed)
            {
                if (Session.IsPreviewing)
                    Session.CancelPreview();
                previewPreparation.Cancel();
                Refresh();
            }
        }
    }

    private void CompletePreview(string name)
    {
        if (previewPreparation.HasPending)
        {
            EditCompletion = ObserveEditAsync(PrepareEditAsync(name, token => PreparePendingPreviewAsync(name, token)));
            return;
        }

        var changedCount = Session.TransformPreview?.Selection.Affected.Count ?? Session.State.Selection.Count;
        if (changedCount <= SynchronousPreviewRoiLimit || !Session.HasPreviewChanges)
        {
            Session.CommitPreview(name);
            return;
        }

        EditCompletion = ObserveEditAsync(PrepareEditAsync(name, token => Session.PreparePreviewAsync(name, token)));
    }

    private async Task<PreparedEditorChange> PreparePendingPreviewAsync(string name, CancellationToken token)
    {
        // Mouse Up이 먼저 와도 마지막 포인터 요청이 적용된 결과를 확정한다.
        await previewPreparation.FlushAsync(token);
        return await Session.PreparePreviewAsync(name, token);
    }

    private void CompleteSelection(IEnumerable<Guid> ids)
    {
        if (Session.State.Rois.Count <= SynchronousSelectionDocumentLimit)
        {
            Session.Select(ids);
            return;
        }

        EditCompletion = ObserveEditAsync(PrepareEditAsync("영역 선택", token => Session.PrepareSelectionAsync(ids, token)));
    }

    // UI 이벤트의 오류를 알리면서도 진단·종료 코드가 완료를 기다릴 수 있도록 Task를 보관한다.
    private async Task ObserveEditAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            if (!disposed)
                Fail(ex);
        }
    }
}
