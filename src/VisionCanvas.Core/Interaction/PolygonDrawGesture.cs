using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Interaction;

/// <summary>여러 번의 클릭을 이어 붙여 폴리곤 ROI 초안을 만들고 닫힘 시 도형을 완성하는 제스처다.</summary>
internal sealed class PolygonDrawGesture(InteractionController controller, PolygonDraftState draft) : IGesture
{
    public bool IsFinished { get; private set; }

    public void AddClick(PointD screen)
    {
        var state = controller.Editor.State;
        if (draft.TryAddPointOrClose(screen, state.Viewport, state.Image) is { } closedPolygon)
        {
            CompleteWithPoints(closedPolygon);
            return;
        }

        ShowDraft();
    }

    public void Move(PointD screen, InputModifiers modifiers)
    {
        if (IsFinished || !draft.HasPoints)
        {
            return;
        }

        var state = controller.Editor.State;
        var cursor = RoiImageBounds.Clamp(state.Viewport.ScreenToImage(screen), state.Image);
        ShowDraft(cursor);
    }

    public void Complete()
    {
        if (IsFinished)
        {
            return;
        }

        ShowDraft();
    }

    public void Cancel()
    {
        IsFinished = true;
        draft.Clear();
    }

    public void CompleteDraft()
    {
        if (!draft.CanComplete)
        {
            return;
        }

        CompleteWithPoints(draft.BuildClosedPoints());
    }

    public void RemoveLastPoint()
    {
        draft.RemoveLastPoint();
        if (!draft.HasPoints)
        {
            IsFinished = true;
            controller.Show(new());
            return;
        }

        ShowDraft();
    }

    private void CompleteWithPoints(IEnumerable<PointD> points)
    {
        var geometry = RoiGeometry.Polygon(points);
        controller.AddDrawnRoi(geometry);
        IsFinished = true;
        draft.Clear();
        controller.Tool = EditorTool.Select;
    }

    private void ShowDraft(PointD? cursor = null)
        => controller.Show(draft.CreateOverlay(controller.DrawAsGroup, cursor));
}
