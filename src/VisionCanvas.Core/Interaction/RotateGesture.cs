using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

internal sealed class RotateGesture(InteractionController controller, PointD dragStartScreen, PointD pivotInImage) : EditGesture(controller)
{
    private const double AngleSnapDegrees = 15;

    private PointD _previousDirection = controller.Editor.State.Viewport.ScreenToImage(dragStartScreen) - pivotInImage;
    private double _accumulatedDegrees;

    protected override string Name => "ROI 회전";

    public override void Move(PointD screen, InputModifiers modifiers)
    {
        var currentDirection = InitialViewport.ScreenToImage(screen) - pivotInImage;
        if (currentDirection.Length < 1e-12)
        {
            return;
        }

        // 이전 방향과의 각도 차이를 누적하므로 ±180도 경계를 넘어도 회전이 이어진다.
        var directionCrossProduct = (_previousDirection.X * currentDirection.Y) - (_previousDirection.Y * currentDirection.X);
        var directionDotProduct = (_previousDirection.X * currentDirection.X) + (_previousDirection.Y * currentDirection.Y);
        _accumulatedDegrees += Math.Atan2(directionCrossProduct, directionDotProduct) * 180 / Math.PI;
        _previousDirection = currentDirection;
        var previewDegrees = _accumulatedDegrees;
        if (modifiers.HasFlag(InputModifiers.Shift))
        {
            previewDegrees = Math.Round(previewDegrees / AngleSnapDegrees) * AngleSnapDegrees;
        }

        Controller.PreviewRotation(pivotInImage, previewDegrees);
    }
}
