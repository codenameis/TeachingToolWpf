using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Interaction;

namespace TeachingToolWpf.Presentation;

public sealed partial class EditorViewModel
{
    public RelayCommand AddChildRoi
    {
        get;
    }

    public RelayCommand DrawChildRoi
    {
        get;
    }

    private void AddRoiInView()
    {
        if (DisplayImage is not { } image)
        {
            return;
        }

        if (SelectedShape == RoiShapeKind.Polygon)
        {
            StartRoiDrawing(null);
            return;
        }

        var geometry = RoiPlacement.InView(image.Width, image.Height, Session.State.Viewport, _viewport.ViewWidth, _viewport.ViewHeight);
        var screen = Session.State.Viewport.ImageToScreen(geometry.Center);
        if (screen.X < 0 || screen.Y < 0 || screen.X > _viewport.ViewWidth || screen.Y > _viewport.ViewHeight)
        {
            FitImage();
        }

        Session.Add(RoiGeometry.Create(SelectedShape, geometry.Center, geometry.Width, geometry.Height));
        SetTool(EditorTool.Select);
        Status = "ROI를 추가했습니다. 핸들로 크기와 회전을 조절하세요.";
    }

    private bool CanAddChildRoi() => HasImage() && Session.State.Selection.Count == 1;

    private void AddChildRoiInView()
    {
        var parentId = Session.State.Selection.Single();
        if (SelectedShape == RoiShapeKind.Polygon)
        {
            StartRoiDrawing(parentId);
            return;
        }

        var parent = Session.State.Rois[parentId];
        var geometry = parent.Geometry;
        var child = RoiGeometry.Create(SelectedShape, geometry.Center,
            Math.Max(.01, geometry.Width / 3), Math.Max(.01, geometry.Height / 3));
        Session.AddChild(parentId, child.RotateAround(geometry.Center, geometry.Angle));
        SetTool(EditorTool.Select);
        Status = $"‘{parent.Name}’의 자식 ROI를 추가했습니다. 이동해도 부모 관계는 유지됩니다.";
    }

    private void StartRoiDrawing(Guid? parentId)
    {
        Input.DrawParentId = parentId;
        SetTool(EditorTool.Draw);
        Status = parentId is { } id ? $"‘{Session.State.Rois[id].Name}’의 자식 ROI를 그립니다."
            : "독립적인 부모 ROI를 그립니다. 겹친 ROI와 자동 연결되지 않습니다.";
        if (SelectedShape == RoiShapeKind.Polygon)
        {
            Status += " 꼭짓점을 차례로 클릭하세요. 선이 교차하면 교차점에서 자동 완성합니다.";
        }
    }
}
