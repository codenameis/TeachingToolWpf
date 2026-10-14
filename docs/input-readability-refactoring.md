# ROI 입력 코드의 신입 친화적 리팩터링 — 2026-10-14

## 범위와 가정

사용자가 매뉴얼보다 코드 리팩터링을 먼저 요청했다. 현재 `main`의 NetVips 제거 이후 코드에서 마우스 입력 → 도구/선택 판단 → 미리보기 → 확정/취소 경로를 정리했다. 기존 HTML 개편 작업은 보존하고 이번에는 변경하지 않았다. 파일 로더 교체나 프로젝트 계층 재편은 이 변경에 포함하지 않는다.

가독성·GRASP·SOLID·클린 코드·클린 아키텍처·단순한 코드·명확한 변수명을 기준으로 삼는다. 새로운 동작, 공개 API 변경, 새 패키지나 범용 추상화는 도입하지 않는다.

## 변경 내용

- `InteractionController.Down`에서 폴리곤 이어 그리기, 도구별 제스처 선택을 이름 있는 private 메서드로 나눴다. 미니맵 → pan → 탐색 전용 → 그리기/마스크 → 선택의 기존 우선순위와 이벤트 발생 순서를 유지한다.
- `StartSelectionGesture`에서 선택 핸들 판정을 분리했다. Alt 순환 선택은 핸들을 건너뛰며, 일반 ROI와 반복 셀의 Ctrl/Shift·선택 유지·이동 시작 규칙을 보존한다.
- 한 파일에 있던 기존 제스처 8개와 도구·수정키·임시 표시 타입 3개를 각각 파일로 옮겼다. 타입·인스턴스·상속 계층을 새로 늘린 것이 아니다. `Gestures.cs`에는 공통 계약과 아무 편집도 하지 않는 상태만 남겼다.
- Roslyn 심볼 Rename으로 73개 식별자와 참조를 함께 갱신했다. `a/b`는 `startInImage/endInImage`, `View`는 `InitialViewport`, `delta`는 `translationInImage`, 회전의 `current`는 `currentDirection`으로 드러냈다. 공개 메서드의 매개변수 이름은 유지했다.
- 그리기·크기 조절·마스크의 긴 삼항식을 조건문으로 풀었다. 회전은 외적·내적을 이름 있는 중간 값으로 읽도록 바꿨으며 연산 순서·각도 누적·Shift 스냅을 보존했다.
- 클릭 허용 거리와 드래그 시작 기준에는 DIP/원본 단위를 드러내는 상수를 사용했다. `Up`에서 활성 제스처를 비운 뒤 최종 위치 반영·확정·실패 취소·표시 정리하는 순서는 유지했다.

## 읽는 순서

1. `IViewportInput`: 호스트가 전달하는 입력 계약.
2. `InteractionController.Down` → `StartGesture`: 현재 입력에 맞는 제스처 선택.
3. `StartSelectionGesture` → `StartHandleGesture` → `HitSelectableRoi`: 핸들 우선과 실제 ROI 선택.
4. `MoveGesture` / `RotateGesture` / `ResizeGesture`: 시작 뷰포트에 대한 원본 좌표 계산.
5. `EditGesture`: `BeginPreview` 한 번, `Complete` 또는 `Cancel` 연결.
6. `InteractionController.Up`: 마지막 포인터를 반영한 뒤 확정. 실패하면 취소하고 임시 표시를 정리.

`DrawGesture`와 `MaskDrawGesture`는 초안을 들고 있다가 완료 시 문서에 추가한다. `MarqueeGesture`는 선택 상자를 표시한 뒤 완료 시 선택을 요청한다. 확정 문서·계층·이력은 여전히 `EditorSession`, 도형 계산은 `VisionGeometry`/기하 헬퍼, WPF/DX12 변환은 기존 바깥 계층이 소유한다.

## SOLID·GRASP 검토

| 원칙 | 이번 변경의 판단 |
|---|---|
| SRP | 컨트롤러의 입력 선택과 제스처의 좌표/수명 흐름을 나눠 읽도록 정리. 문서 변경 책임을 UI로 옮기지 않음 |
| OCP | 기존 IGesture 변형 지점 유지. 신규 공급자나 조건 설정 체계는 만들지 않음 |
| LSP | Move/Complete/Cancel 계약과 예외·취소 결과를 기존 회귀 및 추가 수명 회귀로 검증 |
| ISP | IViewportInput·IGesture의 기존 작은 계약 유지. 사용하지 않는 새 메서드 강제 없음 |
| DIP | Core에 WPF·DX12·코덱 참조 없음. 기존 호스트 콜백 주입과 순수 편집 의존성 유지 |
| Information Expert | 포함·resize는 기존 기하 책임, 확정·이력은 EditorSession에 유지 |
| Creator | 컨트롤러가 실제 선택한 제스처를 생성. 새 factory/서비스 로케이터 없음 |
| Controller | 입력 우선순위와 전달 순서를 명시. 문서 사전 직접 수정 없음 |
| Low Coupling | 기존 공개 입력 계약과 프로젝트 참조를 바꾸지 않음 |
| High Cohesion | 한 파일의 여러 동작을 기존 타입별로 나누고 private 판단 메서드에 의도 부여 |
| Polymorphism | 기존 State 패턴의 IGesture.Move/Complete/Cancel 호출 유지 |
| Pure Fabrication | 제스처라는 기술적 입력 책임을 유지. 별도 범용 manager 추가 없음 |
| Indirection | 기존 호스트→입력 계약→편집 세션 연결 유지. 새 중계 계층 없음 |
| Protected Variations | 도메인에 네이티브 이벤트·파일 코덱·GPU 수명 코드 유입 없음 |

