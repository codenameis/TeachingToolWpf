using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Prepared immutable values; only the originating session may publish them once.</summary>
public sealed class PreparedEditorChange
{
    internal EditorSession Owner { get; }
    internal long Revision { get; }
    internal long PreviewEpoch { get; }
    internal DocumentEdit? Edit { get; }
    internal bool Consumed { get; set; }
    public DocumentState State { get; }
    internal PreparedEditorChange(EditorSession owner, long revision, long previewEpoch, DocumentState state, DocumentEdit? edit)
    { Owner = owner; Revision = revision; PreviewEpoch = previewEpoch; State = state; Edit = edit; }
}

public sealed partial class EditorSession
{
    private long mutationRevision, previewEpoch;

    public Task<PreparedEditorChange> PrepareDeleteAsync(CancellationToken token = default)
        => PrepareChangeAsync("선택 ROI 삭제", before =>
        {
            var ids = before.Selection.Count == before.Rois.Count ? before.Selection
                : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before), token).ToImmutableHashSet();
            var rois = before.Rois.ToBuilder();
            foreach (var id in ids) { token.ThrowIfCancellationRequested(); rois.Remove(id); }
            return before.WithRois(rois.ToImmutable(), ids) with { Selection = before.Selection.Except(ids) };
        }, false, token);

    public Task<PreparedEditorChange> PrepareSelectAllAsync(CancellationToken token = default)
        => PrepareChangeAsync(null, before => before.Selection.Count == before.Rois.Count ? before
            : before with { Selection = before.Rois.Keys.Select(id => { token.ThrowIfCancellationRequested(); return id; }).ToImmutableHashSet() }, false, token);

    public Task<PreparedEditorChange> PrepareSelectionAsync(IEnumerable<Guid> ids, CancellationToken token = default)
        => PrepareChangeAsync(null, before =>
        {
            var selection = ids.Where(id => { token.ThrowIfCancellationRequested(); return before.Rois.ContainsKey(id); }).ToImmutableHashSet();
            return selection.SetEquals(before.Selection) ? before : before with { Selection = selection };
        }, false, token);

    public Task<PreparedEditorChange> PrepareTranslationAsync(PointD delta, CancellationToken token = default)
        => PrepareChangeAsync("ROI 이동", before =>
        {
            var selection = new RoiTranslationSelection(before);
            var constrained = selection.Constrain(delta);
            var next = selection.Apply(constrained, token);
            return selection.PreservesHierarchy(constrained) ? next : RoiHierarchy.Rebuild(before, next, token);
        }, false, token);

    public Task<PreparedEditorChange> PreparePreviewAsync(string name, CancellationToken token = default)
    {
        var before = previewStart ?? throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");
        var translation = TranslationPreview;
        var displayed = State;
        return PrepareChangeAsync(name, _ => translation is { } move ? move.Selection.Apply(move.Delta, token) : displayed,
            translation is null || !translation.Selection.PreservesHierarchy(translation.Delta), token, before);
    }

    private Task<PreparedEditorChange> PrepareChangeAsync(string? name, Func<DocumentState, DocumentState> prepare,
        bool rebuild, CancellationToken token, DocumentState? preview = null)
    {
        if (previewStart is not null && preview is null) throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        var before = preview ?? State; var revision = mutationRevision; var epoch = previewEpoch;
        return EditPreparation.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var next = prepare(before);
            if (rebuild && !ReferenceEquals(before.Rois, next.Rois)) next = RoiHierarchy.Rebuild(before, next, token);
            token.ThrowIfCancellationRequested();
            var edit = name is null ? null : new DocumentEdit(name, before, next, cancellationToken: token);
            Warm(next, token);
            return new PreparedEditorChange(this, revision, epoch, next, edit);
        }, token);
    }

    // Prepare the indexes consumed immediately by input, properties and rendering off the UI thread.
    internal static void Warm(DocumentState state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _ = state.Snapshot.Spatial;
        token.ThrowIfCancellationRequested(); _ = state.Snapshot.Children;
        token.ThrowIfCancellationRequested(); _ = state.SelectionFacts.HasMultipleRoots;
        token.ThrowIfCancellationRequested(); _ = state.SelectedSpatial;
        token.ThrowIfCancellationRequested();
    }

    public void CommitPrepared(PreparedEditorChange prepared)
    {
        if (!ReferenceEquals(prepared.Owner, this) || prepared.Consumed || prepared.Revision != mutationRevision || prepared.PreviewEpoch != previewEpoch)
            throw new InvalidOperationException("편집 준비 중 문서가 변경되었습니다. 다시 시도하세요.");
        if (prepared.Edit is { IsEmpty: false } edit) history.Push(edit);
        prepared.Consumed = true;
        var hadPreview = previewStart is not null;
        previewStart = null; translationSelection = null; TranslationPreview = null; previewEpoch++;
        Publish(prepared.State with { Viewport = State.Viewport });
        if (hadPreview) Changed?.Invoke(this, EventArgs.Empty);
    }
}
