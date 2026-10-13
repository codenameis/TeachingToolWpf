using VisionCanvas.Core.Editing;
using VisionGeometry;

namespace TeachingToolWpf.Presentation;

public sealed partial class EditorViewModel
{
    private const int SynchronousPreviewRoiLimit = 256;
    private const int SynchronousSelectionDocumentLimit = 4096;
    private (object Version, object RepeatedVersion, RoiRows Rows)? _preparedRows;
    private object? _repeatedRowVersion;

    public Task EditCompletion
    {
        get;
        private set;
    } = Task.CompletedTask;

    private async Task PrepareRowsAsync(DocumentState state, CancellationToken token)
    {
        if (ReferenceEquals(_rowVersion, state.Snapshot.RowVersion) && ReferenceEquals(_repeatedRowVersion, state.RepeatedRowVersion))
        {
            return;
        }

        var rows = await EditPreparation.Run(() => new RoiRows(RoiRow.Build(state, token)), token);
        token.ThrowIfCancellationRequested();
        _preparedRows = (state.Snapshot.RowVersion, state.RepeatedRowVersion, rows);
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
        if (_editCancellation is not null || _disposed)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _editCancellation = cancellation;
        var wasPreviewing = Session.IsPreviewing;

        try
        {
            Status = $"{name} 준비 중… 화면 이동·확대 가능 · Esc로 취소";
            Refresh();
            cancellation.Token.ThrowIfCancellationRequested();
            await action(cancellation.Token);
            if (!_disposed)
            {
                Status = $"{name} 완료";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || (wasPreviewing && !Session.IsPreviewing))
        {
            if (!_disposed)
            {
                Status = $"{name} 취소";
            }
        }
        finally
        {
            _preparedRows = null;
            _editCancellation = null;
            if (!_disposed)
            {
                if (Session.IsPreviewing)
                {
                    Session.CancelPreview();
                }

                _previewPreparation.Cancel();
                Refresh();
            }
        }
    }

    private void CompletePreview(string name)
    {
        if (_previewPreparation.HasPending)
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
        await _previewPreparation.FlushAsync(token);
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

    private void CompleteRegionSelection(BoundsD bounds, bool additive)
    {
        if (Session.State.RoiCount <= SynchronousSelectionDocumentLimit)
        {
            Session.SelectRegion(bounds, additive);
        }
        else
        {
            EditCompletion = ObserveEditAsync(PrepareEditAsync("영역 선택", token => Session.PrepareRegionSelectionAsync(bounds, additive, token)));
        }
    }

    public RelayCommand AddRepeatedTest { get; private set; } = null!;

    private void AddRepeatedTestInDocument()
    {
        var image = Session.State.Image ?? throw new InvalidOperationException("이미지를 먼저 열어 주세요.");
        var pitch = Math.Min(image.Width, image.Height) * .45 / 10001;
        if (pitch < .025)
        {
            throw new InvalidOperationException("1억 ROI 테스트에는 가로·세로 556px 이상의 이미지가 필요합니다.");
        }

        Session.AddRepeatedGrid(new(10001, 10001, pitch, new(image.Width / 2d, image.Height / 2d)));
        SetTool(VisionCanvas.Core.Interaction.EditorTool.Select);
        Status = "현재 이미지 중심에 100,020,001개 추가 · 영역 선택 / Ctrl+C·V / 이동·회전 / Ctrl+Z";
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
            if (!_disposed)
            {
                Fail(ex);
            }
        }
    }
}
