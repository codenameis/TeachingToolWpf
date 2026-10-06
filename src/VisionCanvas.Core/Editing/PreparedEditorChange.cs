using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>Prepared immutable values; only the originating session may publish them once.</summary>
public sealed class PreparedEditorChange
{
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

    public DocumentState State
    {
        get;
    }

    internal DocumentState Before
    {
        get;
    }

    internal PreparedEditorChange(EditorSession owner, long revision, long previewEpoch, DocumentState before, DocumentState state,
        DocumentEdit? edit, (long Before, long After)? orderChange = null)
    {
        Owner = owner;
        Revision = revision;
        PreviewEpoch = previewEpoch;
        Before = before;
        State = state;
        Edit = edit;
        OrderChange = orderChange;
    }
}

public sealed partial class EditorSession
{
    private long _mutationRevision;

    private long _previewEpoch;

    public Task<PreparedEditorChange> PrepareGroupAsync(CancellationToken token = default)
    {
        var order = _nextOrder;
        return PrepareChangeAsync("부모 ROI로 그룹화", before => RoiGrouping.Group(before, order + 1, token),
            token, orderChange: (order, order + 1));
    }

    public Task<PreparedEditorChange> PrepareUngroupAsync(CancellationToken token = default)
        => PrepareChangeAsync("그룹 해제", before => RoiGrouping.Ungroup(before, token), token);

    public Task<PreparedEditorChange> PrepareDeleteAsync(CancellationToken token = default)
        => PrepareChangeAsync("선택 ROI 삭제", before =>
        {
            var ids = before.Selection.Count == before.Rois.Count ? before.Selection
                : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before), token).ToImmutableHashSet();
            var rois = before.Rois.ToBuilder();
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                rois.Remove(id);
            }
            return before.WithRois(rois.ToImmutable(), ids) with
            {
                Selection = before.Selection.Except(ids)
            };
        }, token);

    public Task<PreparedEditorChange> PrepareSelectAllAsync(CancellationToken token = default)
        => PrepareChangeAsync(null, before =>
        {
            if (before.Selection.Count == before.Rois.Count)
            {
                return before;
            }

            return before with
            {
                Selection = before.Rois.Keys.Select(id =>
                {
                    token.ThrowIfCancellationRequested();
                    return id;
                }).ToImmutableHashSet()
            };
        }, token);

    public Task<PreparedEditorChange> PrepareSelectionAsync(IEnumerable<Guid> ids, CancellationToken token = default)
        => PrepareChangeAsync(null, before =>
        {
            var selection = ids.Where(id =>
            {
                token.ThrowIfCancellationRequested();
                return before.Rois.ContainsKey(id);
            }).ToImmutableHashSet();
            return selection.SetEquals(before.Selection) ? before : before with
            {
                Selection = selection
            };
        }, token);

    public Task<PreparedEditorChange> PrepareTranslationAsync(PointD delta, CancellationToken token = default)
        => PrepareChangeAsync("ROI 이동", before =>
        {
            var selection = before.TransformSelection;
            var transform = new RigidTransform2D(default, 0, selection.Constrain(delta));
            var next = selection.Apply(transform, token);
            return next;
        }, token);

    public Task<PreparedEditorChange> PrepareRotationAsync(double delta, PointD pivot, CancellationToken token = default)
        => PrepareChangeAsync("ROI 회전", before =>
        {
            var selection = before.TransformSelection;
            var transform = new RigidTransform2D(pivot, selection.RotationLimits(pivot).Clamp(delta));
            var next = selection.Apply(transform, token);
            return next;
        }, token);

    public Task<PreparedEditorChange> PreparePreviewAsync(string name, CancellationToken token = default)
    {
        var before = _previewStart ?? throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        var transform = TransformPreview;
        var displayed = State;
        return PrepareChangeAsync(name, _ => transform is { } move ? move.Selection.Apply(move.Transform, token) : displayed,
            token, before);
    }

    private Task<PreparedEditorChange> PrepareChangeAsync(string? name, Func<DocumentState, DocumentState> prepare,
        CancellationToken token, DocumentState? preview = null, (long Before, long After)? orderChange = null)
    {
        if (_previewStart is not null && preview is null)
        {
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        }

        var before = preview ?? State;
        var revision = _mutationRevision;
        var epoch = _previewEpoch;
        return EditPreparation.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var next = prepare(before);
            token.ThrowIfCancellationRequested();
            var edit = name is null ? null : new DocumentEdit(name, before, next, cancellationToken: token);
            Warm(next, token);
            return new PreparedEditorChange(this, revision, epoch, before, next, edit, orderChange);
        }, token);
    }

    // Prepare the indexes consumed immediately by input, properties and rendering off the UI thread.
    internal static void Warm(DocumentState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = state.Snapshot.Spatial;
        token.ThrowIfCancellationRequested();
        _ = state.Snapshot.Children;
        token.ThrowIfCancellationRequested();
        _ = state.SelectionFacts.HasMultipleRoots;
        token.ThrowIfCancellationRequested();
        _ = state.SelectedSpatial;
        if (!state.Selection.IsEmpty)
        {
            var transform = state.TransformSelection;
            token.ThrowIfCancellationRequested();
            _ = transform.Moving;
            token.ThrowIfCancellationRequested();
            _ = transform.Stationary;
            token.ThrowIfCancellationRequested();
            _ = transform.RotationLimits(transform.Bounds.Center);
        }
        token.ThrowIfCancellationRequested();
    }

    public void CommitPrepared(PreparedEditorChange prepared)
    {
        if (!ReferenceEquals(prepared.Owner, this) || prepared.Consumed || prepared.Revision != _mutationRevision || prepared.PreviewEpoch != _previewEpoch ||
            (prepared.OrderChange is { } expectedOrder && expectedOrder.Before != _nextOrder))
        {
            throw new InvalidOperationException("편집 준비 중 문서가 변경되었습니다. 다시 시도하세요.");
        }

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
        _previewStart = null;
        _transformSelection = null;
        TransformPreview = null;
        _previewEpoch++;
        Publish(prepared.State with
        {
            Viewport = State.Viewport
        });
        if (hadPreview)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
