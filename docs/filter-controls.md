# MVVM 필터 컨트롤과 연속 미리보기

2026-10-06 후속. [프로젝트 사용법](../src/VisionCanvas.Filters.Wpf/README.md), [ADR-039](decisions/0039-filter-controls-and-continuous-preview.md).

## 사용자 동작

ROI 선택 → 필터와 파라미터 지정 → **설정 적용**. **미리보기 ON/OFF**로 결과 표시를 제어한다. 기존 등록된 ROI는 기본 ON 상태에서 자동 표시되며, OFF는 ROI 선택을 바꾸어도 유지한다. 보기 토글은 Undo 이력에 들어가지 않는다.

반경, sigma, 임계값, Canny 상·하한은 숫자 입력과 슬라이더를 제공한다. 필터 종류에 맞는 항목만 표시한다. 숫자 입력은 포커스가 빠질 때 확정한다. 슬라이더를 움직이는 동안에는 임시 결과만 갱신하고, 놓으면 최종 파라미터를 한 번 등록하므로 Undo 한 번으로 복원한다. 슬라이더 조작 중 Esc는 임시 변경을 취소한다. 키보드 방향키도 슬라이더를 조절하며 ROI 이동 키로 전달하지 않는다. CPU/CUDA와 Gray16 의미는 기존 [산업용 필터 계약](industrial-roi-filters.md)을 유지한다.

## 갱신 규칙

이전의 요청마다 취소 + 100ms debounce 방식은 연속 포인터 입력에서 계산이 시작되지 않을 수 있었다. 이제 실행 중 계산 하나를 완료하고 대기 요청 하나만 최신으로 교체한다. 100ms 주기의 대기 자체를 새 포인터 입력 때문에 재시작하지 않는다. ROI 이동·회전·크기 변경과 파라미터 조절 도중에도 완료된 결과를 게시한다.

연속 움직임에서는 마지막으로 계산한 결과를 해당 위치에 표시한다. 오래된 픽셀을 새 ROI 위치로 옮겨 계산이 끝난 것처럼 표시하지 않는다. 아직 다음 요청이 있으면 “갱신 중 · 마지막 완료 결과 표시”로 구별하고 정지 후에는 최종 위치까지 처리한다. 이미지/선택/확정 설정 변경, OFF와 Dispose에서는 세대가 바뀌어 늦게 끝난 결과를 게시하지 않는다.

미리보기는 기존 최대 긴 축 1024px, 선택 전체 약 4Mpx다. 라이브 입력 요청은 최대 5회/초이며 처리 시간이 길면 더 낮다. 이 UI의 연속 갱신은 원본 해상도 검사나 60FPS 필터 처리 보장이 아니다.

## 검증

- 전체 솔루션 Release 빌드 경고/오류 **0**, 새 WPF 프로젝트 단독 Debug 빌드, `Format-Code.ps1 -Verify`와 `git diff --check` **PASS**.
- MVVM/컨트롤/실제 CUDA 테스트 **15 PASS, Skip 0**. 독립 WPF 리소스 생성, 주입한 DataContext 유지, 높은 Gray16 임계값이 필터 전환에서 조용히 clamp되지 않음, 토글 OFF의 늦은 출력 무효화, 임시 설정의 실제 픽셀 결과, gesture당 한 번 Undo, Esc 복원, 연속 이동 중 계산 취소 없음과 최종 pose를 검증한다.
- 기존 ROI 필터 어댑터 테스트 **22 PASS**. Gray16 한 단계 명암 차이·색상 순서·회전·마스크·CPU fallback/오류·기존 OpenCV를 포함한다.
- 실제 WPF `--smoke` 종료 **0**. 새 컨트롤의 routed key/toggle 바인딩과 슬라이더 임시/확정/Undo/Esc/잘못된 숫자 입력, 기존 편집·회전·이미지 교체·라이브 경로까지 확인했다.
- RTX3060 Laptop CUDA, 8K 합성 이미지의 2개 ROI 연속 이동에서 **움직임이 끝나기 전에 8개 필터 결과**를 게시했다. 40회 입력(25ms 간격)과 마지막 결과 대기까지 **1256.78ms**. 이것은 기능 검증 횟수이며 물리 마우스 입력 지연이나 p50/p95 성능 측정은 아니다.

첫 전체 smoke는 이전 실행의 캡처 파일이 남아 있어 기존 ROI 검색 캡처에서 파일 공유 오류가 발생했고 새 컨트롤 시험에 도달하지 못했다. 기존 캡처 코드를 변경하지 않고 새 작업 디렉터리에서 재실행하여 전체 종료 0을 확인했다.

