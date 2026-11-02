using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    /// <summary>선택/이력을 바꾸지 않고 ROI의 현재 영역을 화면에 맞춘다.</summary>
    public bool FocusRoi(Guid id, double viewWidth, double viewHeight)
        => State.Rois.TryGetValue(id, out var roi) && FocusBounds(roi.Geometry.Bounds, viewWidth, viewHeight);

    /// <summary>외부 검사 결과 목록의 원본 좌표 영역도 ROI 선택 없이 표시한다.</summary>
    public bool FocusBounds(BoundsD bounds, double viewWidth, double viewHeight)
    {
        if (IsPreviewing || !ValidView(viewWidth, viewHeight) ||
            !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
            !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var fit = ViewportTransform.Fit(bounds.Width, bounds.Height, viewWidth, viewHeight);
        SetViewport(fit with
        {
            Offset = fit.Offset - (new PointD(bounds.Left, bounds.Top) * fit.Scale)
        });
        return true;
    }

    public bool FitImage(double viewWidth, double viewHeight)
    {
        if (IsPreviewing || !ValidView(viewWidth, viewHeight) || State.Image is not { } image)
        {
            return false;
        }

        SetViewport(ViewportTransform.Fit(image.Width, image.Height, viewWidth, viewHeight));
        return true;
    }

    private static bool ValidView(double width, double height)
        => double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
