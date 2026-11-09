using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TeachingToolWpf.ViewModels;
using VisionGeometry;

namespace TeachingToolWpf.Views;

public partial class PatternMaskWindow : Window
{
    private static readonly SolidColorBrush MaskFillBrush = CreateFrozenBrush(Color.FromArgb(96, 224, 51, 96));
    private static readonly SolidColorBrush SelectedMaskFillBrush = CreateFrozenBrush(Color.FromArgb(135, 224, 51, 96));
    private static readonly SolidColorBrush MaskStrokeBrush = CreateFrozenBrush(Color.FromRgb(244, 63, 94));
    private static readonly SolidColorBrush SelectedMaskStrokeBrush = CreateFrozenBrush(Color.FromRgb(251, 191, 36));
    private static readonly SolidColorBrush ActiveFeatureBrush = CreateFrozenBrush(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush ExcludedFeatureBrush = CreateFrozenBrush(Color.FromArgb(195, 239, 68, 68));
    private static readonly SolidColorBrush DraftPreviewStrokeBrush = CreateFrozenBrush(Color.FromRgb(56, 189, 248));
    private static readonly SolidColorBrush DraftPreviewFillBrush = CreateFrozenBrush(Color.FromArgb(55, 56, 189, 248));
    private static readonly SolidColorBrush PolygonVertexFillBrush = CreateFrozenBrush(Color.FromRgb(255, 255, 255));
    private static readonly SolidColorBrush PolygonStartSnapBrush = CreateFrozenBrush(Color.FromArgb(95, 251, 191, 36));

    private static readonly Pen MaskStrokePen = CreateFrozenPen(MaskStrokeBrush, 1.25);
    private static readonly Pen SelectedMaskStrokePen = CreateFrozenDashedPen(SelectedMaskStrokeBrush, 2.0, [4.0, 2.0]);
    private static readonly Pen DraftPreviewPen = CreateFrozenDashedPen(DraftPreviewStrokeBrush, 1.5, [3.0, 2.0]);
    private static readonly Pen PolygonSolidEdgePen = CreateFrozenPen(DraftPreviewStrokeBrush, 1.6);
    private static readonly Pen PolygonVertexPen = CreateFrozenPen(DraftPreviewStrokeBrush, 1.5);
    private static readonly Pen PolygonStartSnapPen = CreateFrozenPen(SelectedMaskStrokeBrush, 2.0);
    private static readonly Pen BrushCursorPen = CreateFrozenPen(DraftPreviewStrokeBrush, 1.5);
    private static readonly Pen EraserCursorPen = CreateFrozenPen(SelectedMaskStrokeBrush, 1.5);

    private readonly OverlayVisualHost _overlayHost;
    private PatternMaskEditorViewModel? _viewModel;
    private bool _isDragging;
    private bool _hasHoverPoint;
    private Point _hoverLocal;
    private Point _dragStartLocal;
    private Point _lastDragLocal;
    private Point _currentDragLocal;

    public PatternMaskWindow()
    {
        InitializeComponent();
        _overlayHost = new OverlayVisualHost(DrawOverlayIntoContext);
        OverlayCanvas.Children.Add(_overlayHost);
        DataContextChanged += OnDataContextChanged;
        Closed += OnWindowClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.VisualStateChanged -= RenderOverlay;
        }

        _viewModel = e.NewValue as PatternMaskEditorViewModel;
        if (_viewModel is not null)
        {
            _viewModel.VisualStateChanged += RenderOverlay;
            RenderOverlay();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.VisualStateChanged -= RenderOverlay;
            _viewModel = null;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCanvasPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        if (e.Delta > 0)
        {
            _viewModel.ZoomInCommand.Execute(null);
        }
        else if (e.Delta < 0)
        {
            _viewModel.ZoomOutCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || _viewModel.ActiveTool != PatternMaskDrawTool.Polygon || !_viewModel.HasDraftPolygon)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.TryCompleteDraftPolygon();
                RenderOverlay();
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.CancelDraftPolygon();
                RenderOverlay();
                e.Handled = true;
                break;
            case Key.Back:
                _viewModel.UndoLastDraftPolygonPoint();
                RenderOverlay();
                e.Handled = true;
                break;
        }
    }

