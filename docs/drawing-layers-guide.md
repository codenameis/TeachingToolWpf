# 이름 있는 표시 레이어 사용 가이드

2026-10-06. `VisionCanvas.Controls.Dx12Viewport.Layers`로 표시 항목을 추가·교체·제거한다. 편집 가능한 ROI와 표시용 도형은 별개다. 표시 레이어는 선택·hit test·문서·Undo/Redo에 참여하지 않는다. 검사 알고리즘이나 새 GPU 장치를 만들 필요가 없다.

## 앱과 예제에서 먼저 사용하기

- TeachingToolWpf 하단 **크로스헤어 켜기 / 끄기** 버튼으로 십자선을 전환한다. **원본 이미지 중심**을 지나며 캔버스 전체 너비·높이까지 이어진다. 이미지 확대·이동을 따라가고, 이미지 중심이 화면 밖이면 화면 안에 들어오는 축만 표시한다. 이미지가 없으면 표시하지 않는다.
- 상단 **설정** 버튼은 별도 `ConfigPage`를 연다. 일반/자식/부모 ROI 각각의 기본색·단일 선택색·다중 선택색과 두께, 크로스헤어 표시·색상·두께, 픽셀 보간 설정을 모았다. 자세한 조작은 [설정 가이드](configuration-guide.md)를 따른다.
- ROI를 마우스로 그리거나 드래그로 여러 ROI를 선택할 때 현재 마우스 사각 영역만 불투명도 8%로 채운다. 마우스를 놓거나 Esc/포커스 상실로 취소하면 채우기가 사라진다. 원·삼각형 생성에서도 채우기는 **드래그 범위 사각형**이며 실제 ROI 윤곽은 별도로 표시한다. 클릭으로 꼭짓점을 입력하는 폴리곤에는 새 면 채우기를 적용하지 않는다.
- 독립 예제는 다음 명령으로 실행한다. **레이어 도형 추가 → 최근 도형 제거 / 레이어 비우기**, **결과 표시 → 검사 레이어에 도형 추가 → 검사 표시 지우기**, **크로스헤어**를 각각 시험할 수 있다.

```powershell
dotnet run --project samples/VisionCanvas.Sample -c Release
```

## 기본 사용

아래 `canvas`는 화면에 배치한 `Dx12Viewport` 인스턴스다. 일반 이미지·ROI 화면의 `RenderScene` 제출은 기존처럼 호스트가 담당한다. 레이어는 한 번 생성하여 재사용한다.

```csharp
using System.Numerics;
using VisionCanvas.Layers;
using VisionGeometry;

var results = canvas.Layers.Create("My Results", LayerSpace.Image);
var guides = canvas.Layers.Create("My Guides", LayerSpace.Image);

var green = new Vector4(0, 1, 0, 1); // RGBA, 각 성분 0~1
var shape = new RoiGeometry(new PointD(500, 300), 120, 80, 25);
var id = results.Add(new ShapeGraphic(shape, green, Thickness: 2));

results.Update(id, new ShapeGraphic(shape.WithFrame(new(520, 300), 120, 80, 30), green, 2));
results.Remove(id);  // 이 항목만 제거. 없는 ID는 false
results.Clear();    // 이 레이어의 내용만 제거. 가이드와 ROI는 유지

guides.Add(new ImageCrosshairGraphic(new(1, .85f, .2f, 1), Thickness: 1));
guides.IsVisible = false; // 내용을 유지한 채 숨김
guides.IsVisible = true;
```

`Create` 이름은 대소문자를 구분하고 중복 이름·공백 이름을 거부한다. 이미 만든 레이어는 `canvas.Layers["My Results"]`로 얻는다. 레이어 좌표계와 이름은 생성 후 고정한다. 레이어 자체를 제거하는 API는 이번 범위에 없으며 `Clear`로 내용을 비운다.

## 지원하는 표시 항목과 좌표

