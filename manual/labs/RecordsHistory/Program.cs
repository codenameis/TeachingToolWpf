using VisionCanvas.Core.Editing;
using VisionGeometry;

var session = new EditorSession();
var sameSession = session;
var roiId = session.Add(new RoiGeometry(new PointD(100, 80), 60, 40, 0));
var beforeState = session.State;
var beforeRoi = beforeState.Rois[roiId];
var originalName = beforeRoi.Name;

// with는 새 값을 만들 뿐 세션에 편집을 요청하지 않습니다.
var candidate = beforeRoi with { Name = "검사 영역" };
Require(beforeRoi.Name == originalName, "기존 ROI 이름 보존");
Require(candidate.Name == "검사 영역", "새 ROI 이름만 변경");
Require(!ReferenceEquals(beforeRoi, candidate), "새 ROI는 다른 참조");
Require(beforeRoi.Id == candidate.Id, "문서에서는 같은 ID의 ROI");
Require(ReferenceEquals(beforeRoi.Geometry, candidate.Geometry), "바꾸지 않은 형상은 공유");
Require(ReferenceEquals(session.State, beforeState), "with만으로 문서가 바뀌지 않음");

// 참조가 다른 것과 값이 다른 것은 별개의 질문입니다.
var sameValue = beforeRoi with { };
Require(!ReferenceEquals(beforeRoi, sameValue) && beforeRoi == sameValue,
    "별도 객체여도 record 값 비교는 같을 수 있음");

// 실제 공개 편집 API가 검증, 새 문서 생성, 이력 기록, 게시를 수행합니다.
session.Rename(roiId, "검사 영역");
var afterState = session.State;
var afterRoi = afterState.Rois[roiId];
Require(ReferenceEquals(session, sameSession), "편집을 관리하는 세션은 같은 객체");
Require(!ReferenceEquals(beforeState, afterState), "편집 후 문서는 새 상태");
Require(beforeState.Rois[roiId].Name == originalName, "옛 문서에서도 옛 이름 유지");
Require(afterRoi.Name == "검사 영역" && beforeRoi != afterRoi, "이름 변경은 ROI 값의 차이");

// 실제 DocumentEdit으로 전후 차이를 들여다봅니다. 세션 이력에 다시 넣지는 않습니다.
var difference = new DocumentEdit("이름 변경 관찰", beforeState, afterState);
Require(difference.Changes.Length == 1, "ROI 하나의 차이를 기록");
var change = difference.Changes[0];
Require(change.Id == roiId && change.Before == beforeRoi && change.After == afterRoi,
    "RoiChange가 ID와 Before/After를 보관");

session.Undo();
Require(session.State.Rois[roiId].Name == originalName && session.CanRedo,
    "Undo는 이름 변경 전 값을 복원");
Require(afterState.Rois[roiId].Name == "검사 영역", "Undo도 보관한 과거 객체를 덮어쓰지 않음");
session.Redo();
Require(session.State.Rois[roiId].Name == "검사 영역", "Redo는 변경 후 값을 복원");

session.Undo();
session.Rename(roiId, "새 검사 영역");
Require(!session.CanRedo, "Undo 뒤 새 편집은 기존 Redo 갈래 제거");
session.Rename(roiId, "새 검사 영역");
session.Undo();
Require(session.State.Rois[roiId].Name == originalName, "같은 이름 재설정은 이력 추가 없음");

static void Require(bool condition, string explanation)
{
    if (!condition)
    {
        throw new InvalidOperationException(explanation);
    }

    Console.WriteLine($"PASS: {explanation}");
}
