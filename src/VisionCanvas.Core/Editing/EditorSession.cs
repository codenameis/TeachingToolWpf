using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>Application controller: validates and commits edits; geometry/history own their rules.</summary>
public sealed partial class EditorSession
{
    private readonly EditHistory _history = new();
    private DocumentState? _previewStart;
    private RoiTransformSelection? _transformSelection;

    public RoiTransformPreview? TransformPreview
    {
        get;
        private set;
    }

    private long _nextOrder;

    public DocumentState State
    {
        get;
        private set;
    } = DocumentState.Empty;

    public event EventHandler? Changed;

    public bool CanUndo => _history.CanUndo && _previewStart is null;

    public bool CanRedo => _history.CanRedo && _previewStart is null;

    public string? UndoName => _history.UndoName;

    public string? RedoName => _history.RedoName;

    public long HistoryBytes => _history.Bytes;

    public bool IsPreviewing => _previewStart is not null;

    public long PreviewEpoch => _previewEpoch;

    public bool HasPreviewChanges => _previewStart is { } before &&
        (TransformPreview is { Transform.IsIdentity: false } || !State.SameContent(before));

    public Guid Add(RoiGeometry geometry) => AddRoi(geometry, null);

    public Guid AddParent(RoiGeometry geometry) => AddRoi(geometry, null, isGroup: true);

    public Guid AddChild(Guid parentId, RoiGeometry geometry)
    {
        if (!State.Rois.ContainsKey(parentId))
        {
            throw new ArgumentException("자식 ROI를 추가할 부모가 없습니다.", nameof(parentId));
        }

        if (RoiHierarchy.Depth(State.Rois, parentId) >= RoiHierarchy.MaximumDepth)
        {
            throw new InvalidOperationException("ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.");
        }

        return AddRoi(geometry, parentId);
    }

    private Guid AddRoi(RoiGeometry geometry, Guid? parentId, bool isGroup = false)
    {
        geometry = RoiImageBounds.ClipNew(geometry, State.Image);
        var order = _nextOrder + 1;
        var roi = new Roi(Guid.NewGuid(), $"ROI {order:000}", geometry, parentId, isGroup, order);
        string name;
        if (isGroup)
        {
            name = "부모 ROI 추가";
        }
        else if (parentId is null)
        {
            name = "ROI 추가";
        }
        else
        {
            name = "자식 ROI 추가";
        }

        var next = State.WithRois(State.Rois.Add(roi.Id, roi), [roi.Id]).WithSelection([roi.Id]);
        if (parentId is { } parent)
        {
            RoiGroupBounds.Validate(next, [parent], State);
        }

        Commit(name, next);
        _nextOrder = order;
        return roi.Id;
    }

    public IReadOnlyList<Guid> AddRange(IEnumerable<RoiGeometry> geometries, bool selectAdded = false, bool isNameVisible = true)
        => AddRange(geometries.Select(geometry => new RoiCreation(geometry)), selectAdded, isNameVisible);

    public IReadOnlyList<Guid> AddRange(IEnumerable<RoiCreation> definitions, bool selectAdded = false, bool isNameVisible = true)
    {
        var rois = State.Rois.ToBuilder();
        var added = new List<Guid>();
        var order = _nextOrder;
        foreach (var definition in definitions)
        {
            Guid? parent = null;
            if (definition.ParentIndex is { } index)
            {
                if (index < 0 || index >= added.Count)
                {
                    throw new ArgumentException("부모는 같은 배치에서 자식보다 먼저 정의해야 합니다.", nameof(definitions));
                }

                parent = added[index];
                if (RoiHierarchy.Depth(rois, parent.Value) >= RoiHierarchy.MaximumDepth)
                {
                    throw new InvalidOperationException("ROI 계층은 RootGroup을 포함하여 최대 4단계입니다.");
                }
            }

            var roi = new Roi(Guid.NewGuid(), $"ROI {++order:000}", RoiImageBounds.ClipNew(definition.Geometry, State.Image), parent, false, order)
            {
                IsNameVisible = isNameVisible
            };
            rois.Add(roi.Id, roi);
            added.Add(roi.Id);
        }

        var next = State.WithRois(rois.ToImmutable(), added);
        if (selectAdded)
        {
            next = next.WithSelection(RoiIdSet.CreateRange(added));
        }

        RoiGroupBounds.Validate(next, added);
        Commit("ROI 일괄 추가", next);
        _nextOrder = order;
        return added;
    }