| 타입 | 용도 | 크기·좌표 규칙 |
|---|---|---|
| `LineGraphic` | 두 점 사이 선 | 점은 레이어 좌표, 선 두께는 DIP |
| `ShapeGraphic` | 사각형·원·삼각형·닫힌 폴리곤의 윤곽 | `RoiGeometry`의 실제 윤곽. 각도는 도. 선 두께는 DIP |
| `TextGraphic` | 텍스트 | 위치는 레이어 좌표, 글자 크기는 기존 라벨의 DIP 크기 유지 |
| `CrosshairGraphic` | 위치 십자선 / 화면 중앙 조준선 | 위치는 레이어 좌표, 팔 길이·중앙 간격·선 두께는 DIP |
| `ImageCrosshairGraphic` | 이미지 중심을 지나는 전체 캔버스 십자선 | Image 레이어 전용. 원본 중심을 변환하고 선의 길이는 캔버스 크기를 사용 |
| `ResultGraphic` | 기존 `ResultOverlay` 표시 묶음 | Image 레이어 전용. 묶음의 윤곽·라벨·방향·특징점을 함께 표시 |

`LayerSpace.Image`는 원본 이미지 픽셀 좌표이므로 확대·이동을 따른다. `LayerSpace.Screen`은 캔버스 좌상단 기준 DIP이며 이미지 변환의 영향을 받지 않는다. DIP와 물리 디스플레이 픽셀은 DPI에 따라 달라진다. `CrosshairGraphic.ScreenCenter`는 Screen 레이어에서만 사용할 수 있다. `ArmLength`는 중심에서 팔 끝까지, `Gap`은 중심에서 팔 시작까지의 거리다.

색·좌표·선 두께의 NaN/Infinity와 잘못된 범위를 진입점에서 거부한다. `ShapeGraphic`은 기존 `RoiGeometry`의 유효성 조건을 그대로 사용한다. 사용자 도형을 검사 판정이나 ROI 계층에 자동 연결하지 않는다.

## 여러 항목을 한 번에 게시하기

```csharp
CanvasGraphic[] graphics =
[
    new LineGraphic(new(100, 100), new(300, 100), Vector4.One, 2),
    new ShapeGraphic(new(new(250, 200), 80, 80, 0, RoiShape.Circle), new(0, 1, 0, 1)),
    new TextGraphic(new(210, 145), "정상 · 0.96", Vector4.One)
];

var ids = results.AddRange(graphics);  // 기존 항목 뒤에 추가, 입력 순서대로 ID 반환
results.Remove(ids[1]);
var newIds = results.ReplaceAll(graphics); // 이전 항목 전체 교체, 새 ID 반환
```

일괄 작업은 입력 열거와 유효성 검사가 전부 성공한 뒤 한 번 게시한다. 도중 실패하면 기존 항목·ID·화면 스냅샷을 유지한다. `ReplaceAll` 성공 후에는 이전 ID가 유효하지 않다. `Update`는 해당 ID와 표시 순서를 유지하며, 없는 ID는 false다. 잘못된 새 표시 데이터는 ID 존재 여부와 관계없이 예외다.

레이어는 생성 순서, 항목은 추가 순서로 겹쳐 그린다. 나중에 만든 레이어가 앞에 나온다. 기본 `Inspection` 레이어가 가장 먼저 생성된다. 사용자 레이어는 기존 이미지·ROI·편집 핸들 위, 미니맵 아래에 그려진다. 임의 z-index·마우스 입력 가로채기는 추가하지 않았다.

## 기존 검사 결과와 연결하기

기존 `RenderScene.Inspection`을 제출하는 앱도 그대로 동작한다. Canvas가 이 값을 **기본 `Inspection` 레이어의 `ResultGraphic` 한 개**로 연결한다.

**기존 검사 결과를 남긴 채 같은 레이어에 추가하는 샘플**이다. `Create`를 호출하지 않는다. UI 스레드에서 실행하며, 반환 ID로 방금 추가한 도형만 제거할 수 있다.

```csharp
using System.Numerics;
using VisionCanvas.Layers;
using VisionGeometry;

var inspection = canvas.Layers["Inspection"];
var markerId = inspection.Add(new ShapeGraphic(
    new RoiGeometry(new PointD(500, 300), 120, 80, 15),
    new Vector4(0, 1, 0, 1), Thickness: 2));

inspection.Remove(markerId); // 추가한 도형만 제거, 기존 검사 결과 유지
// inspection.Clear();      // 기존 검사 결과를 포함한 이 레이어의 모든 표시 제거
```

