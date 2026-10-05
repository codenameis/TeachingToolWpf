# ADR-032: Canvas의 선택형 NetVips 파일 로더

- 날짜: 2026-10-05
- 상태: 사용자 승인으로 구현. 실행 증거는 [검증 기록](../canvas-reuse-validation.md)의 NetVips 후속 절을 따른다.
- 범위: 기존 `codex/vision-canvas-boundaries`에서 로컬 구현·검증. 커밋·푸시는 하지 않는다.

## 문제와 결정

ADR-031 이후에도 Canvas → VisionImaging → NetVips가 전이되어 파일을 읽지 않는 호스트에 디코더가 포함된다. 사용자는 NetVips를 계속 쓰면서 이 의존성을 낮추도록 요청했다.

새 프로젝트·플러그인·중계 래퍼 없이 기존 타입의 소속과 참조만 정리한다.

| 책임 | 이전 | 이후 |
|---|---|---|
| 파일/소스 로딩 계약 | VisionImaging.IImageLoader | VisionImaging.Abstractions.IImageLoader |
| 최신 프레임 보유·이전 | VisionImaging.LatestFrameMailbox | VisionCanvas.Imaging.LatestFrameMailbox |
| 화면 배율·범위의 가시 타일 계산 | VisionImaging.TilePlanner.Visible | VisionCanvas.Imaging.TilePlanner.Visible |
| 이미지 규격의 피라미드 단계 수 | VisionImaging.TilePlanner.LevelCount | VisionImaging.Abstractions.ImageInfo.CalculateLevelCount |
| 파일 디코드·디스크 피라미드 | VisionImaging.ImageLoader / DiskPyramidSource | 동일한 NetVips 구현 유지 |

Canvas의 직접 프로젝트 참조는 Canvas.Core와 Imaging.Abstractions다. 기하는 편집 코어의 중립 참조로 사용한다. 파일을 여는 앱·샘플은 VisionImaging과 네이티브 패키지를 명시적으로 참조한다. `IImageLoader`를 구현하거나 이전 namespace의 유틸리티를 사용하는 외부 코드는 using/호출부를 변경하고 재빌드해야 한다. 배포된 이전 바이너리와의 호환 shim은 추가하지 않는다.

## 성능과 수명

인터페이스 호출은 기존 LoadAsync/Read 경계를 그대로 사용한다. 프레임의 참조 보유, 픽셀 배열 풀, 최신 프레임 한 칸, 타일 준비 큐 32개, 원본 reader의 단일 소유권은 바꾸지 않는다. 파일 피라미드 생성과 가시 타일 계산식도 유지한다.

ROI 스냅샷·공간 블록 identity와 GPU 캐시에 손대지 않는다. 범용 DTO 변환이나 프레임 전체 복사, 포맷 통일을 추가하지 않는다. 검사용 IOriginalGrayTileSource와 직접 Gray8 경로도 보존한다. CPU→GPU 업로드 등 기존 복사는 남아 있으므로 전체 zero-copy로 표현하지 않는다.

이 변경은 디코더 교체나 성능 최적화가 아니다. 변경 전후 별도 배포본으로 지연·드롭·메모리를 비교하고 첫 파일 import와 캐시 재개방을 구별한다. 짧은 반복 시험이나 단일 대용량 파일 시험으로 장시간 안정성·모든 형식·현장 FPS·검사 125/80ms 목표 달성을 주장하지 않는다.

OpenCV 필터와 WPF/DX12 의존성은 이번 범위에 남긴다. 검사와 앱이 NetVips를 쓰는 것도 의도한 구성이다.

## 검증 기준

- Canvas 전이 assembly 참조 및 공개 API에서 VisionImaging/NetVips를 거부한다. 중립 계약에는 native 참조를 허용하지 않는다.
- Canvas만 참조한 별도 호스트의 새 배포 폴더·deps.json·실제 로드 모듈에 NetVips/libvips가 없어야 한다. 8K 프레임과 ROI의 DX12 표시를 확인한다.
- 기존 파일 샘플·앱의 파일 열기·실패 원자성·필터·이미지 이력·원본 검사를 검증한다.
- 이동한 프레임/타일 테스트를 보존하고 전체 회귀 테스트를 실행한다.

## SOLID·GRASP 검토

| 원칙 | 이번 변경의 확인 |
|---|---|
| SRP | 파일 구현과 표시용 큐·가시 범위 계산의 변경 이유를 분리한다. |
| OCP | 기존 IImageLoader/ITileSource 구현으로 소스를 연결한다. 새 플랫폼 분기를 넣지 않는다. |
| LSP | 취소·독립 reader·버퍼 이전/반환 계약을 유지하고 기존 수명/실패 테스트를 보존한다. |
| ISP | 로더·타일·프레임 계약을 합치지 않고 필요한 계약만 사용한다. |
| DIP | Canvas가 중립 로더 계약에 의존하고 파일 호스트가 NetVips 구현을 선택한다. |
| Information Expert | 단계 수는 이미지 규격, 가시 범위는 Canvas 표시 계산에 둔다. |
| Creator | 로더의 source 생성과 worker/GPU 소유자의 해제 책임을 유지한다. |
| Controller | UI·편집·검사 실행 흐름을 변경하지 않는다. 새 업무 제어를 Canvas에 넣지 않는다. |
| Low Coupling | Canvas의 디코더 구현 전이 참조를 실제로 제거한다. |
| High Cohesion | mailbox와 표시 타일 계획은 Canvas, 파일 피라미드는 파일 구현에 둔다. |
| Polymorphism | 기존 소스/로더 변형만 사용하며 픽셀별 가상 호출을 도입하지 않는다. |
| Pure Fabrication | 기존 큐·GPU 캐시·reader 수명 컴포넌트를 보존한다. |
| Indirection | 기존 인터페이스가 연결점이며 추가 중계 계층은 만들지 않는다. |
| Protected Variations | 파일 디코더 변경이 Canvas의 필수 배포 의존성을 바꾸지 않게 한다. |

설계 원칙을 완화하는 성능 예외는 적용하지 않는다.
