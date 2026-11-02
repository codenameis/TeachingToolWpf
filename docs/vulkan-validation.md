# VisionCanvas Vulkan 검증 기록

2026-10-16, 현재 `main`에서 로컬 수정·검증. 사용자 요청: 별도 Vulkan Canvas/앱/전용 매뉴얼과 기존 코드 규칙 유지. 커밋·푸시는 하지 않았다. 기존 작업 중인 매칭·도형 검출·미니맵 등의 변경은 보존했다.

## 결과

| 항목 | 결과 | 근거와 범위 |
|---|---|---|
| 실제 Vulkan GPU | PASS | RTX3060Laptop, Vulkan API1.4.312, 드라이버581.83. 프로그램은1.1을 요청. 실제 instance/device/Win32 surface/swapchain 생성·제출·표시·종료 |
| 픽셀 형식·방향 | PASS | Gray8/Gray16/RGBA32/BGRA32, 위/아래·좌/우와 RGB 채널 순서. 기존4개 HLSL 모두 SPIR-V 컴파일 |
| DX12 비교 | PASS | ROI4형상·회전·선택 핸들·한글 라벨·마스크·필터·결과 외곽/내부 윤곽·이름 있는 레이어. 채널 평균 오차<1.5, RGB오차합>12인 픽셀<1.5% 허용. bit-exact는 아님 |
| 반복 ROI | PASS | 100,020,001셀 추가·포인터 개별 선택/이동/크기/회전/삭제,50,005,000셀 부분 선택/이동/복붙 후 총150,025,001개 표시와 Undo/Redo 캡처 복원. 일반1억 객체 할당 또는 전체 세부 윤곽 표시 시험이 아님 |
| 8K | PASS | 8,192² 정적 소스,7,680×4,320 Gray8/Gray16 라이브 프레임. resize·최소화 복귀·렌더러 재시작·마지막 프레임 보존 |
| 실제 샘플 창 | PASS | XAML 바인딩/명령,8K 로딩, 그리기·그룹·복붙·삭제·Undo/Redo·새 분기, 캡처. 파일 읽기 실패/취소 시 기존 문서·generation·이력 보존 |
| 독립 실행 파일 | PASS | `VisionCanvas.VulkanSample.exe`의 실제 App 시작 후 정상 창 종료, exit0. 테스트에서 조립한 창과 별도로 확인 |
| ken32 | PASS | 로컬17,280×30,720 RGBA 원본,7단계 피라미드, 화면 맞춤과 원본 배율 타일. 원본좌표(7200,15000)에서 타일 경계 포함30위치×RGB를 원본 타일과 비교, 채널 오차≤1 |
| Vulkan 테스트 | PASS | **15통과,0실패,0skip**. `VISIONCANVAS_VULKAN_KEN32` 지정. 기존13건에1억 ROI 후속2건 추가. ken32 미지정 시 해당1건을 명시적으로 skip |
| 기존 Canvas | PASS | **243통과,0실패,0skip**. 실제 DX12/WPF·이미지·상호작용 회귀 |
| 공통 Core | PASS | **396통과,0실패,0skip**. 계층·경계·좌표·이력 등 |
| 기존 MVVM 샘플 | PASS / 일부 NOT_RUN | **17통과,1skip**. skip은 별도 ken32 명령→캡처 측정의 환경 변수 미지정. Vulkan의 ken32 시험은 별도 실행 |
| 기존 TeachingToolWpf Release | PASS | 경고0·오류0. 기본 DX12 앱 빌드 유지 |
| Vulkan 솔루션 Release | PASS | 오류0. 새 샘플의 NuGet 취약성 피드 `NU1900` 경고는 별도 유지 |
| 선택형 정적 분석 | 범위별 구분 | 전체 참조 빌드는 기존 Geometry/Imaging.Abstractions의 CA1062 11건으로 FAIL. 이미 빌드한 참조를 사용한 새 Vulkan 프로젝트는 경고0/오류0, 새 샘플은 코드 진단0·NU1900만 남음 |
| 변경 범위 C# 규칙 | PASS | 공백/Allman/4칸/괄호/IDE1006 명명. Vulkan 솔루션의 신규·공통 변경 파일과 기존 Canvas 테스트2파일을 각각 검사. P/Invoke 이름은 Roslyn Symbol Rename, native EntryPoint 보존 |
| 전체 `Format-Code.ps1 -Verify` | FAIL (기존 무관한 위반) | PoseMatch.Tdcm.Benchmarks,VisionInspection.Tests/DiskTileRegionTests 등 공백/줄바꿈 위반. 무관한 전체 재서식은 하지 않음 |
| Khronos validation layer | NOT_RUN | 장비에는 AMD switchable/NV optimus/NV present 레이어만 있음 |
| 지속60FPS/scan-out/다른 GPU | NOT_RUN | 기능 표시 성공과 실시간 성능 합격은 구별. GPU timestamp 미연결 |

