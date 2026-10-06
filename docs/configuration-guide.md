# ConfigPage와 이미지 중심 크로스헤어 사용법

2026-10-06 후속. 앱 상단 **설정** 버튼으로 `ConfigPage`를 연다. 같은 버튼을 다시 누르면 열린 설정 창을 활성화한다. 닫고 다시 열어도 적용값은 유지된다. **앱 종료 후 저장·복원은 이번 범위에 없으며 실행 중 설정**이다.

## ROI 색상과 두께

역할별 행과 선택 상태별 열을 분리했다.

| 역할 | 선택 안 됨 기본색 | 단일 선택 기본색 | 다중 선택 기본색 |
|---|---|---|---|
| 일반 ROI | 회색 `#A8BAD6` | 파랑 `#3885FF` | 주황 `#FFB82E` |
| 자식 ROI | 초록 `#33CC99` | 밝은 초록 `#1AFFBF` | 주황 `#FFB82E` |
| 부모 ROI | 핑크 `#F472B6` | 진한 핑크 `#D63384` | 주황 `#FFB82E` |

1. 색상 칸에 `#RRGGBB` 또는 알파를 포함한 `#AARRGGBB`를 입력한다.
2. 선 두께는 역할별로 `0.5~8 DIP`를 입력한다. 선택 윤곽에는 `0.5 DIP`가 더해진다.
3. **ROI 표시 적용**을 누르면 색상·두께를 함께 반영한다. 하나라도 잘못되면 오류를 표시하고 이전 팔레트를 유지한다.
4. **ROI 기본값**은 이 표의 색상과 1 DIP 두께를 복원한다.

부모는 명시적 자식이 있는 ROI다. 자식이면서 부모인 중간 ROI는 부모 스타일을 사용한다. 공간 포함 여부로 역할을 추측하지 않는다. 다중 선택 기본값은 선택한 ROI 전체를 같은 주황색으로 보여 하나의 선택 묶음임을 구별한다. 역할별 다중 선택 색을 서로 다르게 설정할 수도 있다. 다중 선택 외곽선·회전 핸들은 **일반 ROI 행의 다중 선택 색**을 따른다. 이름표 글자는 배경 밝기에 따라 어두운색/흰색으로 바뀐다.

다중 선택 자체는 그룹 생성이 아니다. 실제 ParentId 계층과 그룹화/해제·Undo/Redo는 기존 명령대로 동작한다. 색상 변경은 문서와 이력에 들어가지 않으며 GPU ROI 형상을 다시 생성하지 않는다.

## 이미지 중심 크로스헤어

앱 하단 **크로스헤어 켜기 / 끄기** 버튼을 누른다. 설정 창의 **크로스헤어 표시** 체크와 같은 상태를 공유한다. 설정 창에서 색상과 두께를 입력한 뒤 **크로스헤어 적용**을 누른다. **색상·두께 기본값**은 노란색 1 DIP로 복원하고 현재 표시 여부는 유지한다.

- 기준점: 원본 이미지의 `(Width / 2, Height / 2)`를 화면으로 변환한 위치.
- 가로선: 그 기준점의 Y에서 캔버스 왼쪽 끝부터 오른쪽 끝까지.
- 세로선: 그 기준점의 X에서 캔버스 위쪽 끝부터 아래쪽 끝까지.
- 확대·이동을 따라가며 창 크기가 변하면 선의 끝도 캔버스 크기에 맞게 바뀐다. 이미지 중심이 화면 밖이면 화면 안에 들어오는 축만 보인다. 이미지가 없으면 그리지 않는다.
- 미니맵은 기존처럼 가이드 위에 표시된다. 크로스헤어는 ROI 선택·마우스 입력을 가로채지 않는다.

직접 호스팅할 때는 `Guide` 이름이 앱에서 이미 사용될 수 있으므로 본인 레이어 이름을 정한다. 생성은 한 번만 한다.

```csharp
using VisionCanvas.Layers;

var guide = canvas.Layers.Create("My Image Guide", LayerSpace.Image);
guide.Add(new ImageCrosshairGraphic(new(1, .85f, .2f, 1), Thickness: 1));
guide.IsVisible = true;
guide.IsVisible = false;
```

기존 `CrosshairGraphic.ScreenCenter`는 화면 중심 고정의 짧은 십자선을 제공하는 별도 API로 유지한다. 앱의 새 버튼은 `ImageCrosshairGraphic`을 사용한다.

## 영상 표시

기존 **픽셀 선명하게** 설정을 ConfigPage의 **영상 표시**로 옮겼다. 켜면 픽셀 경계를 그대로 표시하고 끄면 부드럽게 보간한다. 체크 즉시 반영된다. 현재 GPU/CPU(WARP) 표시 장치 정보와 렌더러 재연결 버튼도 이곳에서 확인할 수 있다.

검사 레시피의 검색·판정 조건은 기존 검사 패널에서 편집한다. 이 페이지는 캔버스의 표시 설정을 모은다.

## 기존 검사 결과 레이어에 추가하기

`Inspection`은 Canvas 생성 시 이미 준비되어 있다. 다음 코드를 UI 스레드에서 실행한다.

```csharp
using System.Numerics;
using VisionCanvas.Layers;
using VisionGeometry;

var inspection = canvas.Layers["Inspection"];
var id = inspection.Add(new ShapeGraphic(
    new RoiGeometry(new PointD(500, 300), 120, 80, 15),
    new Vector4(0, 1, 0, 1), Thickness: 2));

inspection.Remove(id); // 추가한 도형만 제거
// inspection.Clear(); // 기존 검사 결과까지 모두 제거
```

같은 결과의 화면 이동·확대에서는 추가 항목이 유지된다. **새 검사 결과를 제출하면 Inspection 전체가 교체되므로 추가한 항목도 사라진다.** 독립적으로 유지할 표시는 별도 레이어에 둔다. 실행 예제는 `samples/VisionCanvas.Sample`의 **검사 레이어에 도형 추가** 버튼이다.

전체 API와 ID/일괄 게시 규칙은 [레이어 가이드](drawing-layers-guide.md), 검증과 남은 범위는 [검증 기록](configuration-validation.md)을 따른다.
