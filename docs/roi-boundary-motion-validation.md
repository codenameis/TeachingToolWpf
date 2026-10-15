# 일반 ROI 이탈·빠른 이동·중첩 선택 검증

후속: 벽 접촉 뒤 접선 방향 슬라이딩과 복수 선택 회전 제거는 [ADR-049](decisions/0049-roi-sliding-and-group-interaction.md), 대량 그룹60FPS 실측은 [새 검증](group-interaction-validation.md)을 따른다. 아래 숫자는 이전 작업 당시의 기록이다.

2026-10-15. 현재 main에서 로컬 수정·검증만 수행했다. 이전 미커밋 작업과 사용자의 dotnet-anti-slop 폴더를 보존했다. [동작 결정](decisions/0048-roi-boundary-motion-and-nested-selection.md).

## 화면에서 사용하는 방법

1. 겹친 ROI 위를 더블클릭하면 실제 연결된 계층의 ROI/그룹을 작은 항목부터 순환한다. 예를 들어 리프가 선택된 상태에서 더블클릭하면 ROI 그룹 → 모듈 → 루트 → 리프 순서다. 선택은 목록과 동기화되며 문서와 이력을 바꾸지 않는다.
2. 선택한 그룹은 내부 어디를 잡아도 그룹 전체를 드래그할 수 있다. 하위 항목을 고르려면 더블클릭을 사용한다. Ctrl 다중 선택과 Alt 겹침 순환은 유지한다.
3. 부모 내부의 일반 ROI와 그룹은 경계에서 멈춘다. 이동·회전의 중간 경로가 부모 밖을 지나는 경우에도 멈추므로 포인터를 빨리 움직여 다른 유효 위치로 건너뛸 수 없다. 크기 변경/속성 입력으로 확정되는 도형도 부모 안에 유지한다.
4. **사용자 확정에 따라 부모 밖 리프 편입은 허용한다.** 편입 순간 도형을 옮기거나 부모를 키우거나 ROI를 새로 만들지 않는다. 밖에서 편입된 ROI를 부모 안으로 이동해 확정하면 이후 편집부터 이탈 제한이 적용된다. 밖에서 시작한 하나의 드래그 중간만 잠깐 안에 지나간 것은 확정으로 취급하지 않는다. Undo는 편입 전/밖에 있던 과거 상태까지 정확히 복원한다.

## 재현과 회귀

- 수정 전 `RoiBoundaryInteractionTests`5개 모두 FAIL: 일반 리프의 동기/준비된 이동이 부모를 이탈, 사각형/원의 빠른 이동이 오목 부모의 홈을 건너뜀(x70→330), 긴 그룹의 회전이 좁은 부모를 넘어180도로 도약. `artifacts/roi-boundary-repro.log`.
- 수정 후 PASS: 위5개와 부모3종×자식2종의 중간 회전 pose 검사, 오목 부모의 자동 ROI 배치. 오목 홈을 내부 경로로 우회하는 동기/prepared 이동이 확정 시 되튀지 않는2개 회귀도 포함한다. 새 `RoiMotionBounds`는 이동 선분/접촉 각도 기반이고 테스트의 중간 pose 샘플 검사는 독립 검증 수단이다.
- PASS: 네 단계의 Down/Up/DoubleClick/Up 순환, 선택 그룹 내부 드래그, Ctrl 다중 선택, 선택만으로 이력/Redo가 바뀌지 않는 회귀.
- PASS:4096개 초과 문서에서 worker 준비 중 마지막180도 입력만 전달되어도 첫 경계에서 멈춤. 지연 완료·미리보기·prepared 확정·Undo 동등성.
- PASS: 밖의 독립 ROI 편입 시 원래 도형/ID/부모/번호 보존, 내부 이동 확정 후 이탈 제한, Undo로 밖의 과거 위치 복원. 새 그룹 편입/승격과 최대4단계도 유지한다.
- 기존의 자유 이탈 기대값은 새 요구에 맞게 포함 판정으로 변경했다. 그룹/자손 이동, 삭제, 속성, 원자성, Undo/Redo 검사를 삭제하지 않았다. 예전의 밖에 만든 자식 fixture는 신규 생성 규칙에 맞춰 내부에 배치했다. 기존 밖의 **편입** 회귀는 유지했다.

## 실행 증거