검증 중 GPU→CPU readback 배리어 누락과 제출 후 Present 실패 시 캡처 버퍼 조기 해제 가능성을 독립 코드 검토에서 발견해 수정했다. TransferWrite→HostRead 배리어와 fence 이후 지연 해제로 해결했고 재검토에서 해소를 확인했다. 실제 device-lost 강제 주입이나 validation-layer 검증으로 표현하지 않는다.

## 1억 ROI 후속 검증

샘플의 `8K 예제` → `1억 ROI` 명령으로 공통 Core의10,001×10,001 압축 격자를 기존 문서에 추가한다. XAML 버튼의 명령 바인딩과 실제 Vulkan viewport를 검사했다. 별도 `HundredMillionTests`2건을 먼저 실행한 뒤 ken32 포함 Vulkan 전체15건을 다시 통과했다. 변경한 C#3파일의 공백/명명/스타일 검사도 통과했다. 공통 Core와 기존 DX12 구현은 이번 후속에서 변경하지 않았으며 표의 기존 회귀 수치는 포팅 완료 시 실행 기록이다.

- 한 셀을 확대·더블클릭한 뒤 실제 `Down/Move/Up` 입력으로 이동·크기·20° 회전한다. 선택 수1과 인접 셀 불변, 개별 삭제, Undo/Redo의 형상과 GPU 캡처 복원을 확인한다.
- 이미지 왼쪽 영역 선택은 공통 준비 API로 수행해50,005,000셀/10,001행 구간을 확인한다. 선택 부분만 이동한 뒤 샘플 복사/붙여넣기 명령으로 총150,025,001셀을 만들고 원본·복제 ID와 개수, Undo/Redo 캡처를 확인한다. 대량 영역 드래그 입력 시간을 측정한 시험은 아니다.
- 최초 배치는1파트/1구간, 논리 추정1,416바이트다. 개별 편집 시험의 이력 추정44,720바이트, 부분 편집·붙여넣기의 이력 추정568,144바이트로 각각1MiB 미만을 확인했다. 이 수치는 프로세스 작업 집합이나 실제 GPU 메모리가 아니다.

실제 RTX3060Laptop, 이미 준비한8K 소스와 GPU에서 관찰한 두 실행이다. 첫 실행은 후속2건만, 두 번째는 전체15건 실행 중 측정했다.

| 구간 | 후속2건 실행 | 전체15건 실행 |
|---|---:|---:|
| 100,020,001셀 추가 명령 완료 | 34.5693ms | 25.4530ms |
| 추가 시작→Vulkan 캡처 완료 | 106.5377ms | 115.3836ms |
| 50,005,000셀 영역 선택 준비·반영 | 31.5739ms | 39.2943ms |
| 해당 셀 복사·붙여넣기 완료 | 20.4384ms | 11.2609ms |

캡처에는 타일 준비·GPU 대기·readback·PNG 저장·20ms polling이 포함된다. 다른 행은 CPU 명령/준비 완료 시간이며 GPU 표시 완료를 뜻하지 않는다. 두 관찰을 평균/백분위·지속60FPS 근거로 사용하지 않는다. 전체 보기의 축소 요약과 확대 영역의 실제 셀을 검증했으며 일반 도형 객체1억 개의 개별 할당이나 모든 셀의 동시 상세 렌더링 시험은 아니다.

근거는 `artifacts/vulkan-port-validation/results/vulkan-hundred-million.trx`, `hundred-million-individual-*.json`, `hundred-million-partial-*.json`, `format-hundred-million-*.log`다. 시간 계측은 테스트에만 두고 샘플 App/VM/View의 책임을 유지한다.

## ken32 로딩 측정

동일 로컬 파일을 사용했다. 최초 관찰은 로딩7,510.9612ms/캡처 완료7,588.6027ms였다. 이후 다른 동시 작업과 산출물 경로가 겹쳐 전용 `vulkan-port-validation` 폴더와 고유 결과 이름으로 분리해 다시 확인했다. 이 새 애플리케이션 캐시 실행은 로딩7,955.2895ms, 로딩 시작부터 Vulkan 캡처 완료까지8,043.9388ms다. OS 파일 캐시는 비우지 않았다. 이전 캐시 재사용 관찰 예는 로딩69.736ms/캡처 완료243.87ms다. 실행마다 파일 시스템·GPU·테스트 부하가 달라 평균/백분위 또는 성능 개선율로 쓰지 않는다. 충돌한 이전 `result-first.json`은 근거 파일로 사용하지 않는다.

