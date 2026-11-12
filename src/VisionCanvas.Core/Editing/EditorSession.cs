using System.Collections.Immutable;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>Application controller: validates and commits edits; geometry/history/preview own their rules.</summary>
public sealed partial class EditorSession
{
    private readonly EditHistory _history = new();
    private readonly PreviewSession _preview = new();
    private long _nextOrder;
    private long _mutationRevision;

    public RoiTransformPreview? TransformPreview => _preview.TransformPreview;

    public DocumentState State { get; private set; } = DocumentState.Empty;

    public event EventHandler? Changed;

    public bool CanUndo => _history.CanUndo && !_preview.IsActive;

    public bool CanRedo => _history.CanRedo && !_preview.IsActive;

    public string? UndoName => _history.UndoName;

    public string? RedoName => _history.RedoName;

    public long HistoryBytes => _history.Bytes;

    public bool IsPreviewing => _preview.IsActive;

    public long PreviewEpoch => _preview.Epoch;

    public bool HasPreviewChanges => _preview.HasChanges(State);

    public void Execute(IEditorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.PreviewBaseline is null)
        {
            ThrowIfPreviewing();
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
        var id = Guid.NewGuid();
        Execute(new EditorOperations.AddRoiOperation(id, geometry, parentId, isGroup, _nextOrder));
        return id;
    }

    public IReadOnlyList<Guid> AddRange(IEnumerable<RoiGeometry> geometries, bool selectAdded = false, bool isNameVisible = true)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        return AddRange(geometries.Select(static geometry => new RoiCreation(geometry)), selectAdded, isNameVisible);
    }

    public IReadOnlyList<Guid> AddRange(IEnumerable<RoiCreation> definitions, bool selectAdded = false, bool isNameVisible = true)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var operation = new EditorOperations.AddRangeOperation(definitions, selectAdded, isNameVisible, _nextOrder);
        Execute(operation);
        return operation.AddedIds;
    }

    public void ReplaceWithNamedRois(
        string editName,
        IReadOnlyList<Roi> namedRois,
        bool selectAdded = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editName);
        ArgumentNullException.ThrowIfNull(namedRois);
        Execute(new EditorOperations.ReplaceWithNamedRoisOperation(editName, namedRois, selectAdded, _nextOrder));
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
        => Publish(new EditorOperations.SelectAllOperation().ComputeNextState(State, CancellationToken.None));

    public void Select(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Publish(new EditorOperations.SelectOperation(ids).ComputeNextState(State, CancellationToken.None));
    }

    public void SelectRegion(BoundsD bounds, bool additive = false)
        => Publish(new EditorOperations.SelectRegionOperation(bounds, additive).ComputeNextState(State, CancellationToken.None));

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
        ArgumentNullException.ThrowIfNull(clipboard);
        if (!clipboard.HasContent || State.Image is not { } image)
        {
            return;
        }

        var content = clipboard.Capture();
        Execute(new EditorOperations.PasteOperation(content, image, CreateDefaultPasteStep(), _nextOrder));
        clipboard.Pasted();
    }

    public async Task PasteAsync(RoiClipboard clipboard, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
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

    public void SetViewport(ViewportTransform view)
        => Publish(State.WithViewport(view));

    public void SetImage(ImageReference image, ViewportTransform view)
    {
        ArgumentNullException.ThrowIfNull(image);
        Execute(new EditorOperations.SetImageOperation(image, view));
    }

    public void InitializeImage(ImageReference image, ViewportTransform view)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (State.Image is not null || State.RoiCount != 0)
        {
            throw new InvalidOperationException("이미지 초기화는 빈 문서에서만 가능합니다.");
        }

        Publish(State.WithImage(image, view));
    }

    public void SetProperties(Guid id, string name, RoiGeometry geometry)
        => SetProperties(id, name, geometry, State.Rois[id].SearchRange, State.Rois[id].IsNameVisible);

    public void SetProperties(Guid id, string name, RoiGeometry geometry, RoiSearchRange? searchRange, bool isNameVisible)
        => Execute(new EditorOperations.SetPropertiesOperation(id, name, geometry, searchRange, isNameVisible));

    public void Rename(Guid id, string name)
        => Execute(new EditorOperations.RenameOperation(id, name));

    public void SetOcvSettings(IReadOnlyList<Guid> ids, RoiOcvSettings settings)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(settings);
        Execute(new EditorOperations.SetOcvSettingsOperation(ids, settings));
    }

    public void DeleteSelection()
        => Execute(new EditorOperations.DeleteSelectionOperation());

    public void SetFilters(FilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Execute(new EditorOperations.SetFiltersOperation(settings));
    }

    public void SetFilterPipeline(ImmutableArray<FilterSettings> pipeline)
        => Execute(new EditorOperations.SetFilterPipelineOperation(pipeline));

    public void TranslateSelection(PointD delta)
        => Execute(new EditorOperations.TranslateSelectionOperation(delta));

    public void RotateSelection(double delta, PointD pivot)
        => Execute(new EditorOperations.RotateSelectionOperation(delta, pivot));

    public void ResetAngles()
        => Execute(new EditorOperations.ResetAnglesOperation());

    public void SetGeometry(Guid id, RoiGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        Execute(new EditorOperations.SetGeometryOperation(id, geometry));
    }

    public Guid GroupSelection()
    {
        Execute(new EditorOperations.GroupSelectionOperation(_nextOrder));
        return State.Selection.Single();
    }

    public void UngroupSelection()
        => Execute(new EditorOperations.UngroupSelectionOperation());

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
        _preview.Begin(State);
    }

    public void PreviewTranslation(PointD delta, bool allowSlide = true)
    {
        if (_preview.TryUpdateTranslation(delta, allowSlide))
        {
            _mutationRevision++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void PreviewRotation(PointD pivot, double degrees)
    {
        if (_preview.TryUpdateRotation(pivot, degrees))
        {
            _mutationRevision++;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void PreviewResize(Guid id, RoiGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var before = _preview.RequireStart();
        // 자식 크기는 유지하고, 핸들 조절로 이동한 부모 중심의 변위만 함께 적용한다.
        Publish(RoiTransforms.Apply(before, [id], _ => geometry));
    }

    public void CommitPreview(string name)
    {
        if (_preview.Start is not { } before)
        {
            return;
        }

        var preview = _preview.TransformPreview;
        var displayedPreview = preview is not null || !ReferenceEquals(State, before);

        // 공통 변환의 실제 ROI 값은 확정 시 한 번만 만든다. 준비 실패 시 preview는 그대로 남는다.
        var next = State;
        if (preview is not null)
        {
            next = preview.Selection.ApplyPreview(preview.Transform).WithViewport(State.Viewport);
        }

        _preview.Clear();
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
        if (_preview.Start is not { } before)
        {
            return;
        }

        var hadTransformPreview = _preview.TransformPreview is not null;
        _preview.Clear();
        Publish(PreserveCurrentViewport(before));
        if (hadTransformPreview)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private DocumentState PreserveCurrentViewport(DocumentState before)
        => before.WithViewport(State.Viewport);

    private void ThrowIfPreviewing(string message = "진행 중인 편집을 먼저 완료하세요.")
    {
        if (_preview.IsActive)
        {
            throw new InvalidOperationException(message);
        }
    }

    private void Commit(string name, DocumentState next, bool replacesImage = false)
    {
        ThrowIfPreviewing();

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
