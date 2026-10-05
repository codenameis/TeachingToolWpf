# VisionCanvas 재사용 경계 분리 계획

- 날짜: 2026-10-05
- 사용자 승인: 별도 브랜치에서 구현. 검증된 단계의 커밋·GitHub 푸시는 하지 않고 로컬 구현·검증만 진행한다.
- 브랜치: `codex/vision-canvas-boundaries`
- 결정: [ADR-031](../docs/decisions/0031-reusable-canvas-boundaries.md)
- 결과와 미검증 범위: [검증 기록](../docs/canvas-reuse-validation.md). 아래 내용은 수용 기준이며 실행 완료를 뜻하지 않는다.

## 목적과 범위

2026-10-05 후속 사용자 승인으로 [ADR-032](../docs/decisions/0032-optional-canvas-file-decoder.md)를 적용한다. 아래 표는 후속 경계이며 ADR-031의 최초 구조보다 우선한다. NetVips 구현은 유지하고 Canvas의 필수 참조에서 제거한다.

다른 WPF 앱에서 TeachingToolWpf의 ViewModel·레시피·검사 프로젝트 없이 영상 표시와 ROI 편집을 사용할 수 있게 한다. 이미지 I/O와 검사가 ROI 편집·계층·이력에 의존하지 않도록 공통 값과 소유권 계약을 옮긴다. 새 기능이나 범용 엔진을 추가하는 작업이 아니다.

`TeachingToolWpf.Core`라는 이름만 바꾸면 검사도 편집 코어를 계속 참조한다. 반대로 이미지 계약을 NetVips 구현과 한 프로젝트에 넣으면 편집 코어가 네이티브 디코더를 간접 참조한다. 따라서 기존 공유 타입을 기하, 이미지 계약, 편집 코어로 분리한다. 프로젝트 수보다 실제 참조 방향과 런타임 소유권을 기준으로 판단한다.

| 프로젝트 | 책임 | 허용할 주요 프로젝트 참조 |
|---|---|---|
| `VisionGeometry` | 원본 좌표의 점·경계·도형·ROI 형상·강체 변환, 뷰 변환과 회전 범위 값 | 없음 |
| `VisionImaging.Abstractions` | 이미지 식별·규격·피라미드 단계 수, IImageLoader·프레임·타일·픽셀 버퍼 소유권 계약 | 없음 |
| `VisionCanvas.Core` | 편집 문서·명시적 계층·선택·입력 상태·Undo/Redo, 편집용 기하 계산 | Geometry, Imaging.Abstractions |
| `VisionImaging` | NetVips 원본 영상 I/O·피라미드와 소스 구현 | Geometry, Imaging.Abstractions |
| `VisionCanvas` | WPF/Win32 입력 연결, DX12 표시·GPU 수명, 가시 타일 계획·최신 프레임 mailbox·viewport 계약 | Canvas.Core, Imaging.Abstractions 및 필요한 중립 기하 |
| `VisionInspection` | 원본 검사 실행·reader lease·검사 예산·결과 | Geometry, Imaging.Abstractions, VisionImaging, VisionAlgorithm |
| `TeachingToolWpf` | 화면·레시피·검사 조립·제품 판정·통합 편집 이력 | 필요한 라이브러리를 명시적으로 조립 |

`VisionAlgorithm`의 플랫폼 독립성과 `VisionCompute.ILGPU`의 선택형 CUDA 경계는 그대로다. WPF·DX12·OpenCV의 `Mat`을 새 중립 계약에 노출하지 않는다.

## 보존할 동작

- ROI는 원본 이미지 좌표의 `double` 값과 실제 사각형·원·삼각형·폴리곤 형상으로 전달한다. 편집 프레임이나 AABB로 축약하지 않는다.
- 명시적 `ParentId`, 부모 밖 자손, 변형 중복 방지, 연쇄 삭제와 정확한 Undo/Redo를 유지한다.
- TeachingToolWpf의 ROI·레시피·이미지 교체는 기존 하나의 원자적 이력을 사용한다. 라이브러리 분리를 이유로 이력 스택을 둘로 나누지 않는다.
- Canvas의 렌더 문서는 불변 스냅샷과 공간 인덱스/블록을 그대로 사용한다. 매 프레임 DTO 변환·ROI 전체 복사·GPU identity 캐시 재작성은 하지 않는다.
- 이미지 버퍼의 이전·보유·해제, 독립 원본 reader lease, 취소와 오래된 결과 거부를 유지한다. 화면 LOD나 캡처를 검사 원본으로 대신하지 않는다.
- 검사 결과는 호스트가 공급자 중립 윤곽·점·색·라벨로 변환해 표시한다. Canvas는 검사를 실행하거나 OK/NG를 판정하지 않는다.
- 기존 OpenCV·논문 형상·자체 기하 검색 결과와 점수·불완전 경고를 바꾸지 않는다. 이번 정리를 검사 지연 목표 달성으로 표시하지 않는다.

## 구현 순서와 수용 기준

1. **공통 타입 분리:** 기하와 이미지 계약을 각각 소유 프로젝트로 옮기고 나머지 편집 기능을 `VisionCanvas.Core`에 둔다. 소스 중복 컴파일 없이 빌드되고 중립 프로젝트에 WPF/DX12/native decoder 참조가 없어야 한다.
2. **호출부와 참조 갱신:** 앱·Canvas·영상 I/O·검사·테스트·벤치마크를 새 소속으로 연결한다. 특히 `VisionInspection`에서 편집 코어에 대한 직접·전이 참조를 모두 제거한다. namespace/assembly 변경의 재빌드 필요성을 사용 안내에 기록한다.
3. **독립 사용 예제:** `samples/VisionCanvas.Sample`에서 공개 API만 사용해 이미지 표시, ROI 편집·Undo/Redo, 임의 오버레이를 조립한다. 샘플은 TeachingToolWpf·VisionInspection·VisionAlgorithm을 참조하지 않는다.
4. **회귀 확인:** 프로젝트 참조 그래프, 기존 기하·편집·영상 수명·검사·앱 테스트, Release 빌드, 독립 샘플 및 기존 DX12 앱 실행을 확인한다. 실행할 수 없는 항목은 이유와 함께 `NOT_RUN` 또는 `BLOCKED`로 남긴다.
5. **문서 정합성:** README·현재 계획·작업 목록에서 새 경계를 안내한다. 과거 ADR의 실행 증거는 당시 기록으로 보존하고 이 결정의 관계를 명시한다.

실행 결과는 검증 문서 한 곳에 기록한다. 독립 샘플의 성공과 현장 8K/24K 성능, Linux 실실행, 물리 입력 전체 E2E는 구별한다. 새 추상화나 설계 원칙의 성능 예외는 현재 범위에 포함하지 않는다.
