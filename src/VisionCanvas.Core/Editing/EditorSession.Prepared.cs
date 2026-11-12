using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public Task<PreparedEditorChange> PrepareAsync(IEditorOperation operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.PreviewBaseline is null)
        {
            ThrowIfPreviewing();
        }

        var before = operation.PreviewBaseline ?? State;
        var revision = _mutationRevision;
        var epoch = _preview.Epoch;
        return EditPreparation.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var next = operation.ComputeNextState(before, token);
            token.ThrowIfCancellationRequested();
            var edit = operation.Name is null
                ? null
                : new DocumentEdit(operation.Name, before, next, replacesImage: operation.ReplacesImage, cancellationToken: token);
            next.Warm(token);
            var orderChange = NormalizeOrderChange(operation.OrderChange, before, next);
            return new PreparedEditorChange(this, revision, epoch, before, next, edit, orderChange, operation.RestoresViewport);
        }, token);
    }

    public Task<PreparedEditorChange> PrepareGroupAsync(CancellationToken token = default)
        => PrepareAsync(new EditorOperations.GroupSelectionOperation(_nextOrder), token);

    public Task<PreparedEditorChange> PrepareUngroupAsync(CancellationToken token = default)
        => PrepareAsync(new EditorOperations.UngroupSelectionOperation(), token);

    public Task<PreparedEditorChange> PrepareDeleteAsync(CancellationToken token = default)
        => PrepareAsync(new EditorOperations.DeleteSelectionOperation(), token);

    public Task<PreparedEditorChange> PrepareSelectAllAsync(CancellationToken token = default)
        => PrepareAsync(new EditorOperations.SelectAllOperation(), token);

    public Task<PreparedEditorChange> PrepareSelectionAsync(IEnumerable<Guid> ids, CancellationToken token = default)
        => PrepareAsync(new EditorOperations.SelectOperation(ids), token);

    public Task<PreparedEditorChange> PrepareRegionSelectionAsync(BoundsD bounds, bool additive = false, CancellationToken token = default)
        => PrepareAsync(new EditorOperations.SelectRegionOperation(bounds, additive), token);

    public Task<PreparedEditorChange> PrepareTranslationAsync(PointD delta, CancellationToken token = default)
        => PrepareAsync(new EditorOperations.TranslateSelectionOperation(delta), token);

    public Task<PreparedEditorChange> PrepareRotationAsync(double delta, PointD pivot, CancellationToken token = default)
        => PrepareAsync(new EditorOperations.RotateSelectionOperation(delta, pivot), token);

    public Task<PreparedEditorChange> PreparePreviewAsync(string name, CancellationToken token = default)
    {
        var before = _preview.RequireStart();
        return PrepareAsync(new EditorOperations.CommitPreviewOperation(name, before, _preview.TransformPreview, State), token);
    }

    public Task<PreparedEditorChange> PrepareAttachAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var roots = ids.Distinct().ToArray();
        return PrepareAsync(new EditorOperations.AttachOperation(roots, parentId), token);
    }

    /// <summary>원본 교체 등 되돌리기 어려운 작업을 시작하기 전에 준비 결과의 유효성을 확인한다.</summary>
    public void ValidatePrepared(PreparedEditorChange prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Owner, this)
            || prepared.Consumed
            || prepared.Revision != _mutationRevision
            || prepared.PreviewEpoch != _preview.Epoch
            || (prepared.OrderChange is { } expectedOrder && expectedOrder.Before != _nextOrder))
        {
            throw new InvalidOperationException("편집 준비 중 문서가 변경되었습니다. 다시 시도하세요.");
        }

        if (prepared.Edit is { } edit)
        {
            _history.ValidateBudget(edit);
        }
    }

    public void CommitPrepared(PreparedEditorChange prepared)
    {
        ValidatePrepared(prepared);

        if (prepared.Edit is { IsEmpty: false } edit)
        {
            _history.Push(edit, prepared.Before, prepared.State);
        }

        if (prepared.OrderChange is { } orderChange)
        {
            _nextOrder = orderChange.After;
        }

        prepared.Consumed = true;
        var hadPreview = _preview.IsActive;
        _preview.Clear();
        var nextViewport = prepared.RestoresViewport ? prepared.State.Viewport : State.Viewport;
        Publish(prepared.State.WithViewport(nextViewport));
        if (hadPreview)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
