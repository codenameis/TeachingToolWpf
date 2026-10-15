using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Interaction;

/// <summary>입력에 맞는 제스처를 선택하고 이동·확정·취소를 전달한다. 문서 변경과 이력은 EditorSession이 소유한다.</summary>
public sealed class InteractionController(EditorSession editor, Action<string>? commitPreview = null, Action<IEnumerable<Guid>>? selectRois = null,
    Action<PointD, bool>? previewTranslation = null, Action<PointD, double>? previewRotation = null, Action<BoundsD, bool>? selectRegion = null) : IViewportInput
{
    private const double RoiHitToleranceDip = 3;
    private const double PolygonClosureDistanceDip = 8;

    private IGesture? _activeGesture;
    private EditorTool _tool;
    private RoiShapeKind _drawShape;
    private readonly List<PointD> _polygonPoints = [];
    private ImageReference? _polygonStartImage;
    private Guid? _drawParentId;
    private bool _drawAsGroup;
    private double _handleScale = HandleGeometry.DefaultScale;

    /// <summary>화면 DIP 기준 핸들 크기와 클릭 범위를 함께 조절한다. ROI 형상과 이력은 바꾸지 않는다.</summary>
    public double HandleScale
    {
        get => _handleScale;
        set
        {
            HandleGeometry.ValidateScale(value);
            if (_handleScale == value)
            {
                return;
            }
            _handleScale = value;
            Show(Overlay);
        }
    }

    public Guid? MaskTargetId
    {
        get; private set;
    }
    public MaskShape MaskShape
    {
        get; private set;
    }

    public void StartMask(MaskShape shape)
    {
        if (NavigationOnly || editor.IsPreviewing || editor.State.SelectionCount != 1 || editor.State.Selection.Count != 1)
        {
            return;
        }
        var id = editor.State.Selection.Single();
        if (editor.State.IsGroupRoi(id) || editor.State.Rois[id].Masks.Length >= EditorSession.MaximumMasksPerRoi)
        {
            return;
        }
        MaskTargetId = id;
        MaskShape = shape;
        Tool = EditorTool.Mask;
    }

    public Guid? DrawParentId
    {
        get => _drawParentId;
        set
        {
            Cancel();
            _drawParentId = value;
            _drawAsGroup = false;
            Changed?.Invoke();
        }
    }

    public bool DrawAsGroup
    {
        get => _drawAsGroup;
        set
        {
            Cancel();
            _drawAsGroup = value;
            if (value)
            {
                _drawParentId = null;
            }
            Changed?.Invoke();
        }
    }

    internal void AddDrawnRoi(RoiGeometry geometry)
    {
        if (DrawAsGroup)
        {
            editor.AddParent(geometry);
        }
        else if (DrawParentId is { } parentId)
        {
            if (!editor.State.Rois.TryGetValue(parentId, out var parent))
            {
                throw new ArgumentException("자식 ROI를 추가할 부모가 없습니다.");
            }
            if (!parent.Geometry.Contains(geometry))
            {
                throw new ArgumentException("자식 ROI는 부모 도형 안에 완전히 들어오도록 그리세요. 경계에 걸칠 수 없습니다.");
            }
            editor.AddChild(parentId, geometry);
        }
        else
        {
            editor.Add(geometry);
        }
    }

    public RoiShapeKind DrawShape
    {
        get => _drawShape;
        set
        {
            Cancel();
            _drawShape = value;
            Changed?.Invoke();
        }
    }

    public int PolygonPointCount => _polygonPoints.Count;

    public bool CanCompletePolygon => _polygonPoints.Count >= 3;

    private double _viewportWidthDip;

    private double _viewportHeightDip;

    public MinimapLayout? Minimap => editor.State.Image is { } image ? MinimapLayout.Create(image.Width, image.Height, _viewportWidthDip, _viewportHeightDip) : null;

    public void SetViewportSize(double width, double height)
    {
        if (width == _viewportWidthDip && height == _viewportHeightDip)
        {
            return;
        }

        _viewportWidthDip = width;
        _viewportHeightDip = height;
        if (_activeGesture is MinimapGesture || _polygonPoints.Count > 0)
        {
            Cancel();
        }
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            Cancel();
            _tool = value;
            Changed?.Invoke();
        }
    }

    public InteractionOverlay Overlay
    {
        get;
        private set;
    } = new();

    public bool IsDragging => _activeGesture is not null;

    public bool NavigationOnly
    {
        get;
        set;
    }

    public event Action? Changed;

    internal EditorSession Editor => editor;

    internal void PreviewTranslation(PointD delta, bool allowSlide)
    {
        if (previewTranslation is null)
        {
            editor.PreviewTranslation(delta, allowSlide);
        }
        else
        {
            previewTranslation(delta, allowSlide);
        }
    }

    internal void PreviewRotation(PointD pivot, double degrees)
    {
        if (previewRotation is null)
        {
            editor.PreviewRotation(pivot, degrees);
        }
        else
        {
            previewRotation(pivot, degrees);
        }
    }

    internal void CommitPreview(string name)
    {
        if (commitPreview is null)
        {
            editor.CommitPreview(name);
        }
        else
        {
            commitPreview(name);
        }
    }

    internal void Select(IEnumerable<Guid> ids)
    {
        if (selectRois is null)
        {
            editor.Select(ids);
        }
        else
        {
            selectRois(ids);
        }
    }

    internal void Show(InteractionOverlay overlay)
    {
        Overlay = overlay.HandleScale == HandleScale ? overlay : overlay with
        {
            HandleScale = HandleScale
        };
        Changed?.Invoke();
    }

    internal void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
    {
        if (selectRegion is not null)
        {
            selectRegion(bounds, additive);
        }
        else if (!baseline.RepeatedRois.IsEmpty)
        {
            editor.SelectRegion(bounds, additive);
        }
        else
        {
            var ids = baseline.Snapshot.Spatial.Intersecting(bounds).Where(roi => roi.Geometry.Bounds is var box &&
                bounds.Contains(new(box.Left, box.Top)) && bounds.Contains(new(box.Right, box.Bottom))).Select(roi => roi.Id);
            Select(additive ? baseline.Selection.Union(ids) : ids);
        }
    }

    public void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
    {
        ValidateDraftImage();
        if (!middle && !NavigationOnly && Tool == EditorTool.Draw && DrawShape == RoiShapeKind.Polygon &&
            !(Minimap is { } panel && panel.Panel.Contains(screen)))
        {
            ContinuePolygon(screen);
            return;
        }
        Cancel();
        _activeGesture = StartGesture(screen, modifiers, middle);

        Changed?.Invoke();
    }

    // 폴리곤의 여러 클릭은 하나의 초안이다. 일반 Down의 Cancel로 기존 점을 지우지 않는다.
    private void ContinuePolygon(PointD screen)
    {
        _activeGesture?.Cancel();
        _activeGesture = new NoGesture();
        var imagePoint = RoiImageBounds.Clamp(editor.State.Viewport.ScreenToImage(screen), editor.State.Image);
        if (PolygonClosure.Find(_polygonPoints, imagePoint) is { } closedPolygon)
        {
            CompletePolygon(closedPolygon);
            return;
        }

        if (CanCompletePolygon && (editor.State.Viewport.ImageToScreen(_polygonPoints[0]) - screen).Length < PolygonClosureDistanceDip)
        {
            CompletePolygon();
            return;
        }

        if (_polygonPoints.Count == 0)
        {
            _polygonStartImage = editor.State.Image;
        }

        if (_polygonPoints.Count == 0 || (_polygonPoints[^1] - imagePoint).Length > .000001)
        {
            _polygonPoints.Add(imagePoint);
        }

        ShowPolygonDraft();
    }

    private IGesture StartGesture(PointD screen, InputModifiers modifiers, bool middle)
    {
        // 미니맵 → 화면 이동 → 탐색 전용 → 그리기/마스크 → 선택 순서를 유지한다.
        if (!middle && Minimap is { } map && map.Panel.Contains(screen))
        {
            return map.ImageBounds.Contains(screen) ? new MinimapGesture(editor, map, screen) : new NoGesture();
        }
        else if (middle || Tool == EditorTool.Pan)
        {
            return new PanGesture(editor, screen);
        }
        else if (NavigationOnly)
        {
            return new NoGesture();
        }
        else if (Tool == EditorTool.Draw)
        {
            return new DrawGesture(this, screen);
        }
        else if (Tool == EditorTool.Mask && MaskTargetId is { } maskTarget && editor.State.Rois.TryGetValue(maskTarget, out var owner))
        {
            return new MaskDrawGesture(this, owner, screen);
        }
        else
        {
            return StartSelectionGesture(screen, modifiers);
        }
    }

    public void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        if (Tool != EditorTool.Select || NavigationOnly ||
            (Minimap is { } map && map.Panel.Contains(screen)))
        {
            Down(screen, modifiers);
            return;
        }

        Cancel();
        // 계층 안의 더블클릭은 겹친 리프 → 안쪽 그룹 → 바깥 그룹 순서로 선택한다.
        var hit = HitSelectableRoi(screen, modifiers.HasFlag(InputModifiers.Alt));
        if (!modifiers.HasFlag(InputModifiers.Alt) && hit.Ordinary is { } ordinary &&
            (editor.State.Rois[ordinary].ParentId is not null || editor.State.IsGroupRoi(ordinary)))
        {
            hit = HitSelectableRoi(screen, cycleSelection: true);
        }
        if (hit.Repeated is { } repeated)
        {
            editor.SelectRepeated(repeated);
        }
        else if (hit.Ordinary is { } id)
        {
            Select([id]);
        }
        _activeGesture = new NoGesture();
        Changed?.Invoke();
    }

    private IGesture StartSelectionGesture(PointD screen, InputModifiers modifiers)
    {
        var state = editor.State;
        var cycleSelection = modifiers.HasFlag(InputModifiers.Alt);
        if (!cycleSelection)
        {
            var handleGesture = StartHandleGesture(state, screen);
            if (handleGesture is not null)
            {
                return handleGesture;
            }
            // 화면에서 고른 그룹을 내부에서 잡아도 리프가 선택을 빼앗지 않는다.
            if (modifiers == InputModifiers.None && state.SelectionCount == 1 && state.Selection.Count == 1 &&
                state.Selection.Single() is var selected && state.IsGroupRoi(selected) &&
                state.Rois[selected].Geometry.Contains(state.Viewport.ScreenToImage(screen)))
            {
                return new MoveGesture(this, screen);
            }
        }

        var hit = HitSelectableRoi(screen, cycleSelection);
        if (hit.Repeated is { } repeatedHit)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.SelectRepeated(repeatedHit, additive: true, toggle: true);
                return new NoGesture();
            }
            if (!state.RepeatedSelection.TryGetValue(repeatedHit.BatchId, out var selectedCells) || !selectedCells.Contains(repeatedHit.Index))
            {
                editor.SelectRepeated(repeatedHit, additive: modifiers.HasFlag(InputModifiers.Shift));
            }

            return new MoveGesture(this, screen);
        }
        if (hit.Ordinary is { } roiId)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.Select(state.Selection.Contains(roiId) ? state.Selection.Remove(roiId) : state.Selection.Add(roiId), preserveRepeated: true);
                return new NoGesture();
            }
            if (!state.Selection.Contains(roiId))
            {
                editor.Select(modifiers.HasFlag(InputModifiers.Shift) ? state.Selection.Add(roiId) : [roiId], preserveRepeated: modifiers.HasFlag(InputModifiers.Shift));
            }

            return new MoveGesture(this, screen);
        }
        return new MarqueeGesture(this, screen, modifiers != InputModifiers.None);
    }

    private IGesture? StartHandleGesture(DocumentState state, PointD screen)
    {
        // 회전/크기 핸들은 단일 ROI 또는 단일 그룹을 선택했을 때만 제공한다.
        if (state.SingleRepeatedCell is { } cell)
        {
            var cellGeometry = state.RepeatedRois[cell.BatchId].Geometry(cell.Index);
            if ((HandleGeometry.RotationHandle(cellGeometry, state.Viewport, HandleScale) - screen).Length < HandleGeometry.RotationHitRadiusDip * HandleScale)
            {
                return new RotateGesture(this, screen, cellGeometry.Center);
            }

            var handles = HandleGeometry.ResizeHandles(cellGeometry, state.Viewport);
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - screen).Length < HandleGeometry.ResizeHitRadiusDip * HandleScale)
                {
                    return new RepeatedResizeGesture(this, cell, cellGeometry, i, screen);
                }
            }
        }
        else if (state.SelectionCount == 1)
        {
            var selectedRoi = state.Rois[state.Selection.Single()];
            if ((HandleGeometry.RotationHandle(selectedRoi.Geometry, state.Viewport, HandleScale) - screen).Length < HandleGeometry.RotationHitRadiusDip * HandleScale)
            {
                return new RotateGesture(this, screen, selectedRoi.Geometry.Center);
            }

            var handles = HandleGeometry.ResizeHandles(selectedRoi.Geometry, state.Viewport);
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - screen).Length < HandleGeometry.ResizeHitRadiusDip * HandleScale)
                {
                    return new ResizeGesture(this, selectedRoi, i, screen);
                }
            }
        }
        return null;
    }

    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        ValidateDraftImage();
        if (_activeGesture is { } active)
        {
            active.Move(screen, modifiers);
        }
        else if (_polygonPoints.Count > 0)
        {
            ShowPolygonDraft(RoiImageBounds.Clamp(editor.State.Viewport.ScreenToImage(screen), editor.State.Image));
        }
        else if (!NavigationOnly && Tool != EditorTool.Mask)
        {
            var hit = HitOrdinaryRoi(screen);
            if (Overlay.Hover != hit)
            {
                Show(Overlay with
                {
                    Hover = hit
                });
            }
        }
    }

    public void Up(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        var active = _activeGesture;
        // 확정 콜백에서 새 입력이 들어와도 방금 끝난 드래그를 다시 사용하지 않는다.
        _activeGesture = null;
        try
        {
            active?.Move(screen, modifiers);
            active?.Complete();
        }
        catch
        {
            active?.Cancel();
            throw;
        }
        finally
        {
            if (_polygonPoints.Count > 0)
            {
                ShowPolygonDraft();
            }
            else
            {
                Show(new());
            }
        }
    }

    public void Cancel()
    {
        var active = _activeGesture;
        _activeGesture = null;
        active?.Cancel();
        _polygonPoints.Clear();
        _polygonStartImage = null;
        Show(new());
    }

    public void ValidateDraftImage()
    {
        if (Tool == EditorTool.Mask && (MaskTargetId is not { } id || !editor.State.Selection.Contains(id) ||
            editor.State.SelectionCount != 1 || !editor.State.Rois.TryGetValue(id, out var owner) ||
            editor.State.IsGroupRoi(id) || owner.Masks.Length >= EditorSession.MaximumMasksPerRoi))
        {
            Tool = EditorTool.Select;
        }
        if (_polygonPoints.Count > 0 && _polygonStartImage != editor.State.Image)
        {
            Cancel();
        }
    }

    public void CompletePolygon()
    {
        ValidateDraftImage();
        if (!CanCompletePolygon)
        {
            return;
        }

        CompletePolygon(PolygonClosure.Find(_polygonPoints, _polygonPoints[0]) ?? _polygonPoints.ToArray());
    }

    private void CompletePolygon(IEnumerable<PointD> points)
    {
        var geometry = RoiGeometry.Polygon(points);
        AddDrawnRoi(geometry);
        Tool = EditorTool.Select;
    }

    public void RemovePolygonPoint()
    {
        if (_polygonPoints.Count > 0)
        {
            _polygonPoints.RemoveAt(_polygonPoints.Count - 1);
        }

        ShowPolygonDraft();
    }

    private void ShowPolygonDraft(PointD? cursor = null)
    {
        if (cursor is { } point && PolygonClosure.Find(_polygonPoints, point) is { Length: >= 3 } closed)
        {
            Show(new(PolygonPoints: closed.Append(closed[0]).ToImmutableArray())
            {
                DraftIsGroup = DrawAsGroup
            });
            return;
        }

        Show(new(PolygonPoints: (cursor is { } end ? _polygonPoints.Append(end) : _polygonPoints).ToImmutableArray())
        {
            DraftIsGroup = DrawAsGroup
        });
    }

    public void Wheel(PointD screen, double notches)
    {
        if (_activeGesture is not null)
        {
            return;
        }

        var anchor = Minimap is { } map && map.Panel.Contains(screen) ? new PointD(_viewportWidthDip / 2, _viewportHeightDip / 2) : screen;
        editor.SetViewport(editor.State.Viewport.ZoomAt(anchor, Math.Pow(1.2, notches)));
    }

    private Guid? HitOrdinaryRoi(PointD screen, bool cycleSelection = false)
    {
        if (Minimap is { } map && map.Panel.Contains(screen))
        {
            return null;
        }

        var imagePoint = editor.State.Viewport.ScreenToImage(screen);
        var hitToleranceInImage = RoiHitToleranceDip / editor.State.Viewport.Scale;
        if (!cycleSelection)
        {
            return editor.State.Snapshot.Spatial.HitTest(imagePoint, hitToleranceInImage)?.Id;
        }

        var searchMarginInImage = hitToleranceInImage * Math.Sqrt(2);
        var hitRoiIds = editor.State.Snapshot.Spatial.Intersecting(new(
                imagePoint.X - searchMarginInImage, imagePoint.Y - searchMarginInImage,
                imagePoint.X + searchMarginInImage, imagePoint.Y + searchMarginInImage))
            .Where(r => r.Geometry.Contains(imagePoint, hitToleranceInImage))
            .OrderBy(r => r.Geometry.Area).ThenByDescending(r => r.Order).Select(r => r.Id).ToArray();
        if (hitRoiIds.Length == 0)
        {
            return null;
        }

        if (cycleSelection && editor.State.Selection.Count == 1)
        {
            var selectedIndex = Array.IndexOf(hitRoiIds, editor.State.Selection.Single());
            return hitRoiIds[(selectedIndex + 1) % hitRoiIds.Length];
        }
        return hitRoiIds[0];
    }

    internal static BoundsD SelectionBounds(DocumentState state)
        => state.RepeatedSelection.IsEmpty ? state.SelectedSpatial.Bounds : state.TransformSelection.Bounds;

    private (Guid? Ordinary, RepeatedRoiCell? Repeated) HitSelectableRoi(PointD screen, bool cycleSelection)
    {
        var state = editor.State;
        if (state.RepeatedRois.IsEmpty)
        {
            return (HitOrdinaryRoi(screen, cycleSelection), null);
        }

        var imagePoint = state.Viewport.ScreenToImage(screen);
        var hitToleranceInImage = RoiHitToleranceDip / state.Viewport.Scale;
        var searchMarginInImage = hitToleranceInImage * Math.Sqrt(2);
        var candidates = new List<(Guid? Ordinary, RepeatedRoiCell? Repeated, double Area, long Order)>();
        if (cycleSelection)
        {
            foreach (var roi in state.Snapshot.Spatial.Intersecting(new(
                imagePoint.X - searchMarginInImage, imagePoint.Y - searchMarginInImage,
                imagePoint.X + searchMarginInImage, imagePoint.Y + searchMarginInImage)))
            {
                if (roi.Geometry.Contains(imagePoint, hitToleranceInImage))
                {
                    candidates.Add((roi.Id, null, roi.Geometry.Area, roi.Order));
                }
            }
        }
        else if (state.Snapshot.Spatial.HitTest(imagePoint, hitToleranceInImage) is { } ordinaryRoi)
        {
            candidates.Add((ordinaryRoi.Id, null, ordinaryRoi.Geometry.Area, ordinaryRoi.Order));
        }
        foreach (var batch in state.RepeatedRois.Values)
        {
            if (!cycleSelection)
            {
                if (batch.HitTest(imagePoint, hitToleranceInImage) is { } cellIndex)
                {
                    candidates.Add((null, new(batch.Id, cellIndex), batch.Geometry(cellIndex).Area, batch.Order));
                }
                continue;
            }
            foreach (var part in batch.Parts)
            {
                foreach (var index in part.HitCandidates(imagePoint, hitToleranceInImage))
                {
                    candidates.Add((null, new(batch.Id, index), part.Grid.Geometry(index).Area, batch.Order));
                }
            }
        }
        if (candidates.Count == 0)
        {
            return default;
        }
        candidates = candidates.Distinct().OrderBy(hit => hit.Area).ThenByDescending(hit => hit.Order)
            .ThenByDescending(hit => hit.Repeated?.Index).ToList();
        var selectedCandidateIndex = -1;
        if (cycleSelection && state.SelectionCount == 1)
        {
            selectedCandidateIndex = candidates.FindIndex(hit =>
            {
                if (hit.Repeated is { } cell)
                {
                    return state.SingleRepeatedCell == cell;
                }

                return hit.Ordinary is { } id && state.Selection.Contains(id);
            });
        }

        var selectedHit = candidates[(selectedCandidateIndex + 1) % candidates.Count];
        return (selectedHit.Ordinary, selectedHit.Repeated);
    }
}
