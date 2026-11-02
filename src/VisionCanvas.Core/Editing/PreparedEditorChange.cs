using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>편집 세션이 실행하거나 백그라운드에서 준비하는 단일 문서 작업 계약이다.</summary>
public interface IEditorOperation
{
    string? Name { get; }

    DocumentState? PreviewBaseline => null;

    (long Before, long After)? OrderChange => null;

    bool ReplacesImage => false;

    bool RestoresViewport => false;

    DocumentState ComputeNextState(DocumentState before, CancellationToken token);
}

internal sealed record DelegateEditorOperation(
    string? Name,
    Func<DocumentState, CancellationToken, DocumentState> Compute,
    DocumentState? PreviewBaseline = null,
    (long Before, long After)? OrderChange = null,
    bool ReplacesImage = false,
    bool RestoresViewport = false) : IEditorOperation
{
    public DocumentState ComputeNextState(DocumentState before, CancellationToken token)
        => Compute(before, token);
}

/// <summary>Prepared immutable values; only the originating session may publish them once.</summary>
public sealed class PreparedEditorChange
{
    internal bool RestoresViewport
    {
        get;
    }

    internal EditorSession Owner
    {
        get;
    }

    internal long Revision
    {
        get;
    }

    internal long PreviewEpoch
    {
        get;
    }

    internal DocumentEdit? Edit
    {
        get;
    }

    internal (long Before, long After)? OrderChange
    {
        get;
    }

    internal bool Consumed
    {
        get;
        set;
    }

    public DocumentState State { get; }

    internal DocumentState Before
    {
        get;
    }

    internal PreparedEditorChange(
        EditorSession owner,
        long revision,
        long previewEpoch,
        DocumentState before,
        DocumentState state,
        DocumentEdit? edit,
        (long Before, long After)? orderChange = null,
        bool restoresViewport = false)
    {
        Owner = owner;
        Revision = revision;
        PreviewEpoch = previewEpoch;
        Before = before;
        State = state;
        Edit = edit;
        OrderChange = orderChange;
        RestoresViewport = restoresViewport;
    }
}

public sealed partial class EditorSession
{
    private long _mutationRevision;
    private long _previewEpoch;

    public void Execute(IEditorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_previewStart is not null && operation.PreviewBaseline is null)
        {
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        }

        var before = operation.PreviewBaseline ?? State;
        var next = operation.ComputeNextState(before, CancellationToken.None);
        var orderChange = NormalizeOrderChange(operation.OrderChange, before, next);
        if (operation.Name is { } editName)
        {
            Commit(editName, next, operation.ReplacesImage);
        }
        else
        {
            Publish(next);
        }

        if (orderChange is { } order)
        {
            _nextOrder = order.After;
        }
    }

    public Task<PreparedEditorChange> PrepareAsync(IEditorOperation operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_previewStart is not null && operation.PreviewBaseline is null)
        {
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        }

        var before = operation.PreviewBaseline ?? State;
        var revision = _mutationRevision;
        var epoch = _previewEpoch;
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

    private static (long Before, long After)? NormalizeOrderChange(
        (long Before, long After)? orderChange,
        DocumentState before,
        DocumentState next)
    {
        // Reusing and expanding a parent must not consume a new ROI number.
        if (orderChange is { } order && next.Rois.Count == before.Rois.Count && !next.SameContent(before))
        {
            return (order.Before, order.Before);
        }

        return orderChange;
    }

    public Task<PreparedEditorChange> PrepareGroupAsync(CancellationToken token = default)
    {
        var order = _nextOrder;
        return PrepareChangeAsync(
            "부모 ROI로 그룹화",
            before => RoiGroupEditor.Group(before, order + 1, token),
            token,
            orderChange: (order, order + 1));
    }

    public Task<PreparedEditorChange> PrepareUngroupAsync(CancellationToken token = default)
        => PrepareChangeAsync("그룹 해제", before => RoiGroupEditor.Ungroup(before, token), token);

    public Task<PreparedEditorChange> PrepareDeleteAsync(CancellationToken token = default)
        => PrepareChangeAsync("선택 ROI 삭제", before => DeleteSelectionState(before, token), token);

    public Task<PreparedEditorChange> PrepareSelectAllAsync(CancellationToken token = default)
        => PrepareChangeAsync(null, before => SelectAllState(before, token), token);

    public Task<PreparedEditorChange> PrepareSelectionAsync(IEnumerable<Guid> ids, CancellationToken token = default)
        => PrepareChangeAsync(null, before => SelectState(before, ids, token), token);

    public Task<PreparedEditorChange> PrepareRegionSelectionAsync(BoundsD bounds, bool additive = false, CancellationToken token = default)
        => PrepareChangeAsync(null, before => SelectRegionState(before, bounds, additive, token), token);

    public Task<PreparedEditorChange> PrepareTranslationAsync(PointD delta, CancellationToken token = default)
        => PrepareChangeAsync("ROI 이동", before => TranslateSelectionState(before, delta, token), token);

    public Task<PreparedEditorChange> PrepareRotationAsync(double delta, PointD pivot, CancellationToken token = default)
        => PrepareChangeAsync("ROI 회전", before => RotateSelectionState(before, delta, pivot, token), token);

    public Task<PreparedEditorChange> PreparePreviewAsync(string name, CancellationToken token = default)
    {
        var before = _previewStart ?? throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        var transform = TransformPreview;
        var displayed = State;
        return PrepareChangeAsync(
            name,
            _ => transform is { } move ? move.Selection.ApplyPreview(move.Transform, token) : displayed,
            token,
            before);
    }

    public Task<PreparedEditorChange> PrepareAttachAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken token = default)
    {
        var roots = ids.Distinct().ToArray();
        return PrepareChangeAsync("부모 ROI에 연결", before => RoiGroupEditor.Attach(before, roots, parentId, token), token);
    }

    private Task<PreparedEditorChange> PrepareChangeAsync(
        string? name,
        Func<DocumentState, DocumentState> prepare,
        CancellationToken token,
        DocumentState? preview = null,
        (long Before, long After)? orderChange = null)
        => PrepareAsync(
            new DelegateEditorOperation(name, (before, _) => prepare(before), preview, orderChange),
            token);

    // Prepare the indexes consumed immediately by input, properties and rendering off the UI thread.
    internal static void Warm(DocumentState state, CancellationToken token) => state.Warm(token);

    /// <summary>원본 교체 등 되돌리기 어려운 작업을 시작하기 전에 준비 결과의 유효성을 확인한다.</summary>
    public void ValidatePrepared(PreparedEditorChange prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Owner, this)
            || prepared.Consumed
            || prepared.Revision != _mutationRevision
            || prepared.PreviewEpoch != _previewEpoch
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
        var hadPreview = _previewStart is not null;
        ClearPreviewState();
        var nextViewport = prepared.RestoresViewport ? prepared.State.Viewport : State.Viewport;
        Publish(prepared.State.WithViewport(nextViewport));
        if (hadPreview)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
