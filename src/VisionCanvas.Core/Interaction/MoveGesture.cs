using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class MoveGesture(InteractionController controller, PointD dragStartScreen) : EditGesture(controller)
{
    private (PointD Translation, bool AllowSlide)? _lastRequest;

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

        var request = (Translation: translationInImage, AllowSlide: !modifiers.HasFlag(InputModifiers.Shift));
        if (_lastRequest == request)
        {
            // 같은 위치의 Mouse Up은 벽 접촉 계산을 반복하지 않고 표시한 결과를 그대로 확정한다.
            return;
        }
        Controller.PreviewTranslation(request.Translation, request.AllowSlide);
        _lastRequest = request;
    }
}
