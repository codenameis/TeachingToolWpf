using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Interaction;

/// <summary>초안은 문서와 분리하고 포인터를 놓을 때 한 번만 이력에 등록한다.</summary>
internal sealed class MaskDrawGesture(InteractionController controller, Roi targetRoi, PointD dragStartScreen) : IGesture
{
    private const double MinimumDrawDistanceDip = 4;
    private const double MinimumSizeInImage = .01;

    private readonly ViewportTransform _initialViewport = controller.Editor.State.Viewport;
    private readonly ImageReference? _initialImage = controller.Editor.State.Image;
    private MaskRegion? _draft;

    public void Move(PointD screen, InputModifiers modifiers)
    {
        var startInImage = _initialViewport.ScreenToImage(dragStartScreen);
        if (!targetRoi.Geometry.Contains(startInImage) || (screen - dragStartScreen).Length < MinimumDrawDistanceDip)
        {
            _draft = null;
            controller.Show(new());
            return;
        }
        var normalizedStart = Normalize(startInImage);
        var normalizedEnd = Normalize(_initialViewport.ScreenToImage(screen));
        var normalizedBounds = new BoundsD(Math.Min(normalizedStart.X, normalizedEnd.X), Math.Min(normalizedStart.Y, normalizedEnd.Y), Math.Max(normalizedStart.X, normalizedEnd.X), Math.Max(normalizedStart.Y, normalizedEnd.Y));
        if (normalizedBounds.Width * targetRoi.Geometry.Width < MinimumSizeInImage ||
            normalizedBounds.Height * targetRoi.Geometry.Height < MinimumSizeInImage)
        {
            _draft = null;
        }
        else
        {
            _draft = new(controller.MaskShape, normalizedBounds);
        }
        controller.Show(new()
        {
            MaskDraft = _draft
        });
    }

    private PointD Normalize(PointD pointInImage)
    {
        var pointInRoi = targetRoi.Geometry.ToLocal(pointInImage);
        return new(Math.Clamp((pointInRoi.X / targetRoi.Geometry.Width) + .5, 0, 1),
            Math.Clamp((pointInRoi.Y / targetRoi.Geometry.Height) + .5, 0, 1));
    }

    public void Complete()
    {
        var state = controller.Editor.State;
        if (_draft is not null && ReferenceEquals(_initialImage, state.Image) && state.Selection.Count == 1 &&
            state.Selection.Contains(targetRoi.Id) && state.Rois.TryGetValue(targetRoi.Id, out var currentRoi) && currentRoi == targetRoi)
        {
            controller.Editor.AddMask(targetRoi.Id, _draft);
            controller.ValidateDraftImage();
        }
    }

    public void Cancel() => controller.Show(new());
}
