# VisionCanvas 실전 사용법 매뉴얼 (USAGE GUIDE)

이 문서는 `VisionCanvas` 라이브러리를 **실제 사용하는 개발자 입장(API & MVVM 통합)**과 **프로그램을 조작하는 최종 사용자 입장(마우스·키보드 조작법)**에서 정리한 실전 매뉴얼입니다.

---

## Part 1. 개발자 입장: 내 WPF MVVM 앱에 VisionCanvas 붙이기

### 1단계. 프로젝트 참조 및 렌더러 선택하기
`VisionCanvas`는 공통 코어(`VisionCanvas.Core`, `VisionCanvas.Host`) 위에 4가지 렌더러 컨트롤을 제공합니다. 프로젝트 목적에 맞는 뷰포트 컨트롤 하나를 선택하세요.

| 렌더러 프로젝트 | XAML 컨트롤 타입 | 특징 및 권장 환경 |
|---|---|---|
| `VisionCanvas` | `<canvas:Dx12Viewport />` | **기본 권장**. Windows Direct3D 12 하드웨어 가속 + GPU 미지원 PC에서 자동 WARP(CPU) 폴백 지원 |
| `VisionCanvas.Vulkan` | `<canvas:VulkanViewport />` | Vulkan 1.2+ 네이티브 swapchain 및 SPIR-V 셰이더 기반 고속 렌더링 |
| `VisionCanvas.Wgpu` | `<canvas:WgpuViewport />` | 차세대 WebGPU(`Alimer.Bindings.WebGPU`) 및 WGSL 셰이더 기반 렌더링 |
| `VisionCanvas.Skia` | `<canvas:SkiaViewport />` | 전용 스레드 Skia CPU 래스터라이저 기반 (GPU 드라이버 제약이 있는 환경에 적합) |

### 2단계. 핵심 객체 4총사 이해하기 (`App.xaml.cs` 조립)
`VisionCanvas`를 구동하려면 `App.xaml.cs`(조립 지점)에서 아래 4개의 핵심 객체를 생성해 ViewModel에 주입합니다.

```csharp
using VisionCanvas.Core.Alignment;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Interaction;
using VisionCanvas.Presentation; // VisionCanvas.Host 프로젝트에 포함된 공통 MVVM 코디네이터

// 1. EditorSession: ROI 추가·삭제·그룹화·이동·회전과 Undo/Redo 이력을 관리하는 두뇌
var session = new EditorSession();

// 2. InteractionController: 마우스 Down/Move/Up/Wheel 이벤트를 받아 선택·그리기·이동 제스처를 수행하는 상태 머신
var input = new InteractionController(session);

// 3. ViewportLayout: 현재 뷰포트의 화면 가로(Width)·세로(Height) DIP 크기와 모니터 DPI 배율을 보관
var layout = new ViewportLayout();

// 4. ImageCoordinator: 이미지 파일(또는 8K 합성 예제) 비동기 로딩, 타일 소스 수명 관리, 이미지 교체 Undo/Redo 연결
var images = new ImageCoordinator(session, layout, loader, viewportControl);
```

### 3단계. XAML에서 뷰포트 바인딩하기 (`EditorView.xaml`)
ViewModel이 제공하는 5가지 핵심 속성을 뷰포트 컨트롤의 DependencyProperty에 바인딩합니다.

```xml
<UserControl x:Class="MyApp.Views.EditorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:canvas="clr-namespace:VisionCanvas.Controls;assembly=VisionCanvas"
             PreviewKeyDown="OnPreviewKeyDown">
    <Grid>
        <canvas:Dx12Viewport x:Name="Canvas"
                             Scene="{Binding Scene}"
                             PointerInput="{Binding Input}"
                             LayoutState="{Binding Layout}"
                             KeyAction="{Binding KeyAction}"
                             IsInputEnabled="{Binding IsInputEnabled}" />
    </Grid>
</UserControl>
```

#### 각 바인딩 속성의 역할
- **`Scene` (`RenderScene`)**: 현재 화면에 무엇을 그릴지 담은 불변 패키지(`DocumentState`, `RoiTransformPreview`, `InteractionOverlay`, 화면 크기, DPI, `RoiRenderStyle`). ViewModel에서 상태가 바뀔 때마다 `Scene = new RenderScene(...)`으로 갱신합니다.
- **`PointerInput` (`IViewportInput`)**: `InteractionController`를 연결합니다. Win32 자식 창에서 발생한 마우스 클릭/드래그/휠이 이곳으로 전달됩니다.
- **`LayoutState` (`ViewportLayout`)**: 창 크기가 바뀌거나 모니터 DPI가 바뀔 때 뷰포트가 자동으로 `Layout.Update(width, height, dpi)`를 호출합니다.
- **`KeyAction` (`Func<Key, bool, bool, bool>`)**: 뷰포트(HWND)가 포커스를 가졌을 때 눌린 키(`Key`, `ctrl`, `shift`)를 ViewModel의 단축키 처리 메서드로 전달합니다.
- **`IsInputEnabled` (`bool`)**: 파일 로딩 중이거나 대량 붙여넣기 준비 중일 때 `false`로 바인딩하면 진행 중인 드래그/캡처를 안전하게 취소하고 입력을 잠급니다.

