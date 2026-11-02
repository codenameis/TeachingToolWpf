# ADR-069 — 공통 입력 잠금과 선택 없는 ROI 탐색

- 상태: 구현, 로컬 검증
- 날짜: 2026-10-16

## 요청

사용자는 Vulkan/WPF/wgpu/Skia/DX12에서 선택 ROI의 방향키 이동, 계속 갱신되는 검사 화면의 전체 입력 차단, 외부 목록을 통한 선택 없는 ROI 확대·축소를 요청했다. 목록 동작은 “해당 ROI 화면 맞춤 + 전체 보기”로 확정했다.

## 결정

- `InteractionController.IsEnabled`는 명시적인 사용자 입력 허용 상태다. 끄면 기존 제스처의 Cancel을 사용해 preview/다각형 초안을 정리하고 포인터·휠·키 이동을 막는다. `NavigationOnly`는 일시적인 편집 제한으로 유지하며 팬/휠을 허용하는 기존 의미와 구별한다.
- HWND의 `IsInputEnabled`는 키/문자/시스템 키 및 마우스 메시지 전체를 차단한다. 프로퍼티 전환 시 진행 중인 입력을 취소하고 이 HWND가 소유한 capture만 해제한다. WPF `IsEnabled`, 기존 가용성 delegate와 Core 잠금도 검사한다. 렌더 스레드/프레임 mailbox는 입력 잠금에 의존하지 않는다.
- `NudgeSelection`은 기존 `EditorSession.TranslateSelection`을 사용한다. WPF의 키→원본1/10px 변환은 공통 `ViewportKeyboard`가 맡으며 메인 앱의 대규모 선택 비동기 준비 경로는 보존한다. 텍스트·목록 등 자식 컨트롤의 입력을 View에서 먼저 구별한다.
- `EditorSession.FocusRoi`/`FocusBounds`/`FitImage`는 기존 `SetViewport`를 사용한다. ROI 선택·이력을 바꾸지 않고 최신 기하로 맞춘다. 사용자 입력 잠금은 프로그램이 게시하는 검사/표시 요청을 막지 않는다.
- 여러 실제 호출자가 있는 공통 탐색 VM/바를 재사용하고 기존 앱/샘플 VM은 가용성과 명령을 연결한다. 반복셀은 배치 한 행으로 유지하고 목록은 펼칠 때만 갱신한다. 선택용 목록과 탐색용 목록은 별도의 선택 상태를 갖는다.

## 원칙 검토

기하·제약·이력은 Core, Windows 키/메시지와 포커스는 WPF/Controls, 화면 상태와 가용성은 VM, 장치/프레임 수명은 기존 renderer에 둔다. 기존 Command/Memento·불변 문서·공급자 경계와 명시적 수명을 유지한다. 변경/구독의 새 소유권 계층이나 렌더러별 기하 중복, 새 패키지는 만들지 않는다. 공통 UI는 실제 여섯 호스트에서 사용하므로 단일 호출자를 위한 추상화가 아니다.

SRP/OCP는 입력·탐색 책임의 기존 경계와 렌더러 공통 경로, LSP는 동일한 HWND 입력 동작, ISP/DIP는 기존 영상/레이아웃 계약으로 검토했다. GRASP의 정보 전문가·Creator·Controller·낮은 결합/높은 응집·다형성·순수 가공·간접화·변화 보호는 기존 문서/제스처/VM/렌더 수명 분리를 유지한다. 성능을 이유로 설계 원칙을 완화한 예외는 없다.

검증·사용법·공개 API 및 미검증 범위는 [공통 입력 안내](../canvas-interaction-modes.md)를 따른다. 지속60FPS나 실제 카메라 장시간 안정성을 이번 입력 기능의 통과 결과로 표현하지 않는다.
