using TeachingToolWpf.Commands;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Guid[] _roisToAttach = [];
    public RelayCommand ChooseChildRois
    {
        get;
    }
    public AsyncCommand AttachToParent
    {
        get;
    }

    private void ChooseRoisToAttach()
    {
        _roisToAttach = Session.State.Selection.ToArray();
        Status = $"ROI {_roisToAttach.Length}개를 지정했습니다. 부모 ROI 하나를 선택하고 ‘선택 부모에 연결’을 누르세요.";
        RefreshCommands();
    }

    private async Task AttachRoisToParentAsync()
    {
        var ids = _roisToAttach;
        var parentId = Session.State.Selection.Single();
        EditCompletion = PrepareEditAsync("부모 ROI에 연결", token => Session.PrepareAttachAsync(ids, parentId, token));
        await EditCompletion;
        _roisToAttach = [];
        RefreshCommands();
    }
    public RelayCommand AddParentRoi
    {
        get;
    }

    public RelayCommand DrawParentRoi
    {
        get;
    }

    private Guid? SelectedGroupParent => Session.State.SelectionCount == 1 && Session.State.Selection.Count == 1 &&
        Session.State.IsGroupRoi(Session.State.Selection.Single()) ? Session.State.Selection.Single() : null;

    private bool CanCreateRoi() => HasImage() && (SelectedGroupParent is not { } parent ||
        RoiHierarchy.Depth(Session.State.Rois, parent) < RoiHierarchy.MaximumDepth);

    private void AddRoiInView()
    {
        if (SelectedGroupParent is { } parent)
        {
            AddChildRoiInView(parent);
        }
        else
        {
            AddRoiInView(Session.Add, () => StartRoiDrawing(null));
        }
    }

    private void AddParentRoiInView() => AddRoiInView(Session.AddParent, StartParentDrawing);

    private void AddRoiInView(Func<RoiGeometry, Guid> add, Action startDrawing)
    {
        if (DisplayImage is not { } image)
        {
            return;
        }

        if (SelectedShape == RoiShapeKind.Polygon)
        {
            startDrawing();
            return;
        }

        var geometry = RoiPlacement.InView(image.Width, image.Height, Session.State.Viewport, Layout.Width, Layout.Height);
        var screen = Session.State.Viewport.ImageToScreen(geometry.Center);
        if (screen.X < 0 || screen.Y < 0 || screen.X > Layout.Width || screen.Y > Layout.Height)
        {
            FitImage();
        }

        var id = add(RoiGeometry.Create(SelectedShape, geometry.Center, geometry.Width, geometry.Height));
        SetTool(EditorTool.Select);
        Status = Session.State.Rois[id].IsGroup ? "부모 ROI를 추가했습니다. ROI 추가/그리기로 하위 ROI를 만드세요."
            : "ROI를 추가했습니다. 핸들로 크기와 회전을 조절하세요.";
    }

    private bool CanAttachToSelectedRoi() => HasImage() && Session.State.SelectionCount == 1 && Session.State.Selection.Count == 1 &&
        RoiHierarchy.Depth(Session.State.Rois, Session.State.Selection.Single()) < RoiHierarchy.MaximumDepth;

    private void AddChildRoiInView(Guid parentId)
    {
        if (SelectedShape == RoiShapeKind.Polygon)
        {
            StartRoiDrawing(parentId);
            return;
        }

        var parent = Session.State.Rois[parentId];
        Session.AddChild(parentId, RoiPlacement.InParent(parent.Geometry, SelectedShape));
        SetTool(EditorTool.Select);
        Status = $"‘{parent.Name}’의 자식 ROI를 추가했습니다. 이동해도 부모 관계는 유지됩니다.";
    }

    private void StartRoiDrawing(Guid? parentId)
    {
        Input.DrawParentId = parentId;
        SetTool(EditorTool.Draw);
        Status = parentId is { } id ? $"‘{Session.State.Rois[id].Name}’의 자식 ROI를 그립니다."
            : "부모 없이 ROI를 그립니다. 겹친 ROI와 자동 연결되지 않습니다.";
        if (SelectedShape == RoiShapeKind.Polygon)
        {
            Status += " 꼭짓점을 차례로 클릭하세요. 선이 교차하면 교차점에서 자동 완성합니다.";
        }
    }

    private void StartParentDrawing()
    {
        Input.DrawAsGroup = true;
        SetTool(EditorTool.Draw);
        Status = SelectedShape == RoiShapeKind.Polygon
            ? "부모 ROI의 꼭짓점을 차례로 클릭하세요. 선이 교차하면 교차점에서 자동 완성합니다."
            : "부모 ROI를 그립니다. 완성한 부모를 선택한 뒤 ROI 추가/그리기를 사용하세요.";
    }
}
