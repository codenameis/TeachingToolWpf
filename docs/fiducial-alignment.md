# 1~4점 보정과 부모 계층별 ROI 맵핑

2026-10-16. `main`에서 로컬 수정·검증만 수행한다. 사용자 요청 범위는 호출하기 쉬운 보정 클래스와 전체 검사/모듈/ROI 그룹별 독립 보정, 최종 검사 ROI의 부모 연결이다. 마크 검출기나 WPF 버튼을 새로 연결하는 작업은 포함하지 않는다.

## 바로 쓰는 진입점

일반 ROI 문서에는 이미 `Roi.ParentId`가 있다. [RoiAlignmentMap](../src/VisionCanvas.Core/Alignment/RoiAlignmentMap.cs)은 그 실제 계층을 사용한다. `Map(roiId)`는 **ID·ParentId·이름·마스크·검색 여유·필터를 보존한 Roi**를 반환하고 `Geometry`만 보정한다. 위치가 가까운 도형을 부모로 추측하지 않는다.

```csharp
using VisionCanvas.Core.Alignment;

// 새 검사 영상마다 새 맵을 만든다. document는 원래 티칭 문서의 불변 스냅샷이다.
var map = new RoiAlignmentMap(document);

// null = 전체 검사 보정. 같은 인덱스는 같은 물리 마크다.
if (!map.TryAlign(null, wholeReferenceMarks, wholeDetectedMarks, out map, out var error))
{
    // ViewModel의 상태 문구로 error를 표시하고 이번 검사를 중단한다.
    return;
}

// 모듈 A에만 적용된다. 모듈 B는 별도로 등록하거나 전체 보정을 상속한다.
if (!map.TryAlign(moduleAId, moduleReferenceMarks, moduleDetectedMarks, out map, out error))
{
    return;
}

// 해당 모듈 아래 ROI 그룹의 추가 보정. 부모는 ParentId에서 자동으로 찾는다.
if (!map.TryAlign(roiGroupId, groupReferenceMarks, groupDetectedMarks, out map, out error))
{
    return;
}

var inspectionRoi = map.Map(leafRoiId);
var parentId = inspectionRoi.ParentId; // 최종 검사 ROI도 자기 부모를 알고 있다.
var parentRoi = parentId is { } id ? map.Map(id) : null;
var searchGeometry = inspectionRoi.Geometry; // 기존 검사 서비스에 전달할 보정 형상
```

전체만, 모듈만, ROI 그룹만 등록할 수도 있다. 로컬 보정이 없는 ROI는 가장 가까운 보정된 조상, 그다음 전체 보정을 사용한다. 아무 보정도 등록하지 않은 맵은 원래 ROI를 반환한다. `TryAlign` 실패 시 반환 맵은 기존 객체 그대로다. **실패를 무시하고 검사를 계속하면 안 된다.**

기존 검사 서비스는 공급자 중립 `RoiGeometry`를 받는다. 검사 실행을 묶는 호출자는 위 `inspectionRoi.Id/ParentId`를 함께 보관하고 결과와 연결한다. VisionInspection에 Canvas/Core 역참조를 추가하지 않았다. 기존 WPF 검사 버튼이 자동으로 이 맵을 사용하도록 바뀐 것은 아니다.

## 좌표와 합성 규칙

- 모든 기준 마크는 **보정 전 원래 티칭 이미지 좌표**, 모든 검출 마크는 **현재 검사 원본 이미지 좌표**다. 하위 호출에 이미 보정한 기준 좌표를 넣지 않는다.
- 1점은 XY 이동만 계산한다. 상위 회전이 있으면 그대로 유지한다.
- 2~4점은 전체 대응점의 최소제곱 XYθ를 계산한다. 배율은 1로 고정하고 마크를 임의로 제외하지 않는다. 일직선상의 여러 점도 강체 보정에는 사용할 수 있다.
- 상위 보정을 먼저 적용한 기준 마크에서 추가 오차를 구한다. `LocalTransform`은 이 추가 변환, `Transform`은 원래 티칭 좌표에 한 번 적용하는 합성 변환이다.
- 원·사각형·삼각형·다각형의 크기와 형상을 유지한다. 마스크의 정규화 좌표와 검색 여유는 같은 ROI 프레임을 따른다.
- 새 맵은 불변 문서 참조와 보정된 그룹 사전만 보관한다. 전체 ROI 문서·BVH를 복제하거나 편집 이력에 보정을 추가하지 않는다. 새 보정 맵을 만들어도 이전 맵은 바뀌지 않는다.
- 하위 보정이 등록된 뒤 상위 보정을 교체/추가하는 요청은 실패한다. 새 맵에서 전체 → 모듈 → ROI 그룹 순서로 다시 구성하여 이전 영상/상위 보정과 하위 값을 섞지 않는다. 형제 그룹은 독립적으로 추가/교체할 수 있다.
- 원래 문서가 편집되거나 영상 프레임이 바뀌면 새 맵을 만든다. 완료 결과의 frame ID/document revision 검증은 기존 검사 실행 소유자가 담당한다.

