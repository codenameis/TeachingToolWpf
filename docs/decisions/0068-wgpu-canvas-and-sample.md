# ADR-068: wgpu Canvas와 독립 WPF 앱

2026-10-16. 상태: 사용자 요청에 따른 로컬 구현·검증.

## 결정

사용자는 VisionCanvas의 wgpu 포팅과 앱까지 구현하도록 승인했다. `VisionCanvas.Wgpu`, `VisionCanvas.WgpuSample`, `VisionCanvas.Wgpu.Tests`, `VisionCanvas.Wgpu.slnx`를 추가한다. 기존 DX12/Vulkan/Skia 경로는 유지한다.

기존 HWND 호스트의 `CreateRenderer`와 `IRenderWorker`/텍스처 경계를 사용한다. Core 문서·입력·좌표·계층·Undo/Redo, 공통 ScenePainter·타일·라벨·필터·최신 프레임 준비를 재사용한다. 공통 프로젝트에는 internal 접근 권한만 추가한다. WPF/기존 DX12 전이 참조를 제거하기 위한 어셈블리 재분할은 하지 않는다.

C# 바인딩 `Alimer.Bindings.WebGPU 1.6.0`, native 패키지 `Alimer.WebGPU.Native 1.0.4`를 고정한다. 실제 native 조회 버전은 27.0.4.0이다. Windows HWND surface와 DX12 high-performance hardware adapter를 선택하며 Gray16의 native R16Unorm 기능을 요구한다. C# 기존 Dx12Device는 생성하지 않지만 wgpu 내부 backend는 DX12다. 다른 backend·브라우저 UI는 이번 구현 범위가 아니다.

## 책임과 수명

- App은 서비스 생성/종료, VM은 상태·명령·오류, View는 입력 전달·바인딩·창 수명을 맡는다.
- WgpuRenderWorker는 전용 STA에서 최신 요청을 병합하고 GPU 작업을 소유한다. 테스트·계측은 별도 프로젝트다.
- WgpuDevice/Surface는 native 객체·surface·제출 번호·지연 해제, FrameResources는 슬롯별 버퍼 영역/bind group을 소유한다.
- 두 슬롯은 완료된 submission을 기다린 뒤 재사용한다. shutdown/resize도 완료 대기 후 해제하며 HWND보다 먼저 renderer를 종료한다.
- WGSL 네 개는 기존 HLSL의 sprite/ROI/mask/repeated grid 계산을 옮긴다. 캡처에서만 readback한다.

원본 Gray16은 16비트 텍스처로 보존한다. 최종 화면은8비트이며 픽셀 시험은 표시 양자화 허용치를 명시한다. 512px 타일·준비 큐32개·세대/취소·최신 프레임 계약을 유지한다. 타일256MiB는 논리 바이트 퇴출 목표, ROI128MiB는 비활성 블록 보관 목표이며 실제 GPU 예산/전체 메모리 상한이 아니다.

wgpu validation을 활성화하고 콜백 오류를 관리 코드에 전달한다. pinned native의 adapter/device 요청은 동기적으로 콜백을 완료한다. 일부 queue submit 실패는 native 내부 fatal 경로이므로 장치 손실의 완전한 복구는 보장하지 않는다. 이를 숨기기 위해 기존 렌더러로 자동 대체하지 않는다.

## 대안과 원칙 검토

Rust 별도 wrapper는 추가 ABI/빌드 체계를 요구하므로 고정 C# 바인딩과 native 패키지를 선택했다. C# 코드 전체의 Rust 재작성이나 Core 복제는 하지 않는다. 현재 WPF를 유지하면서 backend를 도입하므로 UI 크로스플랫폼화는 별도 작업이다.

SRP/DIP는 App 조립·Core 편집·렌더 자원 소유로 유지하고, OCP/LSP는 기존 렌더 생성 지점과 소유권·재시작·실패 이력 테스트로 확인한다. ISP는 기존의 작은 소스/프레임/렌더 계약을 사용한다. GRASP의 정보 전문가·생성자·컨트롤러는 각각 Core와 자원 소유자, App, EditorSession에 둔다. 다형성/간접화는 실제 교체 경계에 한정하며 범용 서비스 프레임워크는 추가하지 않는다. 설계 원칙 예외는 없다.

별도 샘플의 명령 구성은 기존 Vulkan 샘플과 같으며 GPU 수명만 새 프로젝트에 둔다. 샘플 공통화나 기존 전체 형식 수정은 하지 않는다. [실제 검증](../wgpu-validation.md)과 [매뉴얼](../../manual/wgpu/README.md)을 따른다.

## 1억 ROI 후속

2026-10-16 후속: 사용자 요청으로 샘플에 `1억 ROI` 명령과 별도 자동 테스트3건을 추가한다. 기존 Core의100,020,001셀 압축 배치를 재사용하며 현재 문서의 ROI를 유지한다. VM 명령/XAML 바인딩만 연결하고 계측은 테스트에 둔다. 개별 포인터 편집·부분50,005,000개 복붙·Undo/Redo·작은 이미지 거부를 검증하며, 일반1억 객체나 지속60FPS로 표현하지 않는다. [후속 결과](../wgpu-validation.md#1억-roi-후속-검증).

## 공식 근거

- [wgpu-native v27.0.4.0 소스](https://github.com/gfx-rs/wgpu-native/tree/v27.0.4.0): HWND surface, native features, submission/poll, 요청 콜백 및 fatal 오류 경로.
- [고정 C# 바인딩 소스](https://github.com/amerkoleci/Alimer.Bindings/tree/eedec82d8cb7845f713e668e8dad2df3aaec8893): native API와 소유권 시그니처.
- [Alimer.Bindings.WebGPU 1.6.0](https://www.nuget.org/packages/Alimer.Bindings.WebGPU/1.6.0), [Alimer.WebGPU.Native 1.0.4](https://www.nuget.org/packages/Alimer.WebGPU.Native/1.0.4).

정확한 배포 버전은 위 패키지와 실제 함수 조회값을 사용하며 패키지 README의 이전 버전 설명을 런타임 버전으로 간주하지 않는다.