- PASS: 전체1,298개 = Core349 + 앱302 + Canvas235 + Algorithm249 + Compute52 + Inspection111, 실패/건너뜀0. `artifacts/roi-boundary-full-tests.log`.
- PASS: 최종 솔루션 Release 빌드, 경고0/오류0. `artifacts/roi-boundary-final-build.log`.
- PASS: RTX3060 Laptop GPU/debug와 WARP에서 `--smoke --hierarchy-rules-smoke` 종료0. 실제 HWND 입력의 리프/그룹 경계, 네 단계 더블클릭, 빠른 회전(45→170→180도)/이동(오목 홈을 가로지르는 큰 포인터 간격), preview/commit 일치, 이력, 외부 편입, 기본/설정 색상 픽셀. `artifacts/roi-boundary-20261015/gpu-hierarchy-verified/artifacts/smoke.log`, `warp-hierarchy-verified/artifacts/smoke.log`. 내부 경로로 홈을 우회한 후 mouse-up 위치가 유지되는 경우도 두 장치에서 확인했다.
- PASS: 최종 GPU 전체 앱 `--smoke --gpu-debug` 종료0. 툴바/계층/속성/이력/스타일/도형/검색범위/필터/8K 미니맵·라이브/렌더러 재시작을 보존했다. `artifacts/roi-boundary-20261015/gpu-full-verified/artifacts/smoke.log`. 캡처 대기 수정 후에는 이 네이티브 검증과 Release 빌드·스타일 검사를 재실행했다.
- PASS: 변경 C# whitespace/style 검사, 매뉴얼55개 삽입 소스 일치, HTML11개·로컬 참조298개, `git diff --check`. 매뉴얼 브라우저 배치는 이번에 변경하거나 재검증하지 않았다.
- WARP 최종 시도 중1회 FAIL은 캡처 PNG 생성 직후 파일 잠금을 보유한 저장 worker와 검증 읽기의 경쟁이었다. 실패 로그는 `artifacts/roi-boundary-20261015/warp-hierarchy-final/artifacts/smoke.log`에 보존했다. 두 계층 진단은 공유 위반(Windows32)만 기존10초 한도에서 재시도하도록 수정했다. 잘못된 픽셀/파일 형식/다른 I/O 오류는 계속 실패하며 재실행은 새 출력 디렉터리를 사용했다.

## 원칙 검토

| 원칙 | 확인 |
|---|---|
| SRP | 기하의 경로 포함, Core의 계층 예외, 입력의 선택 정책, VM의 안내를 분리했다. |
| OCP | 기존 도형/입력/이력 계약을 사용하며 WPF별 도메인 분기를 추가하지 않았다. |
| LSP | 동기/준비된 편집과 GPU/WARP의 상태 결과·취소·Undo 계약을 검증했다. |
| ISP | IViewportInput/이미지/렌더 계약에 새 메서드를 강제하지 않았다. |
| DIP | Geometry/Core는 WPF·GPU를 참조하지 않으며 VM이 기하 알고리즘을 구현하지 않는다. |
| Information Expert | 실제 윤곽의 접촉은 Geometry, 부모/자식 예외는 RoiGroupBounds가 판단한다. |
| Creator | EditorSession이 새 ROI 불변식을 보존하고 RoiPlacement는 값만 계산한다. |
| Controller | InteractionController가 선택·제스처를 조정하고 EditorSession이 확정한다. |
| Low Coupling | HWND/렌더러를 수정하지 않고 기존 입력 경계에서 해결했다. |
| High Cohesion | 연속 경로 판정은 RoiMotionBounds에, 계층 판정은 Core에 모았다. |
| Polymorphism | 기존 도형 계약과 입력 도구를 유지한다. 새 가상 구현 계층은 필요하지 않다. |
| Pure Fabrication | 기존 선택/피벗 캐시와 준비 worker로 비용을 관리한다. |
| Indirection | View의 이벤트 전달·VM 명령·Core 입력/편집 연결을 유지한다. |
| Protected Variations | 원/다각형 세부 기하와 WPF 입력 방식 변화가 서로 침투하지 않는다. |

대량 부모/자식 편집의 엄격한60FPS, 현장 이미지, 별도 저사양PC는 이번 기능 회귀로 보장하지 않는다. 일반 ROI와 그룹의 공간/시간 경로를 임의 샘플 간격으로 생략하는 성능 예외는 적용하지 않았다.
