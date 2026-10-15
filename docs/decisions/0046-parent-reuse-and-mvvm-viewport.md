# ADR-046: 기존 부모 재사용과 Viewport의 MVVM 연결

2026-10-14. 사용자 요청: 이미 부모 그룹이 있는데 떨어진 ROI를 그룹화하면 ROI가 하나 더 생기는 버그를 수정하고 TeachingToolWpf의 Viewport 및 다른 화면 연결을 MVVM에 맞춘다. 현재 main에서 로컬 수정·검증만 진행한다.

## 그룹화

2026-10-15 표시·명명 후속: `RoiStyles`는 역할별 색상/두께 입력을, `MainWindowViewModel`은 MainWindow의 화면 상태/명령을 맡는다. 기존 타입과 partial 파일을 직접 바꾸며 중계 ViewModel을 추가하지 않는다. 기본 추가/그리기는 부모가 없는 리프 ROI를 만들고 처음부터 자식 스타일(기본 초록색)을 쓴다. 부모 연결 여부는 관계이며 리프의 표시 역할을 바꾸지 않는다. 그룹 역할은 실제 자식 또는 `IsGroup`으로 판단한다. `Default` 표시 슬롯은 축소 요약·반복 ROI·공통 선택 표시용으로 보존하고 설정 이름을 ‘공통 표시’로 명확히 한다. ADR-045의 독립 ROI 별도 표시 설명은 이 후속 규칙으로 대체한다.

ADR-045를 다음처럼 보완한다. 선택한 조상을 제외한 유효 루트 중 부모 역할(명시적 그룹 또는 실제 자식 보유)이 정확히 하나이면, 부모 없는 나머지 ROI를 그 부모에 연결한다. 부모의 ID·이름·형상, 총 ROI 수, 생성 번호를 보존한다. 부모 자체가 상위 그룹에 속해 있어도 대상 부모로 선택할 수 있다. 이미 다른 부모가 있는 자식은 이동시키지 않는다.

부모 역할이 없으면 기존처럼 외곽 그룹을 만든다. 부모 역할이 둘 이상이면 새 상위 그룹을 만든다. 두 경우 모두 최대 4단계를 검사한다. 4단계 루트에 독립 리프를 연결하는 동작은 5단계를 만드는 것이 아니므로 허용한다. 기존 ROI 연결에는 공간 포함 검사를 추가하지 않는다. 버튼 가능 여부와 실행은 같은 Core 규칙을 사용하며 동기·준비 명령, 취소, 번호, 통합 이력을 보존한다.

## MVVM 경계

2026-10-15 MainWindow 코드 비하인드 재검토: 서비스 생성/주입과 해제 순서는 `App.xaml.cs`의 조립 지점으로 옮긴다. `App`이 창을 만들고 `Bind(model)` 뒤 표시하므로 `StartupUri` 자동 생성은 제거한다. 아래 폴더 이동 당시의 URI/창 내 조립 설명을 이 결정으로 대체한다. 진단 플래그 우선순위는 `Diagnostics/DiagnosticStartup`에 보존하며 일반 시작은 VM의 `Initialize` 명령을 호출한다. 이 명령의 기존 `AsyncCommand` 오류 경계가 초기화 실패를 Status에 반영한다. 진단에서 직접 호출하는 `InitializeAsync`는 실패를 계속 호출자에게 전달한다. CPU/GPU 성능 문구와 속성 알림도 `MainWindowViewModel.UpdatePerformance`에서 처리한다.

2026-10-15 폴더 구조 후속: 사용자 요청에 따라 앱의 Presentation 폴더를 없애고 Models/Views/ViewModels로 책임을 나눴다. MainWindow는 Views로 옮기고 App.xaml의 시작 URI와 x:Class/namespace를 함께 변경한다. 순수 데이터·검사 판정/조건은 Models, 화면 상태/설정 입력은 ViewModels, 이미지 교체/라이브/편집 준비는 Services, 명령은 Commands, 알림 기반 타입은 Infrastructure에 둔다. Controls/Themes/Diagnostics와 공통 Canvas/Core의 소유권은 유지한다. 새 프레임워크나 중계 VM은 추가하지 않는다.

별도로 `부모 추가/그리기` 명령을 복구한다. `EditorSession.AddParent`가 `IsGroup = true`인 단일 ROI를 원자적으로 추가하므로 자식이 없어도 부모 색상과 그룹 규칙을 갖는다. 독립 초록색 ROI/선택 부모의 자식 생성은 기존 명령을 유지한다. 부모 그리기와 자식 대상은 동시에 설정되지 않으며 모드 변경·취소·Undo/Redo와 폴리곤 입력을 검증한다.

