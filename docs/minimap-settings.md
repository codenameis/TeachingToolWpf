# 미니맵 표시와 8방향 배치

2026-10-16. 현재 main에서 로컬 수정·검증. 별도 CPU 매칭 변경은 이번 범위에 포함하지 않는다.

메인 앱의 **설정 → 미니맵**에서 `미니맵 표시`를 켜거나 끈다. 위치 목록은 좌상단, 상단 중앙, 우상단, 오른쪽 중앙, 우하단, 하단 중앙, 좌하단, 왼쪽 중앙 순서다. 기본값은 기존의 표시 ON·우하단이며 숨겼다가 켜도 선택 위치를 유지한다. 실행 중 설정이며 앱 재시작 후 저장/복원은 추가하지 않았다.

## 동작과 재사용

- Core의 불변 `MinimapOptions`를 입력과 렌더 장면이 공유한다. `MinimapLayout.Create`가 12 DIP 여백, 크기, 영상 비율과 클릭 좌표를 함께 계산한다.
- `InteractionController.MinimapOptions`와 `RenderScene.MinimapOptions`에 같은 값을 전달한다. 메인 VM은 Input.Changed로 새 장면을 게시한다. 기존 호스트는 기본값으로 계속 동작한다.
- 숨기면 미니맵 렌더 배치와 클릭·휠·더블클릭 가로채기가 사라진다. 원래 미니맵 위치에서 일반 ROI 입력이 가능하다. 파일/라이브 표시의 기본 영상 리소스와 overview 관리는 그대로 유지한다.
- 미니맵 드래그 도중 숨김/위치를 바꾸면 기존 드래그를 취소하고 시작 viewport를 복원한다. 일반 ROI 드래그를 설정 변경만으로 취소하지 않는다. 잘못된 enum은 상태 변경 전에 거부한다.
- 문서·선택·Undo/Redo는 변경하지 않는다. 기존처럼 viewport가 320×240 DIP보다 작거나 이미지가 없으면 미니맵은 나오지 않는다.
- 레거시 별도 반복 격자 시험 창의 `RepeatedRoiInput`에는 설정 UI를 추가하지 않았다. 메인 편집기의 일반/반복 ROI는 공통 InteractionController 경로를 사용한다.

```csharp
input.MinimapOptions = new(true, MinimapPosition.TopLeft);
scene = scene with { MinimapOptions = input.MinimapOptions };
// 숨김: 위치를 보존한다.
input.MinimapOptions = input.MinimapOptions with { IsVisible = false };
scene = scene with { MinimapOptions = input.MinimapOptions };
```

## 검증

- PASS: Core 미니맵/더블클릭 27건. 8방향 실제 클릭 이동, resize 경계, 숨김 후 그리기, 옵션 오류 원자성, 변경 중 드래그 취소.
- PASS: Canvas 이미지 표시 14건. 숨김 시 overview 배치 제거, 8방향×DPI 1/1.5/2의 미니맵 영상 정점과 입력 layout 일치.
- PASS: 표시 설정 13건. 메인 앱/설정 XAML Release 빌드, VM 설정→입력→렌더 장면 일치, 위치 보존, 문서·이력 보존.
- NOT_RUN: 실제 GPU/WARP 화면 캡처·물리 마우스 조작·이번 변경의 프레임 성능 측정. 렌더 mesh 검증을 실제 표시/60FPS 검증으로 표현하지 않는다.

## 설계 원칙 검토

| 원칙 | 확인 |
|---|---|
| SRP | Core 배치/입력, Canvas 그리기, 앱 설정 UI 책임 유지 |
| OCP | 기존 선택/그리기 전략을 수정하지 않고 배치 옵션 확장 |
| LSP | 기존 기본 배치·탐색·취소 계약 회귀 확인 |
| ISP | IViewportInput에 사용하지 않는 설정 메서드를 추가하지 않음 |
| DIP | Core는 WPF/DX12/앱을 참조하지 않음 |
| Information Expert | MinimapLayout이 화면 크기와 배치 계산 소유 |
| Creator | 호스트가 옵션과 장면을 만들고 기존 GPU 수명 소유자 유지 |
| Controller | View는 바인딩, VM은 설정 전달, 입력 컨트롤러는 취소 처리 |
| Low Coupling | 동일한 불변 옵션으로 표시/입력 연결 |
| High Cohesion | 미니맵 규칙을 기하·입력·painter의 기존 위치에 유지 |
| Polymorphism | 새 입력 구현이 필요하지 않아 기존 IViewportInput 유지 |
| Pure Fabrication | 새 설정 서비스/전역 manager 없이 기존 장면과 VM 사용 |
| Indirection | XAML→VM→Core/렌더 장면의 기존 경계 사용 |
| Protected Variations | DPI·화면 크기·위치 변화는 공통 DIP layout에서 처리 |

설계 원칙 예외와 새 패키지 의존성은 없다.