실행 가능한 동일 경로는 독립 예제의 `SampleWindow.AddInspectionGraphic`과 **검사 레이어에 도형 추가** 버튼이다. 같은 검사 결과 인스턴스로 장면을 다시 제출해도 추가 항목이 유지된다. **다음 검사에서 새 결과를 제출하면 Inspection 전체가 교체되므로 이 추가 도형도 지워진다.** 검사 실행 사이에도 유지할 표시는 별도 사용자 레이어에 둔다.

```csharp
var inspection = canvas.Layers["Inspection"]; // 이미 존재하므로 Create하지 않는다
inspection.IsVisible = false;
inspection.IsVisible = true;
inspection.Clear();
```

`Clear` 후 같은 `ResultOverlay` 인스턴스로 확대·이동 장면을 다시 제출해도 지운 결과가 되살아나지 않는다. 호스트가 새로운 결과 인스턴스를 제출하면 이 레이어 내용만 교체한다. 숨김 상태는 새 결과에도 유지한다. 호스트가 기존 결과를 null로 변경하면 기존 검사 표시를 지운다.

검사 결과를 직접 소유하는 새 호스트는 자체 레이어를 만들고 다음처럼 추가할 수 있다.

```csharp
// resultOverlay는 호스트가 만든 VisionCanvas.Rendering.ResultOverlay
var resultId = results.Add(new ResultGraphic(resultOverlay));
results.Remove(resultId); // 이 ResultOverlay 묶음 전체 제거
```

검출 항목별로 제거하려면 각각 별도로 `Add`하여 반환 ID를 보관한다. `ResultGraphic` 한 개에 포함한 여러 `MatchMarker`는 하나의 표시 묶음이며 개별 ROI ID로 제거되지 않는다. 하나의 객체를 윤곽과 문자로 나누어 추가했다면 반환된 ID들을 호스트가 함께 관리한다.

기존 검사 완료 계측(`OverlayPresented`)은 기본 `Inspection` 레이어의 첫 `ResultGraphic`과 `BatchLabels=true`에 대해 유지한다. 사용자 레이어의 일반 Add/Remove 완료를 GPU fence나 물리 화면 표시 완료로 해석하지 않는다. 기존 검사 결과와 사용자 가이드는 독립적이다.

## 스레드·수명·성능

- 변경 API는 해당 Canvas의 **UI 스레드**에서 호출한다. 검사 계산은 worker에서 수행하고 완성한 표시 데이터를 `canvas.Dispatcher.InvokeAsync(() => results.ReplaceAll(graphics))`로 게시한다. 렌더 스레드는 변경 중인 컬렉션을 읽지 않고 불변 스냅샷을 읽는다.
- 첫 장면을 제출한 이후에는 Add/Remove/Clear/숨김 변경만으로 화면이 갱신된다. 별도 `Submit` 호출이 필요 없다. 창 레이아웃 변경 때의 원래 `RenderScene` 제출은 계속 유지한다.
- 레이어 변경은 원본 이미지·ROI·선택·Undo/Redo를 바꾸지 않는다. 사용자 레이어는 이미지 교체 때 자동으로 지우지 않으므로, 새 원본에 이전 표시가 붙으면 안 되는 호스트는 **이미지 교체 성공 시 해당 Image 레이어를 Clear**한다. 독립 예제는 이 정책을 적용하며 Screen 가이드는 유지한다.
- 레이어별 화면 크기 텍스처를 만들지 않고 기존 DX12 배치에 함께 그린다. 변경하지 않은 레이어 데이터와 형상 윤곽은 재사용한다. 수천 항목은 개별 Add를 반복하기보다 AddRange/ReplaceAll로 한 번 게시한다.
- 일반 사용자 표시 레이어는 ROI의 BVH/LOD 캐시를 대신하는 대량 편집 엔진이 아니다. 항목 수·문자 종류·표시 면적에 비례하는 비용이 있으며, 대량 일괄 검증/구성의 UI 시간도 측정해야 한다. 이번 기능으로 전체 27만 ROI 작업의 60FPS 달성을 선언하지 않는다.

검증한 범위와 반투명 on/off 측정은 [검증 기록](drawing-layers-validation.md), 코드 시작점은 [레이어 모델](../src/VisionCanvas/Layers/DrawingLayer.cs), [표시기](../src/VisionCanvas/Rendering/DrawingLayerPainter.cs), [독립 예제](../samples/VisionCanvas.Sample/SampleWindow.cs)다.
