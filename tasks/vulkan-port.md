# VisionCanvas Vulkan 포팅

사용자 요청(2026-10-16): 별도 Vulkan Canvas 프로젝트, Vulkan 전용 WPF 앱, 전용 매뉴얼. 현재 main에서 로컬 수정·검증만 수행한다.

## 범위와 구현 순서

1. 기존 Core의 좌표·ROI·계층·입력·Undo/Redo와 Canvas의 타일/라이브 준비·장면 배치를 재사용한다. 기존 DX12 기본 경로를 보존하면서 렌더 스레드 및 텍스처 업로드 계약만 분리한다.
2. `src/VisionCanvas.Vulkan`: Win32 surface, Vulkan 장치/스왑체인, SPIR-V 파이프라인, 텍스처/ROI/마스크/반복 격자, 프레임 fence와 종료 수명. 장치 선택은 사용 가능한 discrete GPU 우선이다. DX12 자동 대체는 하지 않는다.
3. `samples/VisionCanvas.VulkanSample`: App에서 조립·종료, VM에 명령/상태, View에는 입력 연결. 파일 열기·8K 예제·ROI 편집·Undo/Redo를 제공한다. 검사 알고리즘이나 실제 카메라 수집은 추가하지 않는다.
4. 별도 테스트 프로젝트에서 실제 GPU 표시/캡처, resize/최소화/재시작/종료, 원본 픽셀 형식·8K 및 대용량 파일을 검증한다. 기존 Canvas/Core 회귀 검증을 실행한다.
5. `manual/vulkan/README.md`에 실행, 구조, 수명, 사용법, 배포 의존성, 실측과 미검증 범위를 기록한다.

## 경계

- 새 Vulkan 바인딩은 Vortice.Vulkan, 셰이더 컴파일은 Vortice.ShaderCompiler를 사용하며 버전·라이선스·네이티브 파일을 고지한다.
- 공통 Canvas 어셈블리를 참조해 기존 장면/입력/배치 코드를 재사용한다. DX12 패키지 의존성의 물리적 분리는 이번 작업에 포함하지 않는다. Vulkan 실행 경로에서 DX12 장치를 만들지 않는다.
- 기존 512px 타일, 준비 큐32, 원본 Gray8/Gray16, 최신 라이브 프레임, 불변 문서/장면, 상대 좌표, GPU 완료 후 반환을 유지한다.
- 기존 C# Allman/4공백/명명 규칙 및 nullable을 적용한다. 진단은 샘플 화면이 아닌 테스트에 둔다.
- 성공 기준은 Vulkan GPU에서 실제 Canvas 기능이 실행되는 것이다. 빌드만으로 성공 또는 60FPS 달성을 주장하지 않는다.

## 검증 명령

`dotnet build VisionCanvas.Vulkan.slnx -c Release`

`dotnet test tests/VisionCanvas.Vulkan.Tests/VisionCanvas.Vulkan.Tests.csproj -c Release`

`dotnet test tests/VisionCanvas.Tests/VisionCanvas.Tests.csproj -c Release`

`pwsh -File scripts/Format-Code.ps1 -Verify`

## 완료 기록

구현·실제 GPU·대용량 입력·DX12/Core/기존 샘플 회귀·변경 범위 코드 규칙 검증과 매뉴얼을 완료했다. 세부 PASS/FAIL/NOT_RUN과 실행 자료는 [검증 기록](../docs/vulkan-validation.md), 사용법은 [Vulkan 매뉴얼](../manual/vulkan/README.md)에 있다. 전체 형식 위반과 미측정60FPS·validation layer를 숨기지 않는다. 커밋·푸시는 수행하지 않았다.

## 1억 ROI 후속

사용자 추가 요청에 따라 샘플 VM의 `1억 ROI` 명령과 XAML 버튼을 추가했다. 기존 문서에 공통 Core의100,020,001셀 압축 격자를 추가하고 단일 이력으로 보존한다. 별도 GPU 테스트2건에서 실제 포인터의 개별 선택·이동·크기·회전·삭제,50,005,000개 부분 선택·이동·복붙,총150,025,001개 표시와 Undo/Redo 캡처 복원을 검증한다. 명령/캡처 시간을 별도 JSON에 기록하며 일반1억 객체 할당·지속60FPS·프로세스/GPU 총 메모리 측정으로 표현하지 않는다.