### 4단계. ViewModel에서 `RenderScene` 갱신하기 (`EditorViewModel.cs`)
`EditorSession`, `InteractionController`, `ViewportLayout`, `ImageCoordinator`의 `Changed` 이벤트를 구독하여 `Scene`을 새로 만듭니다.

```csharp
private void Refresh()
{
    if (_disposed)
    {
        return;
    }

    Scene = new RenderScene(
        _editor.State,
        _editor.TransformPreview,
        _editor.PreviewEpoch,
        Layout.Width,
        Layout.Height,
        Layout.Dpi,
        Input.Overlay);

    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scene)));
}
```

### 5단계. 코드로 ROI 다루기 (`EditorSession` & `ViewportNavigationViewModel` 주요 API)

#### 이미지 및 문서 불러오기
```csharp
// 8K 내장 합성 샘플 이미지 열기
await _images.ReplaceAsync(new ImageReference("sample:8192:8192", "8K 예제", 8192, 8192));

// 이미지와 초기 ROI 목록을 한 번에 검증 후 로드하기
await _images.LoadDocumentAsync(cameraImageRef, initialRois);
```

#### ROI 생성·편집·그룹화·이력 API
```csharp
// 1. 사각형·원·삼각형·다각형 ROI 생성 (이미지 좌표계 기준)
var rectGeo = new RoiGeometry(new PointD(4096, 4096), width: 300, height: 200, angle: 0);
Guid roiId = _editor.Add(rectGeo);

// 2. 부모 그룹 ROI 생성 및 자식 ROI 추가 (최대 4단계: RootGroup → ModuleGroup → RoiGroup → Roi)
Guid parentId = _editor.AddParent(new RoiGeometry(new PointD(4096, 4096), 1000, 1000, 0));
Guid childId = _editor.AddChild(parentId, new RoiGeometry(new PointD(4096, 4096), 200, 200, 0));

// 3. 대량 ROI 일괄 추가 (이름표 숨김 옵션 포함)
_editor.AddRange(geometries, selectAdded: true, isNameVisible: false);

// 4. 선택·그룹화·그룹 해제·삭제
_editor.Select([roiId]);
_editor.SelectAll();
_editor.GroupSelection();     // 선택한 ROI들을 묶어 그룹 생성 (또는 기존 부모에 편입)
_editor.UngroupSelection();   // 부모 껍질만 제거하고 자식들은 이미지 좌표 유지한 채 승격
_editor.DeleteSelection();    // 선택한 ROI 삭제 (그룹 선택 시 자손까지 연쇄 삭제)

// 5. 복사·붙여넣기 및 Undo/Redo
_clipboard.Copy(_editor.State);
_editor.Paste(_clipboard);    // 대량 ROI의 경우 PreparePasteAsync + CommitPreparedPaste 사용
await _images.HistoryAsync(redo: false); // Undo (이미지 교체 이력까지 함께 복원)
await _images.HistoryAsync(redo: true);  // Redo
```

#### 화면 이동·맞춤 및 입력 잠금 모드 (`ViewportNavigationViewModel`)
검사 화면처럼 **사용자가 ROI를 실수로 옮기지 못하게 막으면서 확대/축소와 화면 이동(Pan)만 허용**하고 싶을 때는 `InteractionController.NavigationOnly` 또는 `ViewportNavigationViewModel`을 사용합니다.

```csharp
// 검사 화면용 탐색 전용 잠금 (ROI 이동/크기조절/삭제는 차단하고 마우스 휠 줌과 팬만 허용)
Input.NavigationOnly = true;

// 선택 상태를 바꾸지 않고 특정 ROI나 영역으로 카메라만 부드럽게 맞춤 이동
Navigation.FocusRoi(targetRoiId);
Navigation.FitImage();
```

#### 커스텀 오버레이 그리기 (`DrawingLayerCollection`)
ROI 문서나 Undo/Redo 이력에 영향을 주지 않고, 검사 결과 십자선·합격/불합격 박스·안내선 등을 화면 위에 즉시 그리고 싶다면 `viewport.Layers`를 사용합니다.