    public void ReplaceWithNamedRois(
        string editName,
        IReadOnlyList<Roi> namedRois,
        bool selectAdded = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editName);
        ArgumentNullException.ThrowIfNull(namedRois);

        var builder = ImmutableDictionary.CreateBuilder<Guid, Roi>();
        var changedIds = new List<Guid>(State.Rois.Count + namedRois.Count);
        foreach (var existingId in State.Rois.Keys)
        {
            changedIds.Add(existingId);
        }

        long maxOrder = _nextOrder;
        for (int i = 0; i < namedRois.Count; i++)
        {
            var roi = namedRois[i];
            var clipped = roi with
            {
                Geometry = RoiImageBounds.ClipNew(roi.Geometry, State.Image)
            };
            builder[clipped.Id] = clipped;
            changedIds.Add(clipped.Id);
            if (clipped.Order > maxOrder)
            {
                maxOrder = clipped.Order;
            }
        }

        var next = State.WithRois(builder.ToImmutable(), changedIds);
        next = selectAdded
            ? next.WithSelection(RoiIdSet.CreateRange(namedRois.Select(static r => r.Id)))
            : next.WithSelection([]);

        Commit(editName, next);
        _nextOrder = maxOrder;
    }

    public void EnsureNamedRoiPresent(Roi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);
        if (State.Rois.ContainsKey(roi.Id))
        {
            return;
        }

        var clipped = roi with
        {
            Geometry = RoiImageBounds.ClipNew(roi.Geometry, State.Image)
        };
        var next = State.WithRois(State.Rois.Add(clipped.Id, clipped), [clipped.Id]);
        if (clipped.Order > _nextOrder)
        {
            _nextOrder = clipped.Order;
        }

        Publish(next);
    }

    public void SelectAll()
        => Publish(SelectAllState(State, CancellationToken.None));

    private static DocumentState SelectAllState(DocumentState before, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (before.Selection.Count == before.Rois.Count)
        {
            return before;
        }

        return before.WithSelection(RoiIdSet.CreateRange(before.Rois.Keys));
    }

    public void Select(IEnumerable<Guid> ids)
        => Publish(SelectState(State, ids, CancellationToken.None));

    private static DocumentState SelectState(DocumentState before, IEnumerable<Guid> ids, CancellationToken token)
    {
        var selection = RoiIdSet.CreateRange(ids.Where(id =>
        {
            token.ThrowIfCancellationRequested();
            return before.Rois.ContainsKey(id);
        }));
        if (selection.SetEquals(before.Selection))
        {
            return before;
        }

        return before.WithSelection(selection);
    }

    public void SelectRegion(BoundsD bounds, bool additive = false)
        => Publish(SelectRegionState(State, bounds, additive, CancellationToken.None));

    private static DocumentState SelectRegionState(DocumentState before, BoundsD bounds, bool additive, CancellationToken token)
    {
        var ids = FindContainedOrdinaryIds(before, bounds, token);
        var nextSelection = additive ? before.Selection.Union(ids) : RoiIdSet.CreateRange(ids);
        return before.WithSelection(nextSelection);
    }

    internal static IEnumerable<Guid> FindContainedOrdinaryIds(DocumentState state, BoundsD bounds, CancellationToken token = default)
        => state.Snapshot.Spatial.Intersecting(bounds).Where(roi =>
        {
            token.ThrowIfCancellationRequested();
            return roi.Geometry.Bounds.Left >= bounds.Left
                && roi.Geometry.Bounds.Top >= bounds.Top
                && roi.Geometry.Bounds.Right <= bounds.Right
                && roi.Geometry.Bounds.Bottom <= bounds.Bottom;
        }).Select(roi => roi.Id);

    public void Paste(RoiClipboard clipboard)
    {
        if (!clipboard.HasContent || State.Image is null)
        {
            return;
        }

        var content = clipboard.Capture();
        var step = new PointD(20 / State.Viewport.Scale, 20 / State.Viewport.Scale);
        Commit("ROI 붙여넣기", RoiPaste.Prepare(State, content, State.Image, step, _nextOrder, CancellationToken.None));
        _nextOrder += content.OrderCount;
        clipboard.Pasted();
    }

    public async Task PasteAsync(RoiClipboard clipboard, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!clipboard.HasContent || State.Image is null)
        {
            return;
        }

        var prepared = await PreparePasteAsync(clipboard, token);
        token.ThrowIfCancellationRequested();
        // The caller's synchronization context owns commit. Navigation may continue during preparation.
        CommitPreparedPaste(prepared);
    }

    public void SetViewport(ViewportTransform view) => Publish(State.WithViewport(view));

    public void SetImage(ImageReference image, ViewportTransform view)
        => Commit("이미지 교체", new DocumentState(ImmutableDictionary<Guid, Roi>.Empty, [], image, view), replacesImage: true);

    public void InitializeImage(ImageReference image, ViewportTransform view)
    {
        if (State.Image is not null || State.RoiCount != 0)
        {
            throw new InvalidOperationException("이미지 초기화는 빈 문서에서만 가능합니다.");
        }

        Publish(State.WithImage(image, view));
    }

    public void SetProperties(Guid id, string name, RoiGeometry geometry)
        => SetProperties(id, name, geometry, State.Rois[id].SearchRange, State.Rois[id].IsNameVisible);

    public void SetProperties(Guid id, string name, RoiGeometry geometry, RoiSearchRange? searchRange, bool isNameVisible)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            throw new ArgumentException("이름은 1–256자여야 합니다.");
        }

        if (State.IsGroupRoi(id) && searchRange != State.Rois[id].SearchRange)
        {
            throw new ArgumentException("그룹 부모에는 검색 범위를 설정할 수 없습니다.", nameof(searchRange));
        }

        // 확장 크기의 오버플로도 형상/이름을 게시하기 전에 검증한다.
        _ = searchRange?.Expand(geometry);
        var next = RoiTransforms.Apply(State, [id], _ => geometry);
        var changedIds = ReferenceEquals(next, State) ? [id] : next.Snapshot.ChangedIds.Append(id);
        Commit("ROI 속성 변경", next.WithRoisFrom(State, next.Rois.SetItem(id, next.Rois[id] with
        {
            Name = name.Trim(),
            SearchRange = searchRange,
            IsNameVisible = isNameVisible
        }), changedIds));
    }

    public void Rename(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            throw new ArgumentException("이름은 1–256자여야 합니다.", nameof(name));
        }

        Commit("이름 변경", State.WithRois(State.Rois.SetItem(id, State.Rois[id] with
        {
            Name = name.Trim()
        }), [id]));
    }

    public void DeleteSelection()
        => Commit("선택 ROI 삭제", DeleteSelectionState(State, CancellationToken.None));

    private static DocumentState DeleteSelectionState(DocumentState before, CancellationToken token)
    {
        // 명시적으로 연결된 하위 트리만 삭제하며, 겹치는 독립 ROI는 보존한다.
        var ids = before.Selection.Count == before.Rois.Count
            ? before.Selection
            : RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before), token).ToImmutableHashSet();
        var rois = before.Rois.ToBuilder();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            rois.Remove(id);
        }

        return before.WithRois(rois.ToImmutable(), ids)
            .WithSelection(before.Selection.Except(ids));
    }

    public void SetFilters(FilterSettings settings)
    {
        settings.Validate();
        var pipeline = settings.Kind == RoiFilterKind.None
            ? ImmutableArray<FilterSettings>.Empty
            : [settings];
        var rois = State.Rois.ToBuilder();
        foreach (var id in State.Selection)
        {
            rois[id] = rois[id] with
            {
                Filter = settings,
                FilterPipeline = pipeline
            };
        }

        Commit("ROI 필터 등록", State.WithRois(rois.ToImmutable(), State.Selection));
    }

    public void SetFilterPipeline(ImmutableArray<FilterSettings> pipeline)
    {
        var normalized = pipeline.IsDefault
            ? ImmutableArray<FilterSettings>.Empty
            : pipeline.Where(static s => s.Kind != RoiFilterKind.None).ToImmutableArray();
        foreach (var step in normalized)
        {
            step.Validate();
        }

        var primary = normalized.LastOrDefault(static s => s.IsEnabled)
                      ?? (normalized.IsEmpty ? new FilterSettings(RoiFilterKind.None) : normalized[^1]);

        var rois = State.Rois.ToBuilder();
        foreach (var id in State.Selection)
        {
            rois[id] = rois[id] with
            {
                Filter = primary,
                FilterPipeline = normalized
            };
        }

        Commit("ROI 전처리 파이프라인 등록", State.WithRois(rois.ToImmutable(), State.Selection));
    }

    public void TranslateSelection(PointD delta)
        => Commit("ROI 이동", TranslateSelectionState(State, delta, CancellationToken.None).WithViewport(State.Viewport));

    private static DocumentState TranslateSelectionState(DocumentState before, PointD delta, CancellationToken token)
    {
        var selection = before.TransformSelection;
        var transform = new RigidTransform2D(default, 0, selection.Constrain(delta));
        return selection.Apply(transform, token);
    }

    public void RotateSelection(double delta, PointD pivot)
        => Commit("ROI 회전", RotateSelectionState(State, delta, pivot, CancellationToken.None).WithViewport(State.Viewport));

    private static DocumentState RotateSelectionState(DocumentState before, double delta, PointD pivot, CancellationToken token)
    {
        var selection = before.TransformSelection;
        var transform = new RigidTransform2D(pivot, selection.ConstrainRotation(pivot, delta));
        return selection.Apply(transform, token);
    }

    public void ResetAngles() => Commit("0°로 초기화", RoiTransforms.ResetAngles(State));

    public void SetGeometry(Guid id, RoiGeometry geometry) => Commit("ROI 속성 변경", RoiTransforms.Apply(State, [id], _ => geometry));

    public Guid GroupSelection()
    {
        var order = _nextOrder + 1;
        var next = RoiGroupEditor.Group(State, order, CancellationToken.None);
        var createdGroup = next.Rois.Count > State.Rois.Count;
        Commit("부모 ROI로 그룹화", next);
        if (createdGroup)
        {
            _nextOrder = order;
        }

        return next.Selection.Single();
    }

    public void UngroupSelection()
        => Commit("그룹 해제", RoiGroupEditor.Ungroup(State, CancellationToken.None));

    public DocumentState PreviewUndo() => _history.PreviewUndo(State);

    public DocumentState PreviewRedo() => _history.PreviewRedo(State);

    public void Undo()
    {
        if (CanUndo)
        {
            Publish(_history.Undo(State));
        }
    }

    public void Redo()
    {
        if (CanRedo)
        {
            Publish(_history.Redo(State));
        }
    }

    public void BeginPreview()
    {
        CancelPreview();
        _previewEpoch++;
        _previewStart = State;
    }

    public void PreviewTranslation(PointD delta, bool allowSlide = true)
    {
        var before = RequirePreviewStart();
        if (delta == default && _transformSelection is null)
        {
            return;
        }

        _transformSelection ??= before.TransformSelection;
        var previous = TransformPreview is { Transform.AngleDegrees: 0 } preview ? preview.Transform.Translation : default;
        SetTransformPreview(new(default, 0, _transformSelection.Constrain(delta, previous, allowSlide)));
    }

    public void PreviewRotation(PointD pivot, double degrees)
    {
        var before = RequirePreviewStart();
        if (degrees == 0 && _transformSelection is null)
        {
            return;
        }

        _transformSelection ??= before.TransformSelection;
        var constrainedDegrees = _transformSelection.ConstrainRotation(pivot, degrees);
        SetTransformPreview(new(pivot, constrainedDegrees));
    }

    private void SetTransformPreview(RigidTransform2D transform)
    {
        if (TransformPreview?.Transform == transform)
        {
            return;
        }

        TransformPreview = new(_transformSelection!, transform);
        _mutationRevision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PreviewTransform(Func<Roi, RoiGeometry> transform)
    {
        var before = RequirePreviewStart();
        Publish(RoiTransforms.Apply(before, RoiHierarchy.EffectiveRoots(before), transform));
    }

    public void PreviewGeometry(Guid id, RoiGeometry geometry)
    {
        var before = RequirePreviewStart();
        Publish(RoiTransforms.Apply(before, [id], _ => geometry));
    }

    public void PreviewResize(Guid id, RoiGeometry geometry)
    {
        var before = RequirePreviewStart();
        // 자식 크기는 유지하고, 핸들 조절로 이동한 부모 중심의 변위만 함께 적용한다.
        Publish(RoiTransforms.Apply(before, [id], _ => geometry));
    }

    private DocumentState RequirePreviewStart()
        => _previewStart ?? throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");

    public void CommitPreview(string name)
    {
        if (_previewStart is null)
        {
            return;
        }

        var before = _previewStart;
        var preview = TransformPreview;
        var displayedPreview = preview is not null || !ReferenceEquals(State, before);

        // 공통 변환의 실제 ROI 값은 확정 시 한 번만 만든다. 준비 실패 시 preview는 그대로 남는다.
        var next = State;
        if (preview is not null)
        {
            next = preview.Selection.ApplyPreview(preview.Transform).WithViewport(State.Viewport);
        }

        ClearPreviewState();
        var restored = PreserveCurrentViewport(before);
        State = restored;

        // 이력은 시작 상태를 기준으로 기록한다. 확정에 실패하면 시작 ROI와 현재 탐색 위치를 유지한다.
        try
        {
            Commit(name, next);
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
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void CancelPreview()
    {
        if (_previewStart is not { } before)
        {
            return;
        }

        var hadTransformPreview = TransformPreview is not null;
        ClearPreviewState();
        Publish(PreserveCurrentViewport(before));
        if (hadTransformPreview)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearPreviewState()
    {
        TransformPreview = null;
        _transformSelection = null;
        _previewStart = null;
        _previewEpoch++;
    }

    private DocumentState PreserveCurrentViewport(DocumentState before)
        => before.WithViewport(State.Viewport);

    private void Commit(string name, DocumentState next, bool replacesImage = false)
    {
        if (_previewStart is not null)
        {
            throw new InvalidOperationException("진행 중인 편집을 먼저 완료하세요.");
        }

        if (!replacesImage && State.SameContent(next) && State.Image == next.Image)
        {
            return;
        }

        var edit = new DocumentEdit(name, State, next, replacesImage);
        if (edit.IsEmpty)
        {
            return;
        }

        // 이력 예산 검사와 Redo 분기 처리가 성공한 뒤에만 새 문서를 게시한다.
        _history.Push(edit, State, next);
        Publish(next);
    }

    private void Publish(DocumentState next)
    {
        var documentChanged = !next.SameContent(State)
            || !next.SameSelection(State)
            || next.Image != State.Image;
        var viewportChanged = next.Viewport != State.Viewport;
        if (!documentChanged && !viewportChanged)
        {
            return;
        }

        // 탐색만 바뀌면 준비 중인 ROI 편집은 여전히 유효하다.
        if (documentChanged)
        {
            _mutationRevision++;
        }

        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
