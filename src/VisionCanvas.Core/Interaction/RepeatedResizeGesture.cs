using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class RepeatedResizeGesture(InteractionController controller, RepeatedRoiCell cell, RoiGeometry originalGeometry, int handleIndex, PointD dragStartScreen) : EditGesture(controller)
{
    protected override string Name => "ROI 크기 조절";

    public override void Move(PointD screen, InputModifiers modifiers)
    {
        RoiGeometry previewGeometry;
        if ((screen - dragStartScreen).Length < MinimumDragDistanceDip)
        {
            previewGeometry = originalGeometry;
        }
        else
        {
            previewGeometry = HandleGeometry.Resize(originalGeometry, handleIndex, InitialViewport.ScreenToImage(screen));
        }

        Editor.PreviewRepeatedGeometry(cell, previewGeometry);
    }
}
