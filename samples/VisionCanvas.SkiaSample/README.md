# VisionCanvas Skia 샘플

[Skia 코드 교육 HTML](../../manual/skia/index.html): 공통 ROI·영상과 CPU raster·Gray16 표시·반복 경로 재사용을 실제 코드와 실습으로 배운다.

**공통 조작:** 방향키1px / Shift+방향키10px 이동, 영상 갱신을 유지하는 `검사 화면 잠금`, 선택을 바꾸지 않는 `ROI 위치 보기 → ROI 맞춤 / 전체 보기`를 제공합니다. [사용법·통합 API·검증](../../docs/canvas-interaction-modes.md).

Windows x64 / .NET 10 WPF 앱. 기존 문서·ROI 편집기를 사용하고 캔버스는 SkiaSharp CPU raster로 그린다.

```powershell
dotnet build VisionCanvas.Skia.slnx -c Release
dotnet run --project samples/VisionCanvas.SkiaSample -c Release
```

빌드 후 `samples/VisionCanvas.SkiaSample/bin/Release/net10.0-windows/VisionCanvas.SkiaSample.exe`로도 실행할 수 있다.

1. `8K 예제`를 누르거나 `파일 열기`로 PNG/JPEG/TIFF/WebP 파일을 연다.
2. `화면 맞춤`, `1:1`, 확대·축소, `화면 이동`을 사용한다. 휠 확대와 가운데 버튼 이동도 가능하다.
3. Rectangle/Circle/Triangle/Polygon을 고르고 `ROI 추가` 또는 `ROI 그리기`를 누른다. 폴리곤은 점을 찍고 `그리기 완료`로 닫는다.
4. `부모 추가/그리기`, 그룹화·해제, 복사·붙여넣기·삭제와 Undo/Redo를 사용한다. 그룹 하나를 선택한 상태의 ROI 추가/그리기는 그 그룹에 연결한다.
5. 선택 핸들로 크기·각도를 바꾼다. `0° 초기화`, `취소`, Ctrl+Z/Ctrl+Y도 같은 공통 편집 이력을 사용한다.
6. `1억 ROI 추가`를 누르면 현재 이미지 중심에 10,001×10,001 = **100,020,001개**를 추가한다. 기존 ROI는 유지하며 한 번의 Undo로 되돌린다. 여러 번 추가하거나 복사·붙여넣기하면 2억 개 이상도 가능하다. 가로·세로가 556px 미만인 이미지는 거부한다.

반복 ROI는 행·열과 개별 변경값으로 저장한다. 축소 상태에서는 요약 윤곽을 보고, 휠로 충분히 확대하면 개별 셀을 선택·이동·회전·삭제할 수 있다. `전체 선택`·영역 선택·복사·붙여넣기와 Undo/Redo도 같은 문서에서 동작한다. 1억 개의 임의 도형 객체를 메모리에 펼치거나 한 화면에 1억 윤곽을 전부 그리는 방식은 아니다.

`App`은 조립·종료, `ViewModels/EditorViewModel`은 상태·명령, `Views/EditorWindow`는 바인딩·키 입력·UI 수명을 맡는다. 도형·경계·그룹·클립보드·이력은 `VisionCanvas.Core`를 재사용한다. 진단은 `tests/VisionCanvas.Skia.Tests`에 있으며 샘플 앱에 넣지 않았다.

다른 WPF 앱에서는 `SkiaViewport`의 `Scene`, `PointerInput`, `LayoutState`, `KeyAction`을 바인딩한다. `IImageDisplay`/`IFramePresenter` 계약은 기존 Canvas와 같다. `SetSource`는 성공 시 타일 소유권, `PublishFrame`은 프레임 소유권을 받는다. 생산자를 먼저 종료하고 컨트롤을 해제한다.

대용량 파일은 공통 디스크 피라미드·512px 타일 경로다. Gray8은 단일 채널로 표시하며 Gray16은 원본을 유지하고 표시 복사본만8비트 정규화한다. 파일 캐시는 `%LOCALAPPDATA%/VisionCanvas.SkiaSample/ImageCache`다.

현재 CPU 렌더러이며 GPU 가속·60FPS·DX12와 픽셀 전체 동일성은 확인한 결과가 아니다. 공통 HWND 입력을 재사용하므로 `SkiaViewport`는 기존 이름의 `Dx12Viewport`를 상속하지만 DX12 장치/셰이더를 생성하지 않는다. WPF는 앱 UI, GDI는 Skia가 완성한 프레임의 화면 복사만 맡는다. 실제 카메라 수집·검사 알고리즘·필터 설정 UI는 이 샘플에 포함하지 않는다.

[설계 결정](../../docs/decisions/0067-skia-canvas-and-sample.md), [검증·제약](../../docs/skia-canvas-validation.md), [배포 고지](../../THIRD-PARTY-NOTICES.md).

## 코드 읽는 순서

1. `App.xaml.cs`: 편집기와 이미지 서비스를 만들고 창에 연결한다.
2. `ViewModels/EditorViewModel.cs`: 버튼을 Core 명령에 연결한다. `AddRepeatedGrid`는 반복 ROI 추가, `PasteAsync`는 준비·취소·확정을 담당한다.
3. `SkiaRenderWorker`: 최신 장면을 받아 이미지 준비 → 그리기 → 화면 복사 순으로 실행한다. `UpdateRetainedFrame`은 이전 영상 프레임을 언제 해제하는지 보여 준다.
4. `SkiaRepeatedRois.DrawGrid`: 실제 셀 범위 읽기 → 보이는 셀 찾기 → 윤곽 경로 만들기 → 그리기다. 좌표와 회전은 공통 Core 값을 사용한다.
5. `SkiaSprites`: 불투명 사각 이미지는 한 번에 그린다. 회전·투명도·색 결합이 있는 도형은 기존 삼각형 경로를 사용한다.

반복문 안에서 배열이나 회전 변환을 새로 만들지 않는다. Skia 배열·경로·paint는 렌더 스레드가 재사용하고 종료 시 해제한다. [성능 전후 비교와 실패한 실험](../../docs/skia-canvas-performance.md)을 함께 읽으면 이런 선택의 이유를 확인할 수 있다.
