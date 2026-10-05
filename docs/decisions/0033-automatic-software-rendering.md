# ADR-033: GPU 우선과 CPU 소프트웨어 렌더링 자동 전환

- 날짜: 2026-10-05
- 상태: 사용자 승인으로 구현. 실행 결과는 [검증 기록](../software-rendering-validation.md)을 따른다.
- 범위: Windows VisionCanvas의 장치 선택. 검사 알고리즘·CUDA 선택은 변경하지 않는다.

## 요구와 결정

사용자는 GPU가 설치되지 않은 Windows PC에서도 Canvas를 사용하고, GPU가 있으면 자동으로 하드웨어를 사용하도록 요청했다. 기존 하드웨어 전용 시작 조건을 이 결정으로 대체한다.

`Automatic`은 기존 NVIDIA 우선·전용 메모리 순서로 소프트웨어가 아닌 adapter를 시도한다. Direct3D 12 장치를 생성할 수 있는 하드웨어가 없으면 Windows WARP 장치를 생성한다. `Software`는 하드웨어 열거를 건너뛰고 WARP를 사용한다. 장치 선택 실패는 오류로 보고하며 생성 도중 확보한 COM 객체와 fence event를 해제한다.

WARP는 같은 Direct3D 12 API·셰이더를 CPU에서 실행하는 Windows 소프트웨어 렌더러다. 별도 픽셀 렌더러, 패키지, ROI 변환 계층을 만들지 않는다. HWND·swap chain·타일·필터·이력·버퍼 소유권은 기존 경로를 유지한다. [Microsoft WARP 설명](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/directx-warp), [EnumWarpAdapter 계약](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgifactory4-enumwarpadapter)을 근거로 한다.

## 공개 계약과 표시

- `Dx12Viewport.DevicePreference` 기본값은 `RenderDevicePreference.Automatic`이다. 기존 호스트는 변경 없이 자동 전환을 사용한다.
- `Software`는 뷰포트가 native window를 만들기 전에 설정한다. 생성 후 변경은 거부하며 `RestartAsync`에서도 선택을 유지한다. 기존 RenderWorker 생성자는 유지하고 선택 인수가 있는 생성자를 추가한다.
- 앱과 독립 샘플의 `--software-rendering`은 Canvas를 WARP로 강제해 GPU가 있는 PC에서도 CPU 표시를 검증한다. Windows 전체 또는 다른 프로세스의 GPU 사용을 차단하는 옵션은 아니다.
- 상태에 `GPU` 또는 `CPU (WARP)`와 실제 adapter를 표시한다. CPU 모드에서는 GPU timestamp query를 만들지 않고 `GpuMilliseconds`/CSV `gpu_ms`를 비워 둔다. CPU 제출 시간은 화면 표시 완료 시간이나 CPU 전체 사용 시간과 다르다.
- 소프트웨어 타일 texture 캐시는 기존 하드웨어 캐시 상한과 같은256MiB를 사용한다. 소프트웨어의 전용 VRAM0을 전체 사용 가능 메모리로 오해하거나0으로 나누지 않는다. 이 값은 프로세스 전체 메모리 상한이 아니다.

시작 시 장치를 선택한다. 실행 중 장치 제거의 자동 복구, 새 GPU의 실시간 전환, 비Windows 렌더러는 이번 범위가 아니다. WARP에서도 셰이더·동기화·타일 계약은 유지되지만 CPU와 메모리 대역폭을 사용하므로 FPS 동등성은 별도 측정해야 한다.

## 검증과 원칙

하드웨어 열거가 비어 있는 `Automatic`, 소프트웨어 adapter만 열거된 `Automatic`, 하드웨어를 열거하지 않는 `Software`를 실제 WARP 장치로 검증한다. 장치 열거 함수의 내부 주입점은 이 재현과 COM 소유권 확인에만 사용하며 공개 공급자 인터페이스로 확장하지 않는다. 실제 앱·샘플에서 회전·이력·8K/24K 타일과 렌더 재시작을 확인하고, 같은 입력의 GPU/CPU 성능을 구분해 기록한다.

| 원칙 | 확인 |
|---|---|
| SRP | 장치 선택은 Dx12Device, 호스트 옵션은 앱/샘플에 둔다. |
| OCP | 기존 렌더 경로를 유지하고 기존 DX12 backend에서 장치 종류만 선택한다. |
| LSP | 같은 버퍼·ROI·타일 계약을 GPU/WARP 실행에 적용한다. |
| ISP | 소스·입력·그리기 계약에 장치별 메서드를 추가하지 않는다. |
| DIP | Core에 그래픽 API를 추가하지 않고 host가 Canvas 선택 값을 전달한다. |
| Information Expert | adapter 지원 여부와 메모리 정보는 장치 소유자가 판단한다. |
| Creator | 렌더 소유 스레드가 장치·queue·fence를 생성·해제한다. |
| Controller | 앱은 실행 옵션을 연결하고 ROI 업무 로직을 변경하지 않는다. |
| Low Coupling | WARP 선택을 검사·NetVips·Core와 연결하지 않는다. |
| High Cohesion | 선택·오류·초기화 정리는 기존 장치 타입에 둔다. |
| Polymorphism | Direct3D의 하드웨어/소프트웨어 adapter 구현을 같은 렌더 경로로 사용한다. |
| Pure Fabrication | 기존 worker·타일 캐시·GPU 수명 컴포넌트를 재사용한다. |
| Indirection | 기존 DXGI adapter 경계로 CPU 실행을 연결한다. |
| Protected Variations | GPU 부재를 장치 선택 경계에서 처리한다. |

설계 원칙을 완화하는 성능 예외는 없다. WARP를 강제한 현 장비 시험을 GPU가 실제로 없는 저사양 PC의 성능 보장으로 표현하지 않는다.
