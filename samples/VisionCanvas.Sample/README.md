# VisionCanvas 독립 WPF 호스트

`VisionCanvas`로 표시·ROI 편집·외부 overlay를 조립하고, 파일 열기를 위해 `VisionImaging`을 명시적으로 참조하는 실행 예제다. 공통 기하·영상 계약과 편집 코어는 전이 참조로 사용한다. `TeachingToolWpf`, 검사 서비스, 알고리즘, CUDA, friend/internal API는 사용하지 않는다.

Windows x64, .NET 10, DX12 장치가 필요하다. 파일 열기는 기존 `VisionImaging.ImageLoader`를 사용하며 실행 호스트에 고정 버전 `NetVips.Native.win-x64`를 배포한다. NetVips의 고지는 저장소 `THIRD-PARTY-NOTICES.md`를 따른다. 필터를 실행하지 않으므로 OpenCV native runtime은 추가하지 않는다.

카메라나 자체 버퍼를 표시하는 호스트는 `VisionCanvas`만 참조하면 된다. `VisionImaging.Abstractions`의 `VideoFrame`을 `PublishFrame`으로 보내거나 직접 구현한 `ITileSource`를 `SetSource`로 이전한다. NetVips를 거치는 래퍼나 전체 픽셀 복사는 필요하지 않다. 파일 로더 계약만 필요하면 `VisionImaging.Abstractions.IImageLoader`를 사용한다.

```powershell
dotnet run --project samples/VisionCanvas.Sample -c Release
```

- 파일 열기, 8192×8192 / 24576×8192 절차적 샘플, 확대·축소·화면 맞춤
- 사각형·원 추가, 클릭으로 폴리곤 그리기, 선택한 ROI의 자식 추가
- 목록 다중 선택, 부모/자식 회전·연쇄 삭제, Undo/Redo, 편집 취소
- 호스트가 만든 윤곽·점·색·문구를 읽기 전용 overlay로 전달

샘플은 `EditorSession`과 `InteractionController`를 만들어 `Dx12Viewport.Input`에 연결하고, 변경된 불변 상태를 `RenderScene`으로 제출한다. GPU/타일 worker의 수명은 컨트롤이 소유한다. `SetSource`로 전달한 원본은 호스트가 다시 읽거나 해제하지 않는다. 이미지 교체 Undo/Redo는 복원할 파일을 독립적으로 다시 연 뒤에만 이력 위치를 변경한다. 원본 파일이 사라지거나 변경되면 복원을 실패로 표시한다.

검사 예제는 아니다. `결과 표시` 버튼은 알고리즘 없이 임의 overlay를 생성한다. 검사 실행과 판정은 실제 호스트에서 별도로 구현하고, 결과를 이런 표시 데이터로 변환하면 된다.

## 독립 실행 smoke

```powershell
dotnet run --project samples/VisionCanvas.Sample -c Release -- --smoke --gpu-debug --output artifacts/vision-canvas-sample
```

정상 표시 창에서 DX12 준비를 기다린 후 8K/24K 합성 타일 표시, 점 입력 폴리곤, 계층 회전·삭제·Undo/Redo·취소, 이미지 교체 이력, 작은 PNG 파일 읽기, 실제 overlay의 Present/GPU fence 완료와 화면 캡처를 확인한다. `report.json`, `smoke.log`, PNG 캡처를 기록하고 성공은 exit 0, 실패는 exit 1로 종료한다.

24K는 전체 크기의 픽셀 배열을 만들지 않는 기존 `ProceduralTileSource`다. 관측하는 최대 대여 타일 배열 1 MiB는 프로세스/GPU 전체 메모리 상한이 아니다. 합성 데이터·작은 PNG의 기능 검증으로 24K 실파일·실제 카메라·성능 목표·물리 화면 scan-out 검증을 대신하지 않는다. 마우스/키보드의 물리 입력은 별도 수동 검증 대상이다.
