# 재사용 이상 감지 화면 컨트롤

`VisionAnomaly.Controls`는 WPF와 기존 `VisionCanvas` 표시 기능을 참조한다. `VisionAnomaly` 엔진이나 `AnomalyMonitor` 앱을 참조하지 않는다. 수집/검사 알고리즘과 알람 판정은 호스트가 연결한다.

## CameraMonitorControl

`Title`, `StatusText`, `DetailText`, `IsSelected`, `SelectCommand`를 외부 ViewModel에 바인딩한다. 영상은 `Present`로 공급한다.

```csharp
// frame은 BGRA32 원본 프레임. 좌표는 원본 이미지 픽셀이다.
// Present 호출 이후 frame의 소유권은 컨트롤로 넘어간다.
monitor.Present(frame,
    [new VideoAnnotation(new System.Windows.Rect(100, 80, 120, 90), "이상 후보")],
    new System.Windows.Rect(0, 0, 1920, 1080));
```

`Present`는 수집 worker에서 호출할 수 있다. 이미 소유한 프레임을 다른 소비자도 사용한다면 `Retain()`한 참조를 넘긴다. 표시 대기 중 이전 프레임은 반환하고 최신 하나만 보관한다. 관심 영역은 민트색, 이상 후보는 주황색 사각형이다. `VideoAnnotation.Label`은 공급자 설명이며 현재 화면은 사각형만 그린다. 탐색/ROI 편집 컨트롤은 아니다.

`Clear()`와 `Dispose()`는 UI 스레드에서 호출한다. 호스트는 먼저 생산자를 중지하고 구독을 해제한 뒤 컨트롤을 해제해야 한다. `RendererStatusChanged`에서 실제 장치 상태를 받고 `Viewport`를 통해 기존 Canvas의 표시 진단을 사용할 수 있다.

실제 연결 예는 [MonitorLink](../AnomalyMonitor/Services/MonitorLink.cs), 조립/종료 예는 [App](../AnomalyMonitor/App.xaml.cs)를 참고한다.

## AlarmTimelineControl

`ItemsSource`와 양방향 `SelectedItem`을 제공한다. 항목은 표시용 `Time`, `ShortTitle` 속성을 갖는다. 선택과 검토 명령·저장은 호스트가 소유한다. 알람 판정/영상 저장을 컨트롤 안에서 수행하지 않는다.
