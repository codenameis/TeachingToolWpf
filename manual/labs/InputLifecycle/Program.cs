using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionGeometry;

// HWND 대신 좌표를 직접 전달한다. 실제 마우스도 DIP 변환 뒤 같은 API를 호출한다.
var session = new EditorSession();
session.SetViewport(new ViewportTransform(2, new PointD(20, 10)));
var roiId = session.Add(new RoiGeometry(new PointD(100, 100), 80, 80, 0));
var input = new InteractionController(session);

// 원본 중심 (100,100)은 화면 (220,210)에 있다.
input.Down(new PointD(220, 210));
input.Move(new PointD(240, 220));
Require(session.IsPreviewing, "이동 중에는 미리보기 상태");
Require(session.State.Rois[roiId].Geometry.Center == new PointD(100, 100),
    "이동 미리보기는 원본 ROI를 매번 다시 만들지 않음");
Require(session.TransformPreview is not null, "표시용 공통 변환이 존재함");

// Move에서 멈춘 위치와 Up 위치가 다르다. Up의 마지막 위치까지 반영해야 한다.
input.Up(new PointD(260, 230));
Require(session.State.Rois[roiId].Geometry.Center == new PointD(120, 110),
    "화면 이동 (40,20)을 배율 2로 나눈 원본 이동 (20,10)");
Require(!session.IsPreviewing && !input.IsDragging, "확정 후 제스처와 미리보기 종료");
session.Undo();
Require(session.State.Rois[roiId].Geometry.Center == new PointD(100, 100), "Undo 한 번으로 이동 취소");
session.Redo();
Require(session.State.Rois[roiId].Geometry.Center == new PointD(120, 110), "Redo로 이동 복원");

var committedCenter = session.State.Rois[roiId].Geometry.Center;
var undoName = session.UndoName;
input.Down(session.State.Viewport.ImageToScreen(committedCenter));
input.Move(new PointD(300, 260));
input.Cancel();
Require(session.State.Rois[roiId].Geometry.Center == committedCenter && session.UndoName == undoName,
    "Cancel은 드래그 전 ROI와 이력을 유지");
Console.WriteLine("완료: 입력 수명 8개 확인. WPF/GPU 프레임 성능 시험은 아닙니다.");

static void Require(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException($"FAIL: {description}");
    }
    Console.WriteLine($"PASS: {description}");
}