원칙 예외는 적용하지 않았다. 파일 분리만으로 프로젝트 전체의 설계 품질을 증명했다고 주장하지 않는다.

## 검증

- PASS: 수정 전 Core 회귀296개.
- PASS: 제스처 분리 및 입력 선택 추출 후 기존 Core296개.
- PASS: 최종 위치·확정 콜백 시점·확정 실패 원자성·탐색 전용 pan 회귀6개 추가 후 Core302개.
- PASS: 최종 전체 Release 솔루션 빌드, 경고0/오류0. Core302·Canvas228·Imaging274, 총804개 회귀 통과(실패/건너뜀0).
- PASS: 변경 파일의 IDE1006·IDE0003·IDE0011·IDE0047·IDE0048·IDE0161·IDE0065 검사. 이름·중괄호·namespace 규칙을 확인했다.
- PASS: 변경한 Interaction 폴더와 추가 수명 테스트의 공백·CRLF 검사, `git diff --check`. 추가 테스트의 줄바꿈을 수정한 뒤 재확인했다.
- FAIL(기존 범위): `scripts/Format-Code.ps1 -Verify`의 전체 공백 검사에서 이번 변경 밖 파일의 ENDOFLINE/WHITESPACE 문제가 남는다. 무관한 파일을 일괄 재포맷하지 않았다. 전체 컨벤션 통과로 보고하지 않는다.
- PASS: 아래 세 입력 경로의 변경 전후 관리 힙 할당량 동일. 처리 시간은 순서에 따른 변동을 포함해 기록하며, 속도 향상이나 모든 경로의 성능 동등성을 단정하지 않는다.
- NOT_RUN: 전체 앱/GPU/WARP 실제 입력 및 프레임 성능. 기존 60FPS 미달은 유지하며, Core 회귀를 성능 증명으로 표현하지 않음.

초기 전체 빌드는 C: 여유 공간0바이트로 중단됐다. 사용자가 명시적으로 승인한 과거 비교 캐시 `artifacts/netvips-boundary/large-before/artifacts/image-cache/61C9829EDE8924F0A00B3A9B5ECEB513A955D915DAB1793BD6912DA7ADF31A97/0.v` 한 파일(6,442,453,311바이트)만 삭제한 뒤 다시 검증했다. 원본 이미지와 측정 로그는 보존했다. 해당 과거 캐시는 재사용 전 재생성이 필요하다. 샌드박스의 Roslyn/MSBuild 파이프 접근 거부는 일반 권한으로 검사를 실행해 해소했다.

## 입력 처리 비교

Windows/.NET10 Release, `DOTNET_TieredCompilation=0`, 동일 측정 프로그램에서 수정 전/후 Core DLL을 각각 실제 로드했는지 확인했다. ROI 한 개의 이동·회전 미리보기와 위치 변화 없는 Down/Up 쌍을 각각10,000회 준비 실행한 뒤20,000회씩7표본 측정했다. 각 표본 전 GC를 수행하고 `Stopwatch`와 `GC.GetAllocatedBytesForCurrentThread`를 사용했다. 두 번째 실행은 후→전 순서로 뒤집었다. 시간은 각 실행의7표본 중앙값이며, 할당은20,000회 전체 바이트다.

| 경로 | 1차 전→후(ms) | 2차 전→후(ms, 실행은 후→전) | 전=후 할당(byte) |
|---|---:|---:|---:|
| 이동 | 0.9200 → 0.8936 | 0.8074 → 0.9193 | 1,598,960 |
| 회전 | 2.2214 → 2.0691 | 2.1721 → 2.1571 | 2,400,000 |
| Down/Up | 13.8046 → 14.6025 | 15.4234 → 14.8075 | 29,440,000 |

시간 차이는 실행별 방향이 달라 일관된 개선이나 악화로 해석하지 않는다. 이 작은 측정에는 실제 GPU, WPF 이벤트 전달, 대량/반복 ROI, 이미지 타일 로딩이 포함되지 않는다. 공개 API·이벤트 순서·Undo/Redo를 유지하고 새 객체/컬렉션/복사 경로를 추가하지 않았다는 코드 검토와 함께 제한된 근거로 사용한다.

원시 표본과 DLL 해시는 [입력 측정 결과](input-readability-results.json)에 남겼다. 변경 전 비교 DLL과 임시 Roslyn Rename/입력 측정 도구는 무시 대상인 `artifacts/input-readability`에 있다. 추가 회귀는 `tests/TeachingToolWpf.Core.Tests/InteractionLifecycleTests.cs`에 남긴다.