- `MainWindowViewModel`과 `RepeatedRoiViewModel`은 표시 데이터 `Scene`을 게시한다. `Dx12Viewport.Scene` 의존 속성이 기존 Submit 경로에 연결된다. 장면은 기존 불변 문서·캐시를 참조하고 ROI/원본 픽셀을 복제하지 않는다.
- XAML에서 Scene, PointerInput, KeyAction, LayoutState, 입력 허용 상태를 바인딩한다. `ViewportLayout`은 크기·DPI 값만 보유하며 Visual/HWND를 참조하지 않는다. View가 실제 크기를 갱신하고 VM이 배율·중심·장면에 반영한다. 기존 라이브러리 호스트의 Submit/Input/IViewportSurface API는 유지한다.
- VM에 전달하는 `IImageDisplay`는 소스 소유권 이전과 장치 재시작의 작은 서비스 계약이다. 장면 제출·레이아웃 조회·컨트롤 접근 메서드는 없다. 라이브 프레임은 기존 IFramePresenter와 mailbox의 소유권 전송을 유지한다. 네이티브 자원·버퍼를 WPF 바인딩 값으로 넘기지 않는다.
- 파일 선택창은 View의 ImageFilePicker가 열고 VM에는 선택 경로 또는 취소 결과만 전달한다. 검사 구현체 생성은 App 조립 지점이 맡고 VM은 주입된 검사 VM을 조정한다. DispatcherTimer는 창이 소유하고 종료 시 정지한다.
- 반복 ROI의 키별 편집·이미지 로드/소유권 이전은 VM에서 처리한다. 검사 로그의 스냅샷/성공·실패 상태는 VM, 직렬화/파일 저장은 InspectionRunLog에서 담당한다. View는 저장 경로만 받아 VM에 전달한다.

## View에 남기는 코드와 이유

MainWindow에 남긴 코드는 다음과 같다. 코드 비하인드의 줄 수 대신 UI 객체가 필요한지, 표시 정책/업무 판단을 직접 수행하는지로 구분한다.

| 남긴 코드 | 이유 |
|---|---|
| InitializeComponent, DataContext 설정 | 실제 XAML 컨트롤과 이미 만들어진 VM을 연결 |
| Viewport 이벤트 전달, Dispatcher 전환 | 렌더/타일/영상 이벤트를 UI 스레드로 전달. 문구 생성은 VM에 위임 |
| 텍스트·슬라이더 포커스 판별 후 키 전달 | 입력 중 편집 단축키가 값을 가로채지 않도록 WPF 포커스를 확인. 실제 키별 명령은 VM이 결정 |
| DispatcherTimer 시작/정지 | 창의 UI 스레드와 수명에 맞춰 tick만 전달. 라이브 상태 판단/필터 갱신은 VM이 수행 |
| ROI 목록 선택·교차선 링크와 구독 해제 | 실제 ListBox.SelectedItems/표시 레이어를 동기화하고 창 종료 시 연결 해제 |
| 설정창 생성·Owner·최소화 복원·Activate | 창 표시와 포커스 동작이며 편집 정책이 없음. 설정 값의 검증/적용은 기존 VM 명령 |

서비스 조립과 GPU 필터/검사 자원 종료는 위 표의 View 책임에서 제외하고 App으로 옮겼다. App은 첫 Closing을 취소하고 필터 취소/WhenIdle을 기다린 뒤 필터/GPU를 해제하고 다시 Close한다. View의 타이머 정지 → 비동기 필터 drain → Closed의 화면 연결 해제 → VM 해제 → 검사 CUDA 해제 순서를 보존한다. 이 순서를 VM으로 옮기면 구체적인 구현체의 수명을 VM이 알게 되므로 앱 조립 지점에서 관리한다.

다른 View의 WPF 숫자 검증 오류/포커스 처리, HWND의 WndProc/포인터 캡처와 DX12 리소스 수명도 View/렌더러에 남긴다. 이를 VM에 넣으면 오히려 구체적인 UI와 네이티브 수명에 VM이 종속된다.

`VisionCanvas.Controls.WpfTextRasterizer`는 `TextRasterizer`로 이름과 두 partial 파일을 변경한다. 구현체가 하나이고 이미 WPF 컨트롤 계층 안에 있으므로 접두사로 변형을 구별할 필요가 없다. WPF FormattedText/RenderTargetBitmap, 호출 STA, 픽셀/atlas 소유권 및 ITextRasterizer/ITextBatchRasterizer 계약은 유지한다. 플랫폼 독립 구현으로 바꾼 것은 아니며 외부에서 구체 타입을 직접 생성했다면 새 이름으로 수정해야 한다.

WPF 전용 VM에서 ICommand/Key와 설정용 Brush를 사용하는 것은 구체적인 Window/Control 인스턴스를 소유하는 것과 구별한다. 이번 요청은 플랫폼 독립 VM 라이브러리로의 전면 분리가 아니다. 프로젝트 재편, MVVM 패키지 추가, 서비스 로케이터, 설계 원칙 예외는 도입하지 않는다.

판단 근거: Microsoft의 [MVVM 설명](https://learn.microsoft.com/en-us/dotnet/architecture/maui/mvvm)은 표시 상태/명령을 VM에 두고 View와 서비스 경계를 구별한다. [Windows의 바인딩·MVVM 설명](https://learn.microsoft.com/en-us/windows/apps/develop/data-binding/data-binding-and-mvvm)은 화면 조작 및 이벤트 인자 추출 뒤 VM 호출을 View 코드의 적절한 역할로 설명한다. WPF의 HwndHost 세부 처리는 이 저장소의 기존 네이티브 소유권 계약을 따른다.

검증·제한·SOLID/GRASP 검토는 [검증 기록](../mvvm-viewport-validation.md)에 둔다.

2026-10-15 전체 View 후속은 [ADR-047](0047-view-responsibilities-and-group-bounds.md)을 따른다. RepeatedRoiWindow도 App 조립/VM 상태/진단 실행을 분리하고 ShapeMatchingPanel의 버튼/오류 표시를 바인딩으로 통일했다. ROI 추가/그리기는 그룹 하나 선택 시 연결된 ROI를 만드는 통합 명령으로 바뀌며 별도 부모 버튼은 유지한다. 모든 View별 유지 이유와 그룹 경계 제약의 검증은 [후속 기록](../views-mvvm-and-group-bounds.md)에 둔다.