로딩은 PNG 디코딩·원본/피라미드 준비를 포함할 수 있고, 캡처 구간은 타일 준비·GPU 대기·readback·PNG 저장·20ms polling을 포함한다. GPU device/shader 준비는 로딩 전에 완료했다. 버튼 클릭부터 물리 화면 표시까지의 측정이 아니며 지속 프레임 속도도 아니다. 원본 전체 픽셀을 GPU 출력과 전수 대조한 시험은 아니다.

## 재현

```powershell
$env:VISIONCANVAS_VULKAN_KEN32 = (Resolve-Path 'ken32.png').Path
dotnet build VisionCanvas.Vulkan.slnx -c Release
dotnet test tests/VisionCanvas.Vulkan.Tests -c Release
dotnet test tests/VisionCanvas.Vulkan.Tests -c Release --filter FullyQualifiedName~HundredMillionTests
dotnet test tests/VisionCanvas.Tests -c Release
dotnet test tests/TeachingToolWpf.Core.Tests -c Release
dotnet test tests/VisionCanvas.MvvmSample.Tests -c Release
pwsh -File scripts/Format-Code.ps1 -Verify
```

이 환경의 테스트/format 프로세스는 제한된 named pipe 접근으로 기본 sandbox에서 시작하지 못해 로컬 권한으로 실행했다. SDK11.0.100-rc.2.26504.105, 타깃/실행 런타임은 .NET10이다. 새 프로젝트 복원 시 NuGet 취약성 피드 접속 오류 `NU1900`이 발생했다. 빌드·GPU 테스트 성공을 취약성 검사 성공으로 표현하지 않는다.

로컬 상세 자료는 `artifacts/vulkan-port-validation/`의 `results/*.trx`, `vulkaninfo.txt`, `format-scope*.log`, `format-all.log`, `build-existing.log`, `build-*-policy.log`, `sample-*.png`, `ken32/vulkan-result-*.json`, `ken32/original-tile-*.png`다. 테스트 캡처와 측정 JSON은 실행마다 고유 이름으로 만들어 이전 PNG로 잘못 통과하거나 다른 실행 값을 덮어쓰지 않게 했다. 큰 캐시·원본 파일은 Git 대상에 추가하지 않는다.

추가 정적 분석은 `-p:EnableDotNetAntiSlop=true`로 실행했다. 전체 참조 빌드에서 기존 CA1062가 먼저 실패하므로 새 프로젝트 자체의 검사에는 `-p:BuildProjectReferences=false`를 사용했으며 이를 전체 정책 통과라고 표현하지 않는다. 새 코드는 null 입력 검사와 명시적 소유권 이전/실패 해제를 적용했다. 유일한 국소 억제는 `EditorCommand.Execute`의 DAS1003으로, WPF `ICommand.Execute`의 void 계약을 충족하면서 실제 비동기 작업·예외 관찰을 Task 반환 `ExecuteAsync`에 두기 위한 것이다. 전역 규칙을 낮추지 않았다.

## 원칙 검토와 남은 범위

- 공통 Core/장면·타일·라벨·이미지 교체는 재사용하고 native GPU 경계만 Vulkan 프로젝트로 분리했다. 다른 프로젝트의 소스 링크는 없다.
- App 조립, VM 상태·명령·취소, View UI 이벤트·Dispatcher·native 컨트롤 수명을 유지했다. 샘플에 벤치마크/진단 분기를 추가하지 않았다.
- VulkanViewport는 기존 HWND 호스트를 상속한다. 공통 Canvas의 DX12 전이 패키지가 남지만 Vulkan 렌더 경로는 DX12 device나 WARP 대체를 만들지 않는다.
- GPU 전송·렌더·해제는 렌더 스레드가 담당한다. 프레임 fence/이미지별 present semaphore와 시작 실패·재시작·종료 소유권을 확인했다.
- 현재 타일/ROI 캐시는 전체 프로세스 메모리의 엄격한 상한이 아니다. 현재 보이는 ROI·프레임 업로드 high-water·전송/지연 해제 메모리는 추가된다. 큰 일반 ROI의 최악 지연과 장기 안정성, 강제 장치 제거, 다른 PC 배포는 미검증이다.
- 파일/라이브 표시는 지원하지만 샘플에서 실제 카메라 수집·라인 조립·패턴 검사 UI를 제공하지 않는다. 매칭 결과·필터·마스크·레이어 표시는 RenderScene/API 계약으로 지원한다.

[전용 매뉴얼](../manual/vulkan/README.md) · [ADR-066](decisions/0066-vulkan-canvas-and-sample.md) · [작업 범위](../tasks/vulkan-port.md)