실제 계층은 `RootGroup → ModuleGroup → RoiGroup → Roi` 최대 4단계다. 전체 검사 보정은 문서 노드가 아닌 맵 전체의 기본값이므로 루트 그룹 ID와 같을 필요가 없다. 최종 리프를 보정 범위로 등록하는 것은 거부하며, 실제 자식이 있는 노드도 그룹으로 인정한다. 없는 부모/순환/깊이 초과는 실패한다.

## 계산 클래스만 사용하기

문서가 필요 없는 순수 계산은 [FiducialAlignment](../src/VisionGeometry/FiducialAlignment.cs)를 사용한다. 의존 패키지는 추가하지 않았다.

```csharp
if (FiducialAlignment.TryCreate(referenceMarks, detectedMarks,
    out var alignment, out var error, maximumErrorPixels: 1.0))
{
    var inspectionGeometry = alignment.Apply(teachingGeometry);
    var offsetAtModuleCenter = alignment.Transform.Apply(moduleCenter) - moduleCenter;
    var angle = alignment.Transform.AngleDegrees;
}
```

별도 하위 객체를 직접 만들 때는 `parent: overallAlignment`를 넘길 수 있다. 문서의 모듈/그룹에서는 부모 선택 오류를 방지하는 `RoiAlignmentMap.TryAlign`을 사용한다. `Transform.Translation`은 회전 피벗의 이동량이므로 다른 위치의 모듈 OFFSET으로 그대로 사용하지 않는다.

대량 검사에서는 같은 그룹의 보정을 한 번 조회하고 반복 사용할 수 있다. 아래 반복문에는 **해당 그룹을 최근접 보정 조상으로 사용하는 ROI만** 넣는다. 별도 보정된 하위 그룹은 자기 보정값을 사용해야 한다.

```csharp
var alignment = map.GetAlignment(roiGroupId);
foreach (var roi in groupLeaves)
{
    var inspectionRoi = alignment is null
        ? roi
        : roi with { Geometry = alignment.Apply(roi.Geometry) };
    // inspectionRoi.ParentId는 계속 원래 부모 ID다.
}
```

## 검증과 한계

- PASS: 신규 34건, Core 전체 435건. 1~4점, 32K 좌표, 2~4점 각각 -180~180° 10° 간격 총111자세, 역변환, 모든 ROI 형상, 부모 회전 상속과 중복 적용 방지, 형제 독립, 부모 ID/마스크/검색 여유 보존, 불변 스냅샷과 실패 원자성.
- PASS: 100모듈·400 ROI 그룹·40,000 리프의 좌표와 부모 ID 전수 대조. 실행·측정은 별도 [벤치마크](../benchmarks/TeachingToolWpf.Benchmarks/FiducialAlignmentProfile.cs)에 둔다.
- 입력 개수/대응 개수, 비유한 값, 마크 간격 1px 미만, 회전 결정 불가, 수치 범위 초과를 거부한다. 기본 최대 잔차는 1px이며 `maximumErrorPixels`로 지정한다. RMS와 최대 오차를 모두 제공하고 최대 오차로 판정한다.
- 1px 마크 간격은 입력 거부 하한일 뿐 충분한 각도 정확도를 보장하지 않는다. 마크는 검사 영역에 넓게 분산해야 한다. 마크 잔차가 작아도 먼 ROI의 오차를 보장하지 않는다. 1점에서는 회전 검증이 불가능하며, 대칭 배치의 잘못된 대응도 작은 잔차를 만들 수 있다.
- Affine·원근·4점 양선형 OFFSET 보간, 자동 마크 찾기/대응, 이미지 재표본화는 이번 클래스에 포함하지 않는다.
- 맵은 형상을 조용히 자르거나 이미지 안으로 밀지 않는다. 검사 실행 전에 기존 원본/검색 영역 유효성 검사를 적용해야 한다. 부모 ID 보존은 부모 경계 안에 들어간다는 판정과 다르다.
- 기존 압축 반복 셀은 일반 `Roi.Id/ParentId` 검사 계층이 아니다. 반복 1억 셀의 그룹 검사 지원을 추가한 것으로 표현하지 않는다.
- UI 클릭 연결·실제 마크 검출·현장 위치 정답·전체 검사 지연·표시 FPS는 NOT_RUN이다. 이번 성능은 보정 수학과 맵핑에 한정한다.

