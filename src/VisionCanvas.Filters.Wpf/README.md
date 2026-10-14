# VisionCanvas.Filters.Wpf

`.NET 10 / WPF / x64`의 ROI 필터 편집 컨트롤이다. TeachingToolWpf 앱과 CUDA 구현을 참조하지 않는다. VisionCanvas의 ROI 설정·미리보기 계약을 사용하며 컨트롤의 필수 리소스는 자체 제공한다.

```xml
<!-- 호스트 프로젝트 -->
<ProjectReference Include="../VisionCanvas.Filters.Wpf/VisionCanvas.Filters.Wpf.csproj" />
```

```xml
<!-- Window의 xmlns 선언 -->
xmlns:filters="clr-namespace:VisionCanvas.Filters.Wpf.Controls;assembly=VisionCanvas.Filters.Wpf"

<!-- 호스트 ViewModel이 Filters 속성을 제공한다. 컨트롤은 DataContext를 덮어쓰지 않는다. -->
<filters:FilterPanel DataContext="{Binding Filters}" />
```

호스트가 `FilterPreviewViewModel(IRoiFilterPreview processor, EditorSession editor)`을 생성한다. `EditorSession.Changed`에서 `Filters.Update(editor.State, isLive, editor.TransformPreview, pixelKind)`를 호출한다. `Filters.Changed`에서 `Filters.Result`를 viewport에 게시한다. `pixelKind`를 생략하면 임계값 슬라이더는 기본 Gray8 범위를 사용하고 입력된 큰 값을 보존하도록 범위를 확장한다. Gray16 호스트는 `PixelKind.Gray16`을 전달한다.

`SelectedFilter`, `Radius`, `Sigma`, `Threshold`, `EdgeLow`, `EdgeHigh`, `Execution`은 문자열 숫자 입력/선택 바인딩이다. 해당 필터에서 사용하는 필드만 표시한다. 숫자 입력은 포커스를 잃을 때 확정하고 잘못된 값은 `ValidationMessage`에 표시한다. 슬라이더는 실수형 `*Value` 속성과 연결한다.

- `Apply`: 선택된 ROI에 설정을 등록한다. 미리보기 토글을 임의로 켜지 않는다.
- `IsPreviewEnabled`: 전역 보기 상태, 기본 ON. OFF는 선택을 바꾸어도 유지하며 이력에 쌓이지 않는다. 등록된 필터가 없으면 ON이어도 계산하지 않는다.
- `BeginParameterEdit` / `EndParameterEdit` / `CancelParameterEdit`: 슬라이더 이동 중에는 임시 설정으로 계산하고 완료 시 한 번 등록한다. Esc는 설정과 표시를 되돌린다. 마우스/키보드 gesture를 명령으로 바꾸는 책임은 `ParameterSlider`에 있다.
- `Preview` / `Stop`: 기존 호스트의 명령 호출을 위한 명시적 ON+등록 / OFF 진입점이다. 새 패널은 Apply와 토글을 사용한다.
- `WhenIdle`: 단일 worker와 마지막 대기 요청의 완료를 기다린다. 동시 processor 호출은 없다.

호스트는 라이브 영상 갱신 시 `RefreshLiveFrame()`을 호출한다. TeachingToolWpf는 기존 200ms 타이머를 유지한다. ROI/슬라이더 갱신은 실행을 매번 취소하지 않고 대기 요청 하나를 교체한다. 약 100ms 대기 후 최신 요청을 취하며 실제 갱신 주기는 처리 시간에 달려 있다. 처리 중 마지막 완료 결과는 **계산한 위치 그대로** 표시한다. 다른 이미지·선택·확정 파라미터·OFF·종료는 이전 작업을 무효화한다.

종료 순서는 `Filters.Dispose()` → `await Filters.WhenIdle` → processor Dispose → GPU backend Dispose다. 호스트의 이벤트 구독도 해제한다. WPF Closing에서 이미 완료된 Task를 기다리는 경우에도 중첩 Close가 발생하지 않도록 다음 dispatcher 차례에서 닫는다. 실제 연결은 TeachingToolWpf의 MainWindow와 MainWindowViewModel을 참고한다.

현재 필터 목록은 엔진에서 구현된 범위다. 사용자 정의 convolution kernel, 자유 순서 파이프라인 편집기, Flat-field 맵 입력은 추가하지 않았다. [검증·제한](../../docs/filter-controls.md).
