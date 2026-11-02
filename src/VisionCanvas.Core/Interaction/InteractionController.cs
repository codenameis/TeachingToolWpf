using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>입력에 맞는 제스처를 선택하고 이동·확정·취소를 전달한다. 문서 변경과 이력은 EditorSession이 담당한다.</summary>
public sealed class InteractionController : IViewportInput
{
    private readonly EditorSession _editor;
    private readonly IEditorCommandSink _commandSink;
    private readonly PolygonDraftState _polygonDraft = new();
    private IGesture? _activeGesture;
    private bool _pointerDown;
    private EditorTool _tool;
    private RoiShapeKind _drawShape;
    private Guid? _drawParentId;
    private bool _drawAsGroup;
    private double _handleScale = HandleGeometry.DefaultScale;
    private bool _isEnabled = true;

    public InteractionController(EditorSession editor)
        : this(editor, new EditorSessionCommandSink(editor))
    {
    }

    public InteractionController(EditorSession editor, IEditorCommandSink commandSink)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(commandSink);
        _editor = editor;
        _commandSink = commandSink;
    }

    public InteractionController(
        EditorSession editor,
        Action<string>? commitPreview,
        Action<IEnumerable<Guid>>? selectRois = null,
        Action<PointD, bool>? previewTranslation = null,
        Action<PointD, double>? previewRotation = null,
        Action<BoundsD, bool>? selectRegion = null)
        : this(
            editor,
            new DelegatingEditorCommandSink(editor, commitPreview, selectRois, previewTranslation, previewRotation, selectRegion))
    {
    }

    /// <summary>검사 화면의 사용자 입력을 차단한다. 진행 중인 편집은 취소하고 영상/문서 갱신은 허용한다.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            Cancel();
        }
    }

    /// <summary>원본 픽셀 단위의 이동. 기존 부모/이미지 경계와 한 번의 Undo를 사용한다.</summary>
    public bool NudgeSelection(PointD delta)
    {
        if (!IsEnabled || NavigationOnly || IsDragging || _editor.IsPreviewing ||
            _polygonDraft.HasPoints || _editor.State.SelectionCount == 0)
        {
            return false;
        }

        _editor.TranslateSelection(delta);
        return true;
    }

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

    public Guid? MaskTargetId { get; private set; }

    public MaskShape MaskShape { get; private set; }

    public void StartMask(MaskShape shape)
    {
        if (!IsEnabled || NavigationOnly || _editor.IsPreviewing || _editor.State.SelectionCount != 1)
        {
            return;
        }

        var id = _editor.State.Selection.Single();
        if (_editor.State.IsGroupRoi(id) || _editor.State.Rois[id].Masks.Length >= EditorSession.MaximumMasksPerRoi)
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
            _editor.AddParent(geometry);
        }
        else if (DrawParentId is { } parentId)
        {
            if (!_editor.State.Rois.TryGetValue(parentId, out var parent))
            {
                throw new ArgumentException("자식 ROI를 추가할 부모가 없습니다.");
            }

            if (!parent.Geometry.Contains(geometry))
            {
                throw new ArgumentException("자식 ROI는 부모 도형 안에 완전히 들어오도록 그리세요. 경계에 걸칠 수 없습니다.");
            }

            _editor.AddChild(parentId, geometry);
        }
        else
        {
            _editor.Add(geometry);
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

    public int PolygonPointCount => _polygonDraft.Count;

    public bool CanCompletePolygon => _polygonDraft.CanComplete;

    private double _viewportWidthDip;
    private double _viewportHeightDip;
    private MinimapOptions _minimapOptions = MinimapOptions.Default;

    public MinimapOptions MinimapOptions
    {
        get => _minimapOptions;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            if (_minimapOptions == value)
            {
                return;
            }

            if (_activeGesture is MinimapGesture)
            {
                Cancel();
            }

            _minimapOptions = value;
            Changed?.Invoke();
        }
    }

    public MinimapLayout? Minimap
        => _editor.State.Image is { } image
            ? MinimapLayout.Create(image.Width, image.Height, _viewportWidthDip, _viewportHeightDip, MinimapOptions)
            : null;

    public void SetViewportSize(double width, double height)
    {
        if (width == _viewportWidthDip && height == _viewportHeightDip)
        {
            return;
        }

        _viewportWidthDip = width;
        _viewportHeightDip = height;
        if (_activeGesture is MinimapGesture || _polygonDraft.HasPoints)
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

    public bool IsDragging => _pointerDown && _activeGesture is not null;

    public bool HasActiveState => IsDragging || _polygonDraft.HasPoints || Overlay.Hover is not null;

    public bool NavigationOnly
    {
        get;
        set;
    }

    public event Action? Changed;

    internal EditorSession Editor => _editor;

    internal void PreviewTranslation(PointD delta, bool allowSlide)
        => _commandSink.PreviewTranslation(delta, allowSlide);

    internal void PreviewRotation(PointD pivot, double degrees)
        => _commandSink.PreviewRotation(pivot, degrees);

    internal void CommitPreview(string name)
        => _commandSink.CommitPreview(name);

    internal void Select(IEnumerable<Guid> ids)
        => _commandSink.Select(ids);

    internal void Show(InteractionOverlay overlay)
    {
        if (overlay.HandleScale != HandleScale)
        {
            overlay = overlay with
            {
                HandleScale = HandleScale
            };
        }

        Overlay = overlay;
        Changed?.Invoke();
    }

    internal void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
        => _commandSink.SelectRegion(baseline, bounds, additive);

    public void Down(PointD screen, InputModifiers modifiers, PointerButton button)
    {
        if (!IsEnabled)
        {
            return;
        }

        ValidateDraftImage();
        var isMiddleButton = button == PointerButton.Middle;
        if (IsPolygonDrawClick(screen, isMiddleButton))
        {
            StartOrContinuePolygon(screen);
            return;
        }

        Cancel();
        _pointerDown = true;
        _activeGesture = StartGesture(screen, modifiers, isMiddleButton);
        Changed?.Invoke();
    }

    public void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
        => Down(screen, modifiers, middle ? PointerButton.Middle : PointerButton.Left);

    private bool IsPolygonDrawClick(PointD screen, bool middle)
        => !middle
        && !NavigationOnly
        && Tool == EditorTool.Draw
        && DrawShape == RoiShapeKind.Polygon
        && !OverMinimap(screen);

    // 폴리곤의 여러 클릭은 하나의 PolygonDrawGesture가 이어받는다.
    private void StartOrContinuePolygon(PointD screen)
    {
        if (_activeGesture is not PolygonDrawGesture polygonGesture || polygonGesture.IsFinished)
        {
            _activeGesture?.Cancel();
            polygonGesture = new PolygonDrawGesture(this, _polygonDraft);
            _activeGesture = polygonGesture;
        }

        _pointerDown = true;
        polygonGesture.AddClick(screen);
        if (polygonGesture.IsFinished)
        {
            _activeGesture = null;
        }

        Changed?.Invoke();
    }

    private IGesture StartGesture(PointD screen, InputModifiers modifiers, bool middle)
    {
        // 미니맵 → 화면 이동 → 탐색 전용 → 그리기/마스크 → 선택 순서를 유지한다.
        if (!middle && Minimap is { } map && map.Panel.Contains(screen))
        {
            return map.ImageBounds.Contains(screen) ? new MinimapGesture(_editor, map, screen) : new NoGesture();
        }

        if (middle || Tool == EditorTool.Pan)
        {
            return new PanGesture(_editor, screen);
        }

        if (NavigationOnly)
        {
            return new NoGesture();
        }

        if (Tool == EditorTool.Draw)
        {
            return new DrawGesture(this, screen);
        }

        if (Tool == EditorTool.Mask && MaskTargetId is { } maskTarget && _editor.State.Rois.TryGetValue(maskTarget, out var owner))
        {
            return new MaskDrawGesture(this, owner, screen);
        }

        return StartSelectionGesture(screen, modifiers);
    }

    public void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (Tool != EditorTool.Select || NavigationOnly || OverMinimap(screen))
        {
            Down(screen, modifiers);
            return;
        }

        Cancel();
        // 계층 안의 더블클릭은 겹친 리프 → 안쪽 그룹 → 바깥 그룹 순서로 선택한다.
        var hit = HitOrdinaryRoi(screen, modifiers.HasFlag(InputModifiers.Alt));
        if (!modifiers.HasFlag(InputModifiers.Alt) && hit is { } ordinary &&
            (_editor.State.Rois[ordinary].ParentId is not null || _editor.State.IsGroupRoi(ordinary)))
        {
            hit = HitOrdinaryRoi(screen, cycleSelection: true);
        }

        if (!modifiers.HasFlag(InputModifiers.Alt) &&
            _editor.State.SelectionCount == 1 &&
            _editor.State.Selection.TryGetOnly(out var selectedId) &&
            (hit is null || hit == selectedId) &&
            StartHandleGesture(_editor.State, screen) is { } handleGesture)
        {
            _pointerDown = true;
            _activeGesture = handleGesture;
            Changed?.Invoke();
            return;
        }

        if (hit is { } id)
        {
            Select([id]);
        }

        _pointerDown = true;
        _activeGesture = new NoGesture();
        Changed?.Invoke();
    }

    private IGesture StartSelectionGesture(PointD screen, InputModifiers modifiers)
    {
        var state = _editor.State;
        var cycleSelection = modifiers.HasFlag(InputModifiers.Alt);
        if (!cycleSelection && TryStartActiveSelectionGesture(state, screen, modifiers) is { } activeGesture)
        {
            return activeGesture;
        }

        if (HitOrdinaryRoi(screen, cycleSelection) is { } roiId)
        {
            return StartOrdinarySelectionGesture(state, roiId, screen, modifiers);
        }

        return new MarqueeGesture(this, screen, modifiers != InputModifiers.None);
    }

    private IGesture? TryStartActiveSelectionGesture(DocumentState state, PointD screen, InputModifiers modifiers)
    {
        var handleGesture = StartHandleGesture(state, screen);
        if (handleGesture is not null)
        {
            return handleGesture;
        }

        // 화면에서 고른 그룹을 내부에서 잡아도 리프가 선택을 빼앗지 않는다.
        if (modifiers == InputModifiers.None && state.SelectionCount == 1 && state.Selection.TryGetOnly(out var selectedId) &&
            state.IsGroupRoi(selectedId) &&
            state.Rois[selectedId].Geometry.Contains(state.Viewport.ScreenToImage(screen)))
        {
            return new MoveGesture(this, screen);
        }

        return null;
    }

    private IGesture StartOrdinarySelectionGesture(DocumentState state, Guid roiId, PointD screen, InputModifiers modifiers)
    {
        if (modifiers.HasFlag(InputModifiers.Control))
        {
            var nextSelection = state.Selection.Contains(roiId)
                ? state.Selection.Remove(roiId)
                : state.Selection.Add(roiId);
            _editor.Select(nextSelection);
            return new NoGesture();
        }

        if (!state.Selection.Contains(roiId))
        {
            var nextSelection = modifiers.HasFlag(InputModifiers.Shift)
                ? state.Selection.Add(roiId)
                : [roiId];
            _editor.Select(nextSelection);
        }

        return new MoveGesture(this, screen);
    }

    private IGesture? StartHandleGesture(DocumentState state, PointD screen)
    {
        if (state.SelectionCount != 1)
        {
            return null;
        }

        var selectedRoi = state.Rois[state.Selection.Single()];
        if (HitsRotationHandle(state.Viewport, selectedRoi.Geometry, screen))
        {
            return new RotateGesture(this, screen, selectedRoi.Geometry.Center);
        }

        var resizeHandle = HitResizeHandle(state.Viewport, selectedRoi.Geometry, screen);
        return resizeHandle < 0 ? null : new ResizeGesture(this, selectedRoi, resizeHandle, screen);
    }

    /// <summary>화면 좌표가 회전 핸들 안인지 확인한다. 허용 반경은 <see cref="HandleScale"/>에 비례한다.</summary>
    private bool HitsRotationHandle(ViewportTransform viewport, RoiGeometry geometry, PointD screen)
        => (HandleGeometry.RotationHandle(geometry, viewport, HandleScale) - screen).Length
            < HandleGeometry.RotationHitRadiusDip * HandleScale;

    /// <summary>화면 좌표가 몇 번째 크기 핸들 안인지 반환한다. 어느 핸들도 아니면 -1.</summary>
    private int HitResizeHandle(ViewportTransform viewport, RoiGeometry geometry, PointD screen)
    {
        Span<PointD> handles = stackalloc PointD[8];
        HandleGeometry.CopyResizeHandlesTo(geometry, viewport, handles);
        for (var i = 0; i < handles.Length; i++)
        {
            if ((handles[i] - screen).Length < HandleGeometry.ResizeHitRadiusDip * HandleScale)
            {
                return i;
            }
        }

        return -1;
    }

    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        if (!IsEnabled)
        {
            return;
        }

        ValidateDraftImage();
        if (_activeGesture is { } active)
        {
            active.Move(screen, modifiers);
            return;
        }

        if (!NavigationOnly && Tool != EditorTool.Mask)
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
        if (!IsEnabled)
        {
            return;
        }

        _pointerDown = false;
        if (_activeGesture is PolygonDrawGesture polygonGesture)
        {
            if (polygonGesture.IsFinished)
            {
                _activeGesture = null;
                Show(new());
            }
            else
            {
                polygonGesture.Complete();
            }

            return;
        }

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
            Show(new());
        }
    }

    public void Cancel()
    {
        _pointerDown = false;
        var active = _activeGesture;
        _activeGesture = null;
        active?.Cancel();
        _polygonDraft.Clear();
        Show(new());
    }

    public void ValidateDraftImage()
    {
        if (Tool == EditorTool.Mask && (MaskTargetId is not { } id || !_editor.State.Selection.Contains(id) ||
            _editor.State.SelectionCount != 1 || !_editor.State.Rois.TryGetValue(id, out var owner) ||
            _editor.State.IsGroupRoi(id) || owner.Masks.Length >= EditorSession.MaximumMasksPerRoi))
        {
            Tool = EditorTool.Select;
        }

        if (_polygonDraft.IsImageChanged(_editor.State.Image))
        {
            Cancel();
        }
    }

    public void CompletePolygon()
    {
        if (!IsEnabled)
        {
            return;
        }

        ValidateDraftImage();
        if (_activeGesture is PolygonDrawGesture polygonGesture)
        {
            polygonGesture.CompleteDraft();
            if (polygonGesture.IsFinished)
            {
                _activeGesture = null;
            }
        }
    }

    public void RemovePolygonPoint()
    {
        if (!IsEnabled)
        {
            return;
        }

        if (_activeGesture is PolygonDrawGesture polygonGesture)
        {
            polygonGesture.RemoveLastPoint();
            if (polygonGesture.IsFinished)
            {
                _activeGesture = null;
            }
        }
    }

    public void Wheel(PointD screen, double notches)
    {
        if (!IsEnabled || IsDragging)
        {
            return;
        }

        var anchor = OverMinimap(screen) ? new PointD(_viewportWidthDip / 2, _viewportHeightDip / 2) : screen;
        _editor.SetViewport(_editor.State.Viewport.ZoomAt(anchor, Math.Pow(1.2, notches)));
    }

    private Guid? HitOrdinaryRoi(PointD screen, bool cycleSelection = false)
        => new RoiHitTester(_editor.State, OverMinimap(screen)).HitOrdinary(screen, cycleSelection);

    internal static BoundsD SelectionBounds(DocumentState state)
        => state.SelectedSpatial.Bounds;

    /// <summary>포인터가 미니맵 패널 위인지 확인한다. 미니맵 드래그가 ROI 선택보다 우선한다.</summary>
    private bool OverMinimap(PointD screen) => Minimap is { } map && map.Panel.Contains(screen);
}
