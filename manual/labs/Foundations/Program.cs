using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

// 학습용 확인은 틀리면 즉시 실패한다. 눈으로 PASS 문구만 확인하는 예제가 아니다.
var view = new ViewportTransform(2, new PointD(20, 10));
var imagePoint = new PointD(100, 50);
var screenPoint = view.ImageToScreen(imagePoint);
Require(screenPoint == new PointD(220, 110), "이미지 → 화면 좌표");
Require(view.ScreenToImage(screenPoint) == imagePoint, "화면 → 이미지 역변환");

var anchor = new PointD(400, 300);
var zoomed = view.ZoomAt(anchor, 1.5);
Require((view.ScreenToImage(anchor) - zoomed.ScreenToImage(anchor)).Length < 1e-9,
    "확대 후 마우스 아래 원본 좌표 유지");

var rotation = new RigidTransform2D(new PointD(10, 10), 90, new PointD(3, 0));
var rotated = rotation.Apply(new PointD(20, 10));
Require((rotated - new PointD(13, 20)).Length < 1e-9, "회전 후 이동");
Require((rotation.InverseApply(rotated) - new PointD(20, 10)).Length < 1e-9, "강체 역변환");

using (var pixels = new PixelBuffer(3, 2, PixelKind.Gray16))
{
    Require(pixels.Stride == 6 && pixels.Length == 12, "Gray16의 바이트 수");
}

var session = new EditorSession();
session.InitializeImage(new ImageReference("lesson", "학습 영상", 1024, 1024), ViewportTransform.Identity);
var parent = session.Add(new RoiGeometry(new PointD(300, 300), 200, 200, 0));
session.AddChild(parent, new RoiGeometry(new PointD(300, 300), 20, 20, 0));
Require(session.State.Rois.Count == 2, "부모와 자식 생성");
session.Select([parent]);
session.DeleteSelection();
Require(session.State.Rois.Count == 0, "부모 삭제 시 자손 연쇄 삭제");
session.Undo();
Require(session.State.Rois.Count == 2, "Undo가 부모와 자식을 복원");
session.Redo();
Require(session.State.Rois.Count == 0, "Redo가 같은 삭제를 적용");
session.Undo();
session.Add(new RoiGeometry(new PointD(700, 700), 40, 40, 0));
Require(!session.CanRedo, "새 편집은 이전 Redo 분기를 제거");
Console.WriteLine("완료: 좌표·메모리·계층·이력 11개 확인. 성능 측정은 포함하지 않습니다.");

static void Require(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException($"FAIL: {description}");
    }
    Console.WriteLine($"PASS: {description}");
}
