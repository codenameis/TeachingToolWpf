# ADR-038: VisionCanvas ROI 필터에 IndustrialVision 엔진 연결

상태: 2026-10-06 사용자 승인 구현. `main` 로컬 변경만 유지한다.

사용자가 독립 엔진 생성 이후 “VisionCanvas … Roi 필터에 추가 … 테스트”를 요청했다. 기존 앱을 수정하지 않던 최초 범위를 이번 연결에 한해 확장한다.

## 결정

- VisionCanvas → IndustrialVision.Filters.Pipeline → Cpu/Core를 명시적으로 참조한다. 엔진은 Canvas/WPF/영상 디코더/패턴매칭을 역참조하지 않는다. VisionCanvas.Core에는 불변 ROI 설정만 둔다.
- TeachingToolWpf가 선택형 ILGPU CUDA 백엔드를 지연 생성·소유한다. Canvas는 `IGpuBackend` 팩토리를 주입받으며 자체적으로 ILGPU 패키지를 참조하지 않는다. CPU 실행은 GPU 생성기를 호출하지 않는다. 렌더 DX12/WARP와 필터 CUDA 선택은 독립이다.
- 구현된 Gaussian, 사각형 형태학 6종, 원본 명암 임계값, Gaussian → Black-hat → Threshold 프리셋을 UI에 노출한다. 보정 맵 등록 기능이 없는 Flat-field와 미구현 필터는 노출하지 않는다. 기존 OpenCV Binarize/Canny는 그대로 유지한다.
- CpuGpu는 명시적인 CUDA 비교 요청(`PreferGpu`)으로 연결한다. 전송을 포함한 최적 자동 선택이라고 주장하지 않는다. 지원 불가만 사유를 반환하며 구현 오류는 미리보기 실패로 전파한다.
- 기존 ROI 정렬·축소·도형 출력 마스크·등록·통합 이력을 재사용한다. 원본 명암을 유지한 채 필터링하고 화면용 Gray8 변환은 마지막에 적용한다. 이 화면은 축소 미리보기이며 원본 해상도 검사 입력으로 사용하지 않는다.
- 단일 worker가 마지막 설정/크기의 계획 1개와 버퍼를 재사용한다. 다른 설정 또는 크기는 이전 계획을 해제한 뒤 재컴파일한다. 백엔드는 유지한다. 큰 다중 설정 캐시는 추가하지 않는다.
- 창 종료 시 필터를 취소하고 worker 완료 후 계획 → 백엔드 순서로 해제한다. 완료된 Task에서도 WPF Closing 재진입을 피하도록 다음 dispatcher 차례에서 닫는다.

## 설계 검토

| 원칙 | 적용 근거 |
|---|---|
| SRP | Core 설정, Canvas 픽셀 어댑터, 엔진 계산, 앱 UI/장치 조립의 책임 분리 |
| OCP | 기존 OpenCV 동작을 유지하며 구현된 엔진 필터만 선택 항목으로 추가 |
| LSP | CPU/CUDA 결과, 취소, 잘못된 입력, GPU 불가와 구현 오류를 구별하는 테스트 |
| ISP | 기존 preview 계약 유지, 장치 선택은 작은 기존 IGpuBackend 계약 사용 |
| DIP | Canvas는 GPU 계약만 사용하고 실제 CUDA 생성은 앱이 주입 |
| Information Expert | 필터 파라미터 검증은 FilterSettings, 계산 계획은 엔진이 소유 |
| Creator | adapter가 plan/host buffer, 앱이 CUDA backend를 생성·해제 |
| Controller | ViewModel은 등록·진단 표시를 담당하며 픽셀 연산은 하지 않음 |
| Low Coupling | 엔진의 WPF/Canvas 역참조 없음; 패턴매칭 프로젝트 변경 없음 |
| High Cohesion | ROI 변환과 처리 연결은 Canvas Imaging/Filters 내부에 응집 |
| Polymorphism | 기존 IRoiFilterPreview, IGpuBackend 변형 지점을 사용; 픽셀별 동적 호출 없음 |
| Pure Fabrication | 계획/작업 메모리는 IndustrialRoiFilter가 소유하고 ROI 문서에 넣지 않음 |
| Indirection | 내부 Mat → 중립 ImageView 변환으로 OpenCV와 엔진 경계 격리 |
| Protected Variations | UI 레시피와 GPU 구현, 표시 장치와 계산 장치를 분리 |

원칙 완화 없음. [사용법·검증·제한](../industrial-roi-filters.md)을 따른다.
