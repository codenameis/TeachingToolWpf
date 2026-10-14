using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class MoveGesture(InteractionController controller, PointD dragStartScreen) : EditGesture(controller)
{
    protected override string Name => "ROI 이동";

    public override void Move(PointD screen, InputModifiers modifiers)
    {
        var translationInImage = (screen - dragStartScreen) / InitialViewport.Scale;
        if ((screen - dragStartScreen).Length < MinimumDragDistanceDip)
        {
            translationInImage = new();
        }

        if (modifiers.HasFlag(InputModifiers.Shift))
        {
            translationInImage = Math.Abs(translationInImage.X) > Math.Abs(translationInImage.Y) ? new(translationInImage.X, 0) : new(0, translationInImage.Y);
        }

        Controller.PreviewTranslation(translationInImage);
    }
}
