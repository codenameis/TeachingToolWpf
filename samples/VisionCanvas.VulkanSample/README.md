# VisionCanvas Vulkan Sample

[Vulkan 코드 교육 HTML](../../manual/vulkan/index.html): 공통 ROI·영상과 acquire·render pass·동기화를 실제 코드와 실습으로 배운다.

**공통 조작:** 방향키1px / Shift+방향키10px 이동, 영상 갱신을 유지하는 `검사 화면 잠금`, 선택을 바꾸지 않는 `ROI 위치 보기 → ROI 맞춤 / 전체 보기`를 제공합니다. [사용법·통합 API·검증](../../docs/canvas-interaction-modes.md).

기존 Core의 ROI·계층·좌표·Undo/Redo를 공유하는 Vulkan 전용 WPF MVVM 편집 앱이다.

저장소 루트에서 실행한다.

```powershell
dotnet run --project samples/VisionCanvas.VulkanSample -c Release
```

Windows x64, .NET10 Desktop Runtime, Vulkan1.1 지원 GPU/드라이버가 필요하다. `8K 예제` 또는 `파일 열기`로 시작하고 ROI 추가·그리기·그룹·복붙·Undo/Redo를 사용한다.

`8K 예제` → `1억 ROI`로 압축 반복 ROI100,020,001개를 추가한다. 확대 후 셀을 더블클릭하면 개별 이동·크기·회전·삭제가 가능하며 복붙·Undo/Redo도 같은 문서 이력을 사용한다. 일반 객체1억 개의 메모리 할당 시험은 아니다. 자동 검증은 `dotnet test tests/VisionCanvas.Vulkan.Tests -c Release --filter FullyQualifiedName~HundredMillionTests`로 실행한다.

- [Vulkan 전용 매뉴얼](../../manual/vulkan/README.md): 실행·버튼·통합 API·GPU 수명·메모리·배포.
- [검증과 한계](../../docs/vulkan-validation.md): 실제 RTX3060,8K,ken32,기존 DX12 회귀와 미측정60FPS.
- [설계 결정](../../docs/decisions/0066-vulkan-canvas-and-sample.md).

App은 조립과 종료, ViewModel은 상태·명령·취소, View는 UI 연결을 담당한다. 진단은 `tests/VisionCanvas.Vulkan.Tests`에 있다. 패턴 검사 UI와 실제 카메라 수집은 이 샘플의 범위에 포함하지 않는다.
