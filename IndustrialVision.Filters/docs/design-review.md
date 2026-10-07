# 첫 수직 경로 설계 검토 — 2026-10-06

가정: 독립 솔루션은 기존 저장소의 새 하위 폴더에 둔다. 기존 프로젝트는 변경하지 않는다. Gray8/16/Float32의 네 가지 필터부터 실제 검증하며 전체 A/B/C 완료로 표현하지 않는다. 동기 단일 호출자 실행을 먼저 완성하고 비동기·멀티스트림·자동 근사 전환은 도입하지 않는다.

## 책임과 패턴

Core는 값/소유권/계약, Cpu는 계산과 작업 배열, Gpu는 CUDA context/stream/kernel/device 배열, Pipeline은 컴파일·배치·검증·타일 경계를 소유한다. 예제의 파일 adapter와 외부에서 주입하는 GPU backend에 Adapter/Strategy를 적용했다. recipe는 불변 순서이며 필터 재배치나 숨은 fusion을 하지 않는다. 픽셀 루프에는 다형 호출이 없다. 이력/편집/UI Controller는 이번 범위가 아니다.

| 원칙 | 확인 |
|---|---|
| SRP | 위 네 경계와 파일 adapter 분리. main 예제는 조립만 수행 |
| OCP | GPU는 작은 중립 계약으로 선택. 새 filter type은 구현과 검증을 명시적으로 추가해야 함; 사용하지 않는 plugin 프레임워크 없음 |
| LSP | CPU/CUDA가 같은 입력 validation·invalid 전파·signed/threshold 계약을 독립 참조에 대해 검증 |
| ISP | GPU backend/program, tile source/sink만 분리. 사용하지 않는 장치 API 강제 없음 |
| DIP | Core/Pipeline은 ILGPU/GUI를 참조하지 않음. 호스트 주입 |
| Information Expert | stride/버퍼 크기는 ImageDescriptor/ImageView, map 일치는 CalibrationMap |
| Creator | HostImage/CPU program/CUDA program이 자기 저장소를 생성·종료 |
| Controller | UI가 없으므로 MVVM Controller는 해당 없음. 실행 조정은 CompiledPlan |
| Low Coupling | Core 계약과 plain memory 외 WPF/SDK 타입이 경계를 넘지 않음 |
| High Cohesion | 파일 읽기/테스트 참조/계산/벤치마크 분리 |
| Polymorphism | 실제 GPU 선택/타일 I/O 변형에만 적용. 픽셀마다 interface 없음 |
| Pure Fabrication | CPU 작업 공간과 CUDA stream/buffer 수명은 도메인 필터 파라미터 밖에서 관리 |
| Indirection | 호스트의 FilterEngine 주입으로 CUDA·Canvas 직접 결합 차단 |
| Protected Variations | 디코더/GUI/GPU 런타임은 코어 자료형 변경 없이 교체 가능 |

승인된 원칙 예외는 사용하지 않았다. ILGPU의 동적 launcher가 인자 타입에 접근하도록 `KernelSettings`만 public 값 형식으로 두고 EditorBrowsable(Never)로 표시했다. 이는 정확성/책임 경계를 완화하는 예외가 아니다.

## 확인된 한계

- CPU/GPU 혼합 **여러 구간**의 일반 계획기는 아직 없다. 현재 지원 필터의 전체 CPU 또는 단일 연속 CUDA 구간, 명시적 계획/할당 fallback만 제공한다.
- Conservative의 단일 단계 CPU 선택은 구조적 보수 정책이다. 장비별 최적 crossover를 학습했다고 주장하지 않는다.
- SIMD는 Gaussian에 적용했다. 보정·threshold SIMD 융합, 작은 고정 Gaussian 특수화, 큰 GPU morphology의 선형 알고리즘은 남았다.
- 논리적 유효성 마스크는 invalid 전파용이다. 마스크 표본만으로 통계 재정규화하는 기능은 없다.
- strict 메모리 항목은 배열 payload이며 프로세스 working set 상한이 아니다. 메모리 할당 실패 외 커널/입력 오류를 fallback으로 숨기지 않는다.
- GPU 동시 작업/OS 부하, 전력 관리에 따른 p95 변동을 제거했다고 주장하지 않는다.
- GPU 없는 물리 장비, Linux, 비CUDA backend는 이번 환경에서 실기 검증하지 않았다.
