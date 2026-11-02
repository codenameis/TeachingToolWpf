# ADR-066: 공통 Canvas 계약을 재사용하는 Vulkan 렌더러와 독립 앱

- 날짜: 2026-10-16
- 상태: 구현. 사용자 요청에 따라 로컬 수정·검증만 수행.
- 요청: 별도 VisionCanvas Vulkan 프로젝트, Vulkan 앱, 기존 코드 규칙, 전용 매뉴얼.

## 결정

`VisionCanvas.Vulkan`에 실제 Vulkan 1.1 장치·surface·swapchain·파이프라인을 구현하고 `VisionCanvas.VulkanSample`을 독립 시작 앱으로 추가한다. WPF/Win32 입력과 수명은 기존 컨트롤을 상속하며 protected renderer factory와 `IRenderWorker`로 렌더러를 선택한다. 텍스처·업로드·지연 해제 계약은 내부 인터페이스로 한정한다. 기존 DX12 구현은 이 계약에 연결하며 기본 동작을 유지한다.

문서·계층·경계·좌표·Undo/Redo는 Core, 소스 준비·타일·라벨·장면 배치는 기존 Canvas/Imaging을 ProjectReference로 공유한다. HLSL 수식도 기존 파일을 사용한다. 소스 파일 링크나 도메인 복제 없이 native GPU 경계만 포팅한다. 앱은 App 조립, ViewModel 상태/명령/취소, View 입력 전달·Dispatcher·컨트롤 해제로 나눈다. 진단은 별도 테스트 프로젝트에 둔다.

Vortice.Vulkan 3.2.3과 Vortice.ShaderCompiler 1.9.0을 추가한다. shaderc 호출은 UTF-8 바이트 길이와 native bool ABI를 명시한 작은 P/Invoke 경계로 처리한다. 기존 HLSL 레지스터를 descriptor에 명시 매핑하고 Y 방향을 맞춘다. 176바이트 ROI 상수는 UBO다.

두 프레임 슬롯의 fence와 이미지별 present semaphore를 사용한다. 자원 교체/캡처 버퍼는 fence 이후 해제한다. Present 실패 시에도 readback 수명이 유지된다. HostCoherent와 별개로 캡처 TransferWrite→HostRead 배리어를 기록한다. 장치 재생성과 종료는 GPU 완료 후 진행한다. 최소화는 제출을 쉬고 resize는 swapchain을 재생성한다. 명시적 캡처·결과 완료 확인 외에 매 프레임 device idle을 호출하지 않는다.

## 선택의 비용

공통 WPF 호스트의 기존 이름 `Dx12Viewport`와 Canvas의 DX12 전이 참조가 남는다. 렌더러 선택만으로 Vulkan 경로는 DX12 device를 만들지 않지만, 배포 의존성까지 분리된 순수 Vulkan 공통 어셈블리는 아니다. 전체 프로젝트 재분할은 요청 범위에 비해 변경이 커서 하지 않는다. Windows WPF 지원이며 크로스플랫폼 UI는 별도 작업이다.

현재 ROI 캐시는 보이는 블록을 우선 보존하고 비활성 블록을 128MiB 기준으로 퇴출한다. DX12의 시간 분할 준비/세부 메모리 allocator를 그대로 옮긴 것은 아니다. 원본8K·대용량 파일·압축 반복 ROI 지원과 엄격한60FPS 또는 무제한 일반 ROI 메모리 보장은 구별한다. GPU timestamp·물리 scan-out 계측은 없다.

## 검증

실제 RTX3060Laptop의 픽셀·WPF 창·8K·ken32·재시작과 기존 Canvas/Core 회귀 결과는 [검증 기록](../vulkan-validation.md)을 따른다. [전용 매뉴얼](../../manual/vulkan/README.md)에 실행·통합 API·소유권·배포·미검증 범위를 기록한다. 현재 장비에 Khronos validation layer가 없어 validation 통과를 주장하지 않는다.
