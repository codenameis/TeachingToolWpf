using System.Collections.Immutable;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Editing;

/// <summary>Application controller: validates and commits edits; geometry/history own their rules.</summary>
public sealed partial class EditorSession
{
    private readonly EditHistory history = new();
    private DocumentState? previewStart;
    private RoiTransformSelection? transformSelection;
    public RoiTransformPreview? TransformPreview
    {
        get; private set;
    }
    private long nextOrder;
    public DocumentState State { get; private set; } = DocumentState.Empty;
    public event EventHandler? Changed;
    public bool CanUndo => history.CanUndo && previewStart is null;
    public bool CanRedo => history.CanRedo && previewStart is null;
    public string? UndoName => history.UndoName;
    public string? RedoName => history.RedoName;
    public long HistoryBytes => history.Bytes;
    public bool IsPreviewing => previewStart is not null;
    public long PreviewEpoch => previewEpoch;
    public bool HasPreviewChanges => previewStart is { } before &&
        (TransformPreview is { Transform.IsIdentity: false } || !ReferenceEquals(State.Rois, before.Rois));

    public Guid Add(RotatedRect geometry)
    {
        geometry = RoiImageBounds.ClipNew(geometry, State.Image);
        var roi = new Roi(Guid.NewGuid(), $"ROI {++nextOrder:000}", geometry, null, false, nextOrder);
        Commit("ROI 추가", State.WithRois(State.Rois.Add(roi.Id, roi), [roi.Id]) with
        {
            Selection = [roi.Id]
        });
        return roi.Id;
    }
    public void AddRange(IEnumerable<RotatedRect> geometries)
    {
        var rois = State.Rois.ToBuilder();
        var added = new List<Guid>();
        foreach (var geometry in geometries)
        {
            var roi = new Roi(Guid.NewGuid(), $"ROI {++nextOrder:000}", RoiImageBounds.ClipNew(geometry, State.Image), null, false, nextOrder);
            rois.Add(roi.Id, roi);
            added.Add(roi.Id);
        }
        Commit("ROI 일괄 추가", State.WithRois(rois.ToImmutable(), added));
    }
    public void Select(IEnumerable<Guid> ids)
    {
        var selected = ids.Where(State.Rois.ContainsKey).ToImmutableHashSet();
        if (!State.Selection.SetEquals(selected))
            Publish(State with
            {
                Selection = selected
            });
    }
    public void Paste(RoiClipboard clipboard)
    {
        if (!clipboard.HasContent || State.Image is null)
            return;
        var templates = clipboard.Place(State.Image, new(20 / State.Viewport.Scale, 20 / State.Viewport.Scale));
        Commit("ROI 붙여넣기", RoiPaste.Prepare(State, templates, nextOrder, CancellationToken.None), rebuild: false);
        nextOrder += templates.Length;
        clipboard.Pasted();
    }
    public async Task PasteAsync(RoiClipboard clipboard, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!clipboard.HasContent || State.Image is null)
            return;
        var prepared = await PreparePasteAsync(clipboard, token);
        token.ThrowIfCancellationRequested();
        // The caller's synchronization context owns commit. Navigation may continue during preparation.
        CommitPreparedPaste(prepared);
    }
    public void SetViewport(ViewportTransform view) => Publish(State with { Viewport = view });
    public void SetImage(ImageReference image, ViewportTransform view) => Commit("이미지 교체", State with
    {
        Image = image,
        Viewport = view,
        Rois = ImmutableDictionary<Guid, Roi>.Empty,
        Selection = []
    }, rebuild: false, replacesImage: true);
    public void InitializeImage(ImageReference image, ViewportTransform view)
    {
        if (State.Image is not null || State.Rois.Count != 0)
            throw new InvalidOperationException("이미지 초기화는 빈 문서에서만 가능합니다.");
        Publish(State with
        {
            Image = image,
            Viewport = view
        });
    }
    public void SetProperties(Guid id, string name, RotatedRect geometry)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
            throw new ArgumentException("이름은 1–256자여야 합니다.");
        var next = RoiTransforms.Apply(State, [id], _ => geometry);
        Commit("ROI 속성 변경", next.WithRoisFrom(State, next.Rois.SetItem(id, next.Rois[id] with
        {
            Name = name.Trim()
        }),
            ReferenceEquals(next, State) ? [id] : next.Snapshot.ChangedIds.Append(id)));
    }
    public void Rename(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
            throw new ArgumentException("이름은 1–256자여야 합니다.", nameof(name));
        Commit("이름 변경", State.WithRois(State.Rois.SetItem(id, State.Rois[id] with
        {
            Name = name.Trim()
        }), [id]), rebuild: false);
    }
    public void DeleteSelection()
    {
        var ids = RoiHierarchy.Subtree(State, RoiHierarchy.EffectiveRoots(State));
        // Removing complete subtrees cannot change a surviving ROI's smallest containing parent.
        Commit("선택 ROI 삭제", State.WithRois(State.Rois.RemoveRange(ids), ids) with
        {
            Selection = State.Selection.Except(ids)
        }, rebuild: false);
    }
    public void SetFilters(FilterSettings settings)
    {
        settings.Validate();
        var rois = State.Rois.ToBuilder();
        foreach (var id in State.Selection)
            rois[id] = rois[id] with
            {
                Filter = settings
            };
        Commit("ROI 필터 등록", State.WithRois(rois.ToImmutable(), State.Selection), rebuild: false);
    }
    public void TranslateSelection(PointD delta)
    {
        var selection = State.TransformSelection;
        var transform = new RigidTransform2D(default, 0, selection.Constrain(delta));
        Commit("ROI 이동", selection.Apply(transform) with
        {
            Viewport = State.Viewport
        }, rebuild: !selection.PreservesHierarchy(transform));
    }
    public void RotateSelection(double delta, PointD pivot)
    {
        var selection = State.TransformSelection;
        var transform = new RigidTransform2D(pivot, selection.RotationLimits(pivot).Clamp(delta));
        Commit("ROI 회전", selection.Apply(transform) with
        {
            Viewport = State.Viewport
        }, rebuild: !selection.PreservesHierarchy(transform));
    }
    public void ResetAngles() => Commit("0°로 초기화", RoiTransforms.ResetAngles(State));
    public void SetGeometry(Guid id, RotatedRect geometry) => Commit("ROI 속성 변경", RoiTransforms.Apply(State, [id], _ => geometry));

    public Guid GroupSelection()
    {
        var roots = RoiHierarchy.EffectiveRoots(State).ToArray();
        if (roots.Length < 2)
            throw new InvalidOperationException("독립적인 ROI를 2개 이상 선택하세요.");
        var bounds = BoundsD.FromPoints(roots.SelectMany(id => State.Rois[id].Geometry.Corners));
        var padding = Math.Max(1, Math.Max(Math.Abs(bounds.Center.X), Math.Abs(bounds.Center.Y)) * 1e-10);
        var group = new Roi(Guid.NewGuid(), $"그룹 {++nextOrder:000}",
            RoiImageBounds.ClipNew(new(bounds.Center, bounds.Width + padding * 2, bounds.Height + padding * 2, 0), State.Image), null, true, nextOrder);
        var next = RoiHierarchy.Rebuild(State, State.WithRois(State.Rois.Add(group.Id, group), [group.Id]) with
        {
            Selection = [group.Id]
        });
        if (roots.Any(id => !RoiHierarchy.IsDescendant(next, id, group.Id)))
            throw new InvalidOperationException("기존 부모와 충돌합니다. 해당 부모 ROI까지 선택한 뒤 그룹화하세요.");
        Commit("부모 ROI로 그룹화", next, rebuild: false);
        return group.Id;
    }
    public void UngroupSelection()
    {
        var groups = State.Selection.Where(id => State.Rois[id].IsGroup).ToHashSet();
        var children = groups.SelectMany(id => State.Snapshot.Children.GetValueOrDefault(id, [])).ToImmutableHashSet();
        Commit("그룹 해제", State.WithRois(State.Rois.RemoveRange(groups), groups) with
        {
            Selection = children.Except(groups)
        });
    }
    public DocumentState PreviewUndo() => history.PreviewUndo(State);
    public DocumentState PreviewRedo() => history.PreviewRedo(State);
    public void Undo()
    {
        if (CanUndo)
            Publish(history.Undo(State));
    }

    public void Redo()
    {
        if (CanRedo)
            Publish(history.Redo(State));
    }

    public void BeginPreview()
    {
        CancelPreview();
        previewEpoch++;
        previewStart = State;
    }

    public void PreviewTranslation(PointD delta)
    {
        var before = RequirePreviewStart();
        if (delta == default && transformSelection is null)
            return;

        transformSelection ??= before.TransformSelection;
        SetTransformPreview(new(default, 0, transformSelection.Constrain(delta)));
    }

    public void PreviewRotation(PointD pivot, double degrees)
    {
        var before = RequirePreviewStart();
        if (degrees == 0 && transformSelection is null)
            return;

        transformSelection ??= before.TransformSelection;
        var constrainedDegrees = transformSelection.RotationLimits(pivot).Clamp(degrees);
        SetTransformPreview(new(pivot, constrainedDegrees));
    }

    private void SetTransformPreview(RigidTransform2D transform)
    {
        if (TransformPreview?.Transform == transform)
            return;

        TransformPreview = new(transformSelection!, transform);
        mutationRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PreviewTransform(Func<Roi, RotatedRect> transform)
    {
        var before = RequirePreviewStart();
        Publish(RoiTransforms.Apply(before, RoiHierarchy.EffectiveRoots(before), transform));
    }

    public void PreviewGeometry(Guid id, RotatedRect geometry)
    {
        var before = RequirePreviewStart();
        Publish(RoiTransforms.Apply(before, [id], _ => geometry));
    }

    public void PreviewResize(Guid id, RotatedRect geometry)
    {
        var before = RequirePreviewStart();
        // 자식 크기는 유지하고, 핸들 조절로 이동한 부모 중심의 변위만 함께 적용한다.
        Publish(RoiTransforms.Apply(before, [id], _ => geometry));
    }

    private DocumentState RequirePreviewStart()
        => previewStart ?? throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");

    public void CommitPreview(string name)
    {
        if (previewStart is null)
            return;

        var before = previewStart;
        var preview = TransformPreview;
        var displayedPreview = preview is not null || !ReferenceEquals(State, before);

        // 공통 변환의 실제 ROI 값은 확정 시 한 번만 만든다. 준비 실패 시 preview는 그대로 남는다.
        var next = State;
        if (preview is not null)
            next = preview.Selection.Apply(preview.Transform) with
            {
                Viewport = State.Viewport
            };

        ClearPreviewState();
        var restored = PreserveCurrentViewport(before);
        State = restored;

        // 이력은 시작 상태를 기준으로 기록한다. 확정에 실패하면 시작 ROI와 현재 탐색 위치를 유지한다.
        try
        {
            var requiresHierarchyRebuild = preview is null || !preview.Selection.PreservesHierarchy(preview.Transform);
            Commit(name, next, rebuild: requiresHierarchyRebuild);
        }
        catch
        {
            Publish(restored);
            throw;
        }
        finally
        {
            // 원점으로 되돌아온 드래그도 화면의 preview 표시를 제거해야 한다.
            if (displayedPreview && ReferenceEquals(State, restored))
                Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void CancelPreview()
    {
        if (previewStart is not { } before)
            return;

        var hadTransformPreview = TransformPreview is not null;
        ClearPreviewState();
        Publish(PreserveCurrentViewport(before));
        if (hadTransformPreview)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPreviewState()
    {
        TransformPreview = null;
        transformSelection = null;
        previewStart = null;
        previewEpoch++;
    }

    private DocumentState PreserveCurrentViewport(DocumentState before)
        => before.Viewport == State.Viewport ? before : before with
        {
            Viewport = State.Viewport
        };

    private void Commit(string name, DocumentState next, bool rebuild = true, bool replacesImage = false)
    {
        if (previewStart is not null)
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        if (!replacesImage && ReferenceEquals(State.Rois, next.Rois) && State.Image == next.Image)
            return;

        if (rebuild)
            next = RoiHierarchy.Rebuild(State, next);
        var edit = new DocumentEdit(name, State, next, replacesImage);
        if (edit.IsEmpty)
            return;

        // 이력 예산 검사와 Redo 분기 처리가 성공한 뒤에만 새 문서를 게시한다.
        history.Push(edit);
        Publish(next);
    }

    private void Publish(DocumentState next)
    {
        var documentChanged = !ReferenceEquals(next.Rois, State.Rois)
            || !ReferenceEquals(next.Selection, State.Selection)
            || next.Image != State.Image;
        var viewportChanged = next.Viewport != State.Viewport;
        if (!documentChanged && !viewportChanged)
            return;

        // 탐색만 바뀌면 준비 중인 ROI 편집은 여전히 유효하다.
        if (documentChanged)
            mutationRevision++;
        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
