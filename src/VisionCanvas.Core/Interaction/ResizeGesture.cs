using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class ResizeGesture(InteractionController controller, Roi originalRoi, int handleIndex, PointD dragStartScreen) : EditGesture(controller)
{
    protected override string Name => "ROI 크기 조절";

    public override void Move(PointD screen, InputModifiers modifiers)
    {
        RoiGeometry previewGeometry;
        if ((screen - dragStartScreen).Length < MinimumDragDistanceDip)
        {
            previewGeometry = originalRoi.Geometry;
        }
        else
        {
            previewGeometry = HandleGeometry.Resize(originalRoi.Geometry, handleIndex, InitialViewport.ScreenToImage(screen));
        }

        Editor.PreviewResize(originalRoi.Id, previewGeometry);
    }
}
