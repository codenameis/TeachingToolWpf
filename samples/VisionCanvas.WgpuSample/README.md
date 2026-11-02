# VisionCanvas wgpu WPF 앱

[wgpu 코드 교육 HTML](../../manual/wgpu/index.html): 공통 ROI·영상과 native WebGPU·WGSL·제출 슬롯을 실제 코드와 실습으로 배운다.

**공통 조작:** 방향키1px / Shift+방향키10px 이동, 영상 갱신을 유지하는 `검사 화면 잠금`, 선택을 바꾸지 않는 `ROI 위치 보기 → ROI 맞춤 / 전체 보기`를 제공합니다. [사용법·통합 API·검증](../../docs/canvas-interaction-modes.md).

파일 열기·8K 합성 예제·이동/확대·회전 ROI·그룹·Undo/Redo를 제공하는 독립 MVVM 샘플이다. 실제 wgpu-native GPU 렌더러를 사용하며 현재 backend는 Windows DX12다.

저장소 루트에서:

```powershell
dotnet run --project samples/VisionCanvas.WgpuSample -c Release
```

또는 `VisionCanvas.Wgpu.slnx`를 열고 이 앱을 시작 프로젝트로 지정한다. 빌드된 실행 파일은 `bin/Release/net10.0-windows/VisionCanvas.WgpuSample.exe`다.

**1억 ROI:** `8K 예제` → `1억 ROI`를 누르면 기존 문서에100,020,001개의 압축 반복 ROI를 추가한다. 확대 후 더블클릭으로 개별 선택·이동·크기·회전·삭제할 수 있고 복붙/Undo/Redo를 유지한다. 전체 보기에서는 요약 격자를 표시한다. 일반 객체1억 개 할당이나 지속60FPS 검증은 아니다.

```powershell
dotnet test tests/VisionCanvas.Wgpu.Tests -c Release --filter FullyQualifiedName~HundredMillionTests
```

[전용 매뉴얼](../../manual/wgpu/README.md): 실행 조건·조작·WPF 통합·소유권·배포.
[검증](../../docs/wgpu-validation.md): 실제 RTX 3060, 8K/ken32, 원본 픽셀·회귀와 성능 미검증 범위.

App은 서비스 조립/종료, VM은 화면 상태/명령, View는 UI 입력/바인딩을 맡는다. Model은 공통 Core를 재사용한다. 테스트·계측은 별도 `VisionCanvas.Wgpu.Tests`에 있고 앱에는 진단 실행 코드를 넣지 않았다.