증거는 `artifacts/filter-control-tests/*.trx`, `artifacts/filter-controls-run-02/artifacts/filter-control-smoke.json`, 같은 폴더의 `smoke.log`와 `industrial-filter-app.png`다. 보존본: `docs/data/filter-controls-20261006.zip`.

```powershell
dotnet build TeachingToolWpf.slnx -c Release
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --filter 'FullyQualifiedName~FilterPreviewSessionTests|FullyQualifiedName~FilterControlTests|FullyQualifiedName~IndustrialFilterCudaTests'
dotnet test tests/VisionCanvas.Tests -c Release --filter 'FullyQualifiedName~RoiFilterTests|FullyQualifiedName~IndustrialRoiFilterTests'
pwsh -File scripts/Format-Code.ps1 -Verify

# 전체 UI 시험은 매번 빈 작업 디렉터리에서 실행한다.
$runDirectory = Join-Path (Resolve-Path artifacts) ('filter-controls-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$appPath = (Resolve-Path src/TeachingToolWpf/bin/Release/net10.0-windows/TeachingToolWpf.exe).Path
Start-Process -FilePath $appPath -ArgumentList '--smoke' -WorkingDirectory $runDirectory -WindowStyle Hidden -Wait
```

물리 마우스로 다양한 장비에서 입력 지연을 분포 측정하는 작업, 현장 영상 검증, 새 필터 알고리즘의 추가는 이번 컨트롤 연결 범위 밖이며 완료로 표시하지 않는다.

## 취소 예외에서 디버거가 멈출 때

설정 확정·선택 변경·미리보기 OFF는 이전 계산의 취소 토큰을 취소한다. CPU 형태학의 `ThrowIfCancellationRequested()`에서 발생하는 `OperationCanceledException`은 이 취소를 호출자에게 전달한다. `Parallel.For`와 작업 본문은 같은 토큰을 사용하고, `FilterPreviewViewModel.RunAsync`는 자신이 요청한 취소를 처리한 뒤 다음 요청을 실행한다. 검사 코드를 지우거나 부분 결과를 성공으로 반환하지 않는다.

Visual Studio에서 **예외 발생 시 중단**이 켜져 있으면 처리할 예외도 catch에 도달하기 전에 멈출 수 있다. F5로 계속했을 때 정상 갱신되는 경우, 디버그 → 창 → 예외 설정에서 `System.OperationCanceledException`의 발생 시 중단만 해제할 수 있다. 전체 CLR 예외의 중단을 해제할 필요는 없다. 앱 종료나 “미리보기 실패”가 나타나는 경우는 별도로 확인해야 한다. 사용자 PC의 디버거 설정과 실제 중단 유형을 직접 확인한 것은 아니다. [Microsoft 예외 설정 안내](https://learn.microsoft.com/en-us/visualstudio/debugger/managing-exceptions-with-the-debugger?view=vs-2022), [Parallel.For 취소 계약](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/how-to-cancel-a-parallel-for-or-foreach-loop).

2026-10-06 추가 검증: Release x64에서 CPU/계약 **14 PASS**, 미리보기/MVVM 컨트롤 **16 PASS**, Skip 0. 새 테스트 네 경우는 CPU 1/4 스레드의 실제 계산 도중 취소(첫 단계 완료를 확인한 뒤 취소), 출력·유효성 마스크 보존과 재사용, worker의 취소 예외 전달 후 OFF→ON 및 설정 교체를 확인한다. 기존 구현에서 모두 통과하여 운영 코드의 취소 처리를 변경하지 않았다. 증거: `artifacts/filter-cancellation-tests/cpu-cancellation-contracts.trx`, `preview-cancellation.trx`.

```powershell
dotnet test IndustrialVision.Filters/tests/IndustrialVision.Filters.Tests -c Release --filter 'FullyQualifiedName~CpuCancellationTests|FullyQualifiedName~ContractTests'
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --filter 'FullyQualifiedName~FilterPreviewSessionTests|FullyQualifiedName~FilterControlTests'
```

설계 검토: 이번 변경은 테스트와 설명에 한정한다. SOLID의 SRP/OCP/LSP/ISP/DIP 및 GRASP의 Information Expert/Creator/Controller/Low Coupling/High Cohesion/Polymorphism/Pure Fabrication/Indirection/Protected Variations를 확인했다. 기존 UI·CPU·리소스 소유권과 계약의 책임 경계를 바꾸지 않으며, 취소·재사용·출력 계약을 테스트로 확인한다. 새 운영 추상화, 의존성, 설계 원칙 예외는 없다.