```csharp
// 이미지 좌표계("Image") 또는 화면 좌표계("Screen") 레이어 가져오기/생성
DrawingLayer layer = viewport.Layers["Inspection"];
layer.Clear();

// 사각형, 원, 선분, 십자선(Crosshair), 텍스트 그래픽 추가
layer.Add(CanvasGraphic.Rectangle(new BoundsD(100, 100, 300, 300), strokeColor: 0xFF32CD32, thicknessDip: 2.0));
layer.Add(CanvasGraphic.Crosshair(new PointD(200, 200), sizeDip: 12.0, strokeColor: 0xFFFF6347));
```

---

## Part 2. 사용자 입장: 마우스 & 키보드 조작법 총정리

### 1. 마우스 조작법

| 동작 | 마우스 조작 방법 | 내부 동작 원리 |
|---|---|---|
| **단일 선택** | ROI 윤곽선 또는 내부 클릭 | 3 DIP 오차 범위 내의 최상위 도형 선택 |
| **겹친 도형 순환 선택** | 같은 위치를 **더블클릭** 또는 **Alt + 클릭** | 겹쳐 있는 부모 그룹 ↔ 자식 ROI를 순서대로 순환 선택 |
| **다중 선택 추가/토글** | **Ctrl + 클릭** 또는 빈 공간에서 **드래그(마키 선택)** | 마키 사각형 안에 완전히 들어온 ROI들을 일괄 선택 |
| **도형 이동** | 선택된 ROI 내부를 잡고 드래그 (**Shift** 누르면 가로/세로 축 고정) | 자식 ROI는 부모 도형 벽을 뚫고 나갈 수 없으며 벽면을 따라 미끄러짐(Sliding) |
| **크기 조절 (Resize)** | 선택된 ROI의 **흰색 사각형 핸들(8방향)** 드래그 | 회전된 로컬 좌표계 기준으로 크기 변경 |
| **회전 (Rotate)** | 선택된 단일 ROI/그룹 상단의 **원형 회전 핸들** 드래그 | 중심점(Pivot) 기준 회전, 부모/이미지 경계 이탈 시 첫 접촉 각도에서 정지 |
| **다각형(Polygon) 그리기** | 다각형 도구 선택 후 꼭짓점마다 클릭 → 시작점 클릭 또는 **Enter** | 선분이 스스로 교차해 닫히면(`PolygonClosure`) 자동으로 다각형 완성 |
| **화면 확대/축소 (Zoom)** | 마우스 **휠 위/아래** 굴리기 | 마우스 커서가 가리키는 이미지 픽셀 위치를 고정한 채(`ZoomAt`) 배율 변경 |
| **화면 이동 (Pan)** | 마우스 **가운데 버튼(휠 클릭) 드래그** 또는 **Space + 좌클릭 드래그** | 뷰포트 오프셋(`Offset`) 이동 |
| **미니맵 이동** | 우하단 미니맵 클릭 또는 드래그 | 전체 이미지에서 해당 위치로 뷰포트 즉시 이동 |

### 2. 키보드 단축키 일람

| 단축키 | 동작 | 비고 |
|---|---|---|
| **방향키 (`←` `↑` `→` `↓`)** | 선택된 ROI **1px** 정밀 이동 | 부모/이미지 경계 제한 동일 적용, 연속 입력 시 이력 합침 |
| **Shift + 방향키** | 선택된 ROI **10px** 빠른 이동 | 정밀 위치 미세 조정용 |
| **Ctrl + A** | 전체 ROI 선택 (`SelectAll`) | 현재 문서의 모든 ROI 선택 |
| **Ctrl + C / Ctrl + V** | 선택한 ROI 복사 / 붙여넣기 | 계층 구조(부모-자식 관계)와 마스크까지 보존하여 복제 |
| **Ctrl + G / Ctrl + Shift + G** | 그룹화 (`Group`) / 그룹 해제 (`Ungroup`) | 최대 4단계 계층 규칙 적용 |
| **Delete** | 선택한 ROI 삭제 | 그룹 삭제 시 하위 자손까지 연쇄 삭제 |
| **Ctrl + Z / Ctrl + Y (Ctrl+Shift+Z)** | 실행 취소 (`Undo`) / 다시 실행 (`Redo`) | 도형 편집뿐만 아니라 이미지 교체까지 원자적으로 복원 |
| **Enter** | 다각형 그리기 완료 | 점 3개 이상 찍었을 때 다각형 확정 |
| **Esc** | 현재 드래그·그리기·로딩 작업 즉시 취소 | 드래그 시작 전 상태로 완벽히 복원 |
| **Ctrl + O** | 이미지 파일 열기 대화상자 호출 | `IImageFilePicker` 연동 |