    private void OnSurfaceMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasTargetRoi || _viewModel.ActiveTool != PatternMaskDrawTool.Polygon)
        {
            return;
        }

        if (_viewModel.DraftPolygonPoints.Count >= 3)
        {
            _viewModel.TryCompleteDraftPolygon();
        }
        else if (_viewModel.HasDraftPolygon)
        {
            _viewModel.CancelDraftPolygon();
        }

        RenderOverlay();
        e.Handled = true;
    }

    private void OnSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasTargetRoi)
        {
            return;
        }

        var localPt = ToRoiLocalPixel(e.GetPosition(PatternSurface));
        _hoverLocal = localPt;
        _hasHoverPoint = true;

        if (_viewModel.ActiveTool == PatternMaskDrawTool.Polygon)
        {
            double snapRadius = Math.Max(3.5, 9.0 / Math.Max(0.25, _viewModel.ZoomScale));
            if ((e.ClickCount >= 2 && _viewModel.DraftPolygonPoints.Count >= 3) ||
                _viewModel.IsNearDraftPolygonStart(localPt.X, localPt.Y, snapRadius))
            {
                _viewModel.TryCompleteDraftPolygon();
            }
            else
            {
                _viewModel.AddDraftPolygonPoint(localPt.X, localPt.Y);
            }

            RenderOverlay();
            e.Handled = true;
            return;
        }

        _dragStartLocal = localPt;
        _lastDragLocal = localPt;
        _currentDragLocal = localPt;
        _isDragging = true;
        PatternSurface.CaptureMouse();

        switch (_viewModel.ActiveTool)
        {
            case PatternMaskDrawTool.Select:
            {
                var hitIndex = _viewModel.HitTestMaskAtLocalPixel(localPt.X, localPt.Y);
                _viewModel.SelectedIndex = hitIndex;
                break;
            }
            case PatternMaskDrawTool.Brush:
                _viewModel.BeginInteractiveStroke();
                _viewModel.TryPaintBrushStrokeSegment(localPt.X, localPt.Y, localPt.X, localPt.Y);
                break;
            case PatternMaskDrawTool.Eraser:
                _viewModel.BeginInteractiveStroke();
                _viewModel.TryEraseMasksAlongSegment(localPt.X, localPt.Y, localPt.X, localPt.Y);
                break;
        }

        RenderOverlay();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasTargetRoi)
        {
            return;
        }

        var localPt = ToRoiLocalPixel(e.GetPosition(PatternSurface));
        _hoverLocal = localPt;
        _hasHoverPoint = true;

        if (!_isDragging)
        {
            if (_viewModel.ActiveTool is PatternMaskDrawTool.Brush or PatternMaskDrawTool.Eraser or PatternMaskDrawTool.Polygon)
            {
                RenderOverlay();
            }

            return;
        }

        _currentDragLocal = localPt;

        switch (_viewModel.ActiveTool)
        {
            case PatternMaskDrawTool.Select:
            {
                if (_viewModel.HasSelectedMask)
                {
                    var dx = localPt.X - _lastDragLocal.X;
                    var dy = localPt.Y - _lastDragLocal.Y;
                    if (_viewModel.TryTranslateSelectedMaskByLocalPixels(dx, dy))
                    {
                        _lastDragLocal = localPt;
                    }
                }
                break;
            }
            case PatternMaskDrawTool.Brush:
                _viewModel.TryPaintBrushStrokeSegment(_lastDragLocal.X, _lastDragLocal.Y, localPt.X, localPt.Y);
                _lastDragLocal = localPt;
                RenderOverlay();
                break;
            case PatternMaskDrawTool.Eraser:
                _viewModel.TryEraseMasksAlongSegment(_lastDragLocal.X, _lastDragLocal.Y, localPt.X, localPt.Y);
                _lastDragLocal = localPt;
                RenderOverlay();
                break;
            case PatternMaskDrawTool.Rectangle:
            case PatternMaskDrawTool.Ellipse:
                RenderOverlay();
                break;
        }
    }

    private void OnSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _viewModel is null)
        {
            return;
        }

        var endLocal = ToRoiLocalPixel(e.GetPosition(PatternSurface));
        _hoverLocal = endLocal;
        _isDragging = false;
        PatternSurface.ReleaseMouseCapture();

        switch (_viewModel.ActiveTool)
        {
            case PatternMaskDrawTool.Rectangle:
                _viewModel.TryAddMaskFromLocalPixels(
                    MaskShape.Rectangle,
                    _dragStartLocal.X,
                    _dragStartLocal.Y,
                    endLocal.X,
                    endLocal.Y);
                break;
            case PatternMaskDrawTool.Ellipse:
                _viewModel.TryAddMaskFromLocalPixels(
                    MaskShape.Ellipse,
                    _dragStartLocal.X,
                    _dragStartLocal.Y,
                    endLocal.X,
                    endLocal.Y);
                break;
            case PatternMaskDrawTool.Brush:
                _viewModel.TryPaintBrushStrokeSegment(_lastDragLocal.X, _lastDragLocal.Y, endLocal.X, endLocal.Y);
                _viewModel.EndInteractiveStroke();
                break;
            case PatternMaskDrawTool.Eraser:
                _viewModel.TryEraseMasksAlongSegment(_lastDragLocal.X, _lastDragLocal.Y, endLocal.X, endLocal.Y);
                _viewModel.EndInteractiveStroke();
                break;
        }

        RenderOverlay();
        e.Handled = true;
    }

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging && _hasHoverPoint)
        {
            _hasHoverPoint = false;
            RenderOverlay();
        }
    }

    private Point ToRoiLocalPixel(Point canvasPoint)
    {
        if (_viewModel is null)
        {
            return new Point(0, 0);
        }

        var scale = Math.Max(0.1, _viewModel.ZoomScale);
        var x = Math.Clamp(canvasPoint.X / scale, 0.0, _viewModel.RoiWidth);
        var y = Math.Clamp(canvasPoint.Y / scale, 0.0, _viewModel.RoiHeight);
        return new Point(x, y);
    }

    /// <summary>
    /// UIElement 할당이나 Layout(Measure/Arrange) 없이 단일 DrawingContext 렌더 패스를 VSync에 맞춰 예약한다.
    /// </summary>
    private void RenderOverlay()
    {
        _overlayHost.InvalidateVisual();
    }

    private void DrawOverlayIntoContext(DrawingContext dc)
    {
        if (_viewModel is null || !_viewModel.HasTargetRoi)
        {
            return;
        }

        var zoom = _viewModel.ZoomScale;
        var roiW = _viewModel.RoiWidth;
        var roiH = _viewModel.RoiHeight;

        if (_viewModel.ShowExcludedOverlay)
        {
            // Vector masks (Rectangle / Ellipse) are drawn with crisp vector geometry;
            // Freehand brush masks are already rendered seamlessly via BrushMaskImage WriteableBitmap.
            var vectorMasks = _viewModel.VectorMasks;
            var selectedIdx = _viewModel.SelectedIndex;

            for (var i = 0; i < vectorMasks.Count; i++)
            {
                DrawMaskRegionGeometry(dc, vectorMasks[i], isSelected: i == selectedIdx, roiW, roiH, zoom);
            }

            // If the user explicitly selected a brush-compiled region in the right-hand ListBox, highlight its bounds
            var allMasks = _viewModel.DraftMasks;
            if (selectedIdx >= vectorMasks.Count && selectedIdx < allMasks.Count)
            {
                DrawMaskRegionGeometry(dc, allMasks[selectedIdx], isSelected: true, roiW, roiH, zoom);
            }
        }

        if (_viewModel.ShowEdgeFeatures)
        {
            var features = _viewModel.FeaturePoints;
            var radius = Math.Clamp(zoom * 0.58, 1.1, 2.5);

            for (var i = 0; i < features.Length; i++)
            {
                var pt = features[i];
                var center = new Point(pt.LocalX * zoom, pt.LocalY * zoom);
                dc.DrawEllipse(
                    pt.IsExcluded ? ExcludedFeatureBrush : ActiveFeatureBrush,
                    null,
                    center,
                    radius,
                    radius);
            }
        }

        if (_isDragging && (_viewModel.ActiveTool is PatternMaskDrawTool.Rectangle or PatternMaskDrawTool.Ellipse))
        {
            var x0 = Math.Min(_dragStartLocal.X, _currentDragLocal.X) * zoom;
            var y0 = Math.Min(_dragStartLocal.Y, _currentDragLocal.Y) * zoom;
            var w = Math.Max(1.0, Math.Abs(_currentDragLocal.X - _dragStartLocal.X) * zoom);
            var h = Math.Max(1.0, Math.Abs(_currentDragLocal.Y - _dragStartLocal.Y) * zoom);
            var rect = new Rect(x0, y0, w, h);

            if (_viewModel.ActiveTool == PatternMaskDrawTool.Rectangle)
            {
                dc.DrawRectangle(DraftPreviewFillBrush, DraftPreviewPen, rect);
            }
            else
            {
                dc.DrawEllipse(
                    DraftPreviewFillBrush,
                    DraftPreviewPen,
                    new Point(x0 + (w * 0.5), y0 + (h * 0.5)),
                    w * 0.5,
                    h * 0.5);
            }
        }

        if (_viewModel.ActiveTool == PatternMaskDrawTool.Polygon && _viewModel.DraftPolygonPoints.Count > 0)
        {
            DrawDraftPolygonOverlay(dc, _viewModel.DraftPolygonPoints, zoom);
        }

        if (_hasHoverPoint && (_viewModel.ActiveTool is PatternMaskDrawTool.Brush or PatternMaskDrawTool.Eraser))
        {
            var brushRadius = Math.Max(1.0, _viewModel.BrushSizePixels * zoom * 0.5);
            var pen = _viewModel.ActiveTool == PatternMaskDrawTool.Brush ? BrushCursorPen : EraserCursorPen;
            dc.DrawEllipse(
                null,
                pen,
                new Point(_hoverLocal.X * zoom, _hoverLocal.Y * zoom),
                brushRadius,
                brushRadius);
        }
    }

    private void DrawDraftPolygonOverlay(DrawingContext dc, IReadOnlyList<PointD> points, double zoom)
    {
        int count = points.Count;
        var firstCanvasPt = new Point(points[0].X * zoom, points[0].Y * zoom);

        if (count >= 2)
        {
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(firstCanvasPt, isFilled: true, isClosed: true);
                for (int i = 1; i < count; i++)
                {
                    ctx.LineTo(new Point(points[i].X * zoom, points[i].Y * zoom), isStroked: true, isSmoothJoin: true);
                }

                if (_hasHoverPoint)
                {
                    ctx.LineTo(new Point(_hoverLocal.X * zoom, _hoverLocal.Y * zoom), isStroked: true, isSmoothJoin: true);
                }
            }

            geom.Freeze();
            dc.DrawGeometry(DraftPreviewFillBrush, DraftPreviewPen, geom);
        }

        for (int i = 0; i + 1 < count; i++)
        {
            var p0 = new Point(points[i].X * zoom, points[i].Y * zoom);
            var p1 = new Point(points[i + 1].X * zoom, points[i + 1].Y * zoom);
            dc.DrawLine(PolygonSolidEdgePen, p0, p1);
        }

        if (_hasHoverPoint)
        {
            var lastPt = new Point(points[count - 1].X * zoom, points[count - 1].Y * zoom);
            var hoverPt = new Point(_hoverLocal.X * zoom, _hoverLocal.Y * zoom);
            dc.DrawLine(DraftPreviewPen, lastPt, hoverPt);
        }

        double snapRadiusLocal = Math.Max(3.5, 9.0 / Math.Max(0.25, zoom));
        bool isHoveringStart = _hasHoverPoint && _viewModel!.IsNearDraftPolygonStart(_hoverLocal.X, _hoverLocal.Y, snapRadiusLocal);

        for (int i = 0; i < count; i++)
        {
            var pt = new Point(points[i].X * zoom, points[i].Y * zoom);
            if (i == 0 && isHoveringStart)
            {
                dc.DrawEllipse(PolygonStartSnapBrush, PolygonStartSnapPen, pt, 7.0, 7.0);
            }
            else
            {
                double r = i == 0 ? 4.2 : 3.2;
                dc.DrawEllipse(PolygonVertexFillBrush, PolygonVertexPen, pt, r, r);
            }
        }
    }

    private static void DrawMaskRegionGeometry(
        DrawingContext dc,
        MaskRegion mask,
        bool isSelected,
        double roiW,
        double roiH,
        double zoom)
    {
        var left = mask.Bounds.Left * roiW * zoom;
        var top = mask.Bounds.Top * roiH * zoom;
        var width = Math.Max(1.0, mask.Bounds.Width * roiW * zoom);
        var height = Math.Max(1.0, mask.Bounds.Height * roiH * zoom);
        var fill = isSelected ? SelectedMaskFillBrush : MaskFillBrush;
        var pen = isSelected ? SelectedMaskStrokePen : MaskStrokePen;

        if (mask.Shape == MaskShape.Rectangle)
        {
            dc.DrawRectangle(fill, pen, new Rect(left, top, width, height));
        }
        else
        {
            dc.DrawEllipse(
                fill,
                pen,
                new Point(left + (width * 0.5), top + (height * 0.5)),
                width * 0.5,
                height * 0.5);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateFrozenDashedPen(Brush brush, double thickness, double[] dashes)
    {
        var pen = new Pen(brush, thickness)
        {
            DashStyle = new DashStyle(dashes, 0.0)
        };
        pen.Freeze();
        return pen;
    }

    private sealed class OverlayVisualHost(Action<DrawingContext> renderCallback) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext)
            => renderCallback(drawingContext);
    }
}