측정 원시는 [JSON](data/fiducial-alignment-profile.json)에 보존한다. Release, .NET 10, 고정 합성 좌표, 준비 후 3회 워밍업·7회 측정이다. 원본 영상 읽기, 마크 검출, 실제 검사, 화면 표시, 문서/맵 등록 및 그룹 변환 사전 준비는 측정에서 제외한다.

| 경로 | 중앙값 | 측정 범위 | 실행 스레드 할당 |
|---|---:|---:|---:|
| 1점 계산 10,000회 | 1.177ms | 1.126~7.900ms | 약1.60MB |
| 2점 계산 10,000회 | 6.007ms | 5.378~15.389ms | 1.60MB |
| 3점 계산 10,000회 | 6.925ms | 6.265~9.213ms | 1.60MB |
| 4점 계산 10,000회 | 10.012ms | 8.443~13.666ms | 1.60MB |
| 40,000 ROI 부모 조회 + 중심 좌표 계산 | 12.507ms | 12.126~21.533ms | 0B |
| 준비된400그룹 변환으로40,000 중심 계산 | 0.334ms | 0.328~0.354ms | 0B |
| `Map`으로40,000 부모 보존 Roi 생성 | 21.118ms | 20.437~21.885ms | 6.72MB |

각 보정 결과 객체는 약160B, 실제 `Map`은 Roi와 RoiGeometry를 함께 만들어 약168B/ROI를 할당했다. 관리 힙의 해당 스레드 할당량이며 전체 프로세스/GPU 메모리가 아니다. 0.334ms는 준비된 변환으로 중심 좌표만 계산한 시간이며 **전체 ROI 객체 생성이나 전체 검사 시간으로 표현하면 안 된다**. 반복 중 시간 변동을 원시에 보존하며 지속60FPS를 검증한 결과가 아니다.

```powershell
dotnet test tests/TeachingToolWpf.Core.Tests/TeachingToolWpf.Core.Tests.csproj -c Release
dotnet run --project benchmarks/TeachingToolWpf.Benchmarks/TeachingToolWpf.Benchmarks.csproj -c Release -- --fiducial-alignment-profile --output docs/data/fiducial-alignment-profile.json
pwsh -File scripts/Format-Code.ps1 -Verify
```

전체 형식 검사는 이번 작업 밖의 기존 줄바꿈/공백 위반으로 FAIL이며 무관한 파일은 재포맷하지 않았다. 추가·변경 C# 6파일의 한정 공백·스타일 검사는 PASS다. 전체 재검사에서도 해당 6파일의 오류는 없으며, 로컬 로그는 `artifacts/alignment/full-format-final.log`에 있다.

## 책임 검토

순수 수학은 VisionGeometry, 부모 계층과 보정 선택은 Canvas.Core, 검출·검사 실행 및 UI 상태는 기존 호출자 책임이다(SRP/GRASP Controller·Information Expert). 기존 강체 변환과 불변 ROI를 재사용한다(Creator·Low Coupling·High Cohesion). 상속이나 공급자 인터페이스를 추가하지 않아 LSP/ISP·Polymorphism을 위한 불필요한 계층이 없고, 기존 코드 경로 변경 없이 클래스가 추가된다(OCP). 외부 공급자/WPF 의존성을 넣지 않는다(DIP). 두 좁은 클래스의 경계로 수학과 문서 변화를 분리한다(Pure Fabrication·Indirection·Protected Variations). 새 편집 명령은 없으므로 Undo/Redo는 기존 경로를 유지하며 Core 회귀로 확인한다.
