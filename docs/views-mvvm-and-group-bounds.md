# 전체 View의 MVVM 검토와 그룹 경계 검증

2026-10-15 후속 [ROI 이탈·빠른 입력·중첩 선택](roi-boundary-motion-validation.md)이 아래의 일반 리프 자유 이동 설명을 대체한다. 부모 안의 리프도 이탈을 제한하지만 사용자 답변에 따라 밖의 독립 리프 편입은 계속 허용한다. View/MVVM 검토 결과는 유지한다.

2026-10-15, 현재 main의 로컬 수정·검증. [결정](decisions/0047-view-responsibilities-and-group-bounds.md). 이전 MainWindow/TextRasterizer 작업의 증거는 [기존 기록](mvvm-viewport-validation.md)에 보존한다.

## 사용 동작

- `ROI 추가 / ROI 그리기`: 그룹 하나 선택 시 그 그룹에 연결된 ROI, 일반 ROI·무선택·다중선택 시 독립 ROI. R 단축키도 동일하다. 별도 자식 버튼은 제거했고 `부모 추가 / 부모 그리기`는 유지한다.
- ROI 그룹은 모듈 그룹, 모듈 그룹은 루트 그룹의 실제 윤곽 안에서만 이동·회전·크기 변경한다. 회전된 사각형·원·삼각형·오목 폴리곤을 실제 형상으로 판정하며 같은 경계는 허용한다. 드래그 미리보기와 확정값, 숫자 입력, 동기/준비된 편집에 동일하게 적용한다.
- 상위 그룹을 줄일 때도 하위 그룹을 경계 밖에 남길 수 없다. 선택한 그룹과 그 자손은 같은 변환을 따라간다. 그룹 트리 전체를 함께 옮길 때 내부 관계 때문에 이동이 막히지 않는다.
- 모듈을 선택해 그룹 해제하면 모듈 껍질이 사라지고 ROI 그룹이 루트 아래로 나온다. 루트를 해제하면 하위 그룹이 독립한다. 자손·선택·Undo/Redo는 유지한다. 연결로 형성한 그룹도 그룹 해제가 가능하다.
- 일반 리프 ROI는 그릴 때만 부모 내부로 제한한다. 이후 부모 밖 편집과 이미 있는 독립 리프의 편입은 허용한다. 밖에 있는 그룹 편입이나 밖에 있는 리프를 그룹으로 승격하는 작업은 원자적으로 거부한다.
- 최대4단계, 역할별 설정 색상, 기존 부모 재사용 시 추가 ROI/번호를 만들지 않는 규칙, 검색범위 타입 즉시 적용은 유지한다.

## 모든 View 확인 결과

기준은 코드 비하인드 유무가 아니라 화면 객체가 필요한 책임과 편집/표시 상태 판단의 분리다. 소스 위치는 `src/TeachingToolWpf/Views`이며 FilterPanel만 공통 라이브러리에 있다.

| View | VM 또는 조립 지점으로 옮긴 책임 | View에 남긴 코드와 이유 |
|---|---|---|
| MainWindow | 앞선 작업에서 초기화/오류/성능 상태는 MainWindowViewModel, 구현체 생성/해제는 App, 실행 모드는 DiagnosticStartup | XAML/VM 연결, Viewport 이벤트 전달·Dispatcher, 텍스트/슬라이더 포커스 확인, UI 타이머 수명, 목록/교차선 링크, 설정창 Owner/Show/Activate. 실제 컨트롤·창의 수명이 필요하다. |
| RepeatedRoiWindow | 로더/VM 생성·해제는 App, 로딩 취소·실패/성능/adapter 상태는 RepeatedRoiViewModel, 진단 실행/보고서는 RepeatedRoiRun | InitializeComponent/Bind, Viewport 이벤트 전달·구독 해제, TextBox 포커스 확인. 편집 키의 의미는 VM이 처리한다. |
| ShapeMatchingPanel | 학습/검색 버튼 IsEnabled 직접 대입 제거. 기존 VM의 CanExecute를 사용하고 오류 안내 Visibility는 HasInputErrors 바인딩 | WPF ValidationError→숫자 오류 상태 전달, 방식 변경 후 잘못된 TextBox 초안을 UpdateTarget으로 복원, Loaded/Unloaded/DataContext 구독, SaveFileDialog 경로 선택. 실제 저장·오류 문구는 VM/저장 서비스다. |
| PrimitiveShapeDetectionPanel | 이미 VM이 숫자/옵션 유효성과 명령 실행 가능 여부를 담당하므로 이동 불필요 | WPF 파싱 오류를 집계해 HasInputErrors 전달, DataContext 교체 시 이전 상태 해제. TextBox 바인딩에서 실패한 값은 VM setter에 도착하지 않으므로 View 어댑터가 필요하다. |
| ConfigPage | 설정 값/적용은 기존 설정 VM | InitializeComponent와 창 Close. 닫기 자체는 화면 수명이다. |
| RoiPanel | 편집 규칙은 MainWindowViewModel/EditorSession | InitializeComponent와 실제 목록 컨트롤을 선택 동기화 링크에 제공. 선택 규칙을 직접 계산하지 않는다. |
| InspectorPanel | 이미 바인딩/VM에 있음 | InitializeComponent만 있음. |
| RoiMaskPanel | 이미 바인딩/VM에 있음 | InitializeComponent만 있음. |
| RoiStylePanel | 이미 바인딩/VM에 있음 | InitializeComponent만 있음. |
| SourceBar | 이미 바인딩/VM에 있음 | InitializeComponent만 있음. |
| ViewportToolbar | 이미 바인딩/VM에 있음 | InitializeComponent만 있음. |
| VisionCanvas.Filters.Wpf/Controls/FilterPanel | 이미 독립 FilterPanelViewModel에 있음 | InitializeComponent만 있음. 앱/CUDA 역참조를 추가하지 않는다. |

App.xaml.cs는 ViewModel이 아닌 앱 조립 지점이다. 로더/검사/CUDA 등 구체 서비스 생성, 주입 및 종료 순서를 이곳에 둔다. 반복 ROI 창도 MainWindow와 같은 조립 경계를 사용한다. VM으로 구체 Window, DispatcherTimer, SaveFileDialog, DX12 해제 절차를 옮기지 않는다.

## 구현 경로와 보존 사항

`RoiGeometry.Covers`는 순수 기하, `RoiGroupBounds`는 Core 계층 제약이다. `RoiTransformSelection`은 움직이지 않는 상위 그룹 경계를 선택 스냅샷에 캐시하고 준비 작업에서 계산한다. 미리보기·확정·prepared 회전은 같은 제약을 호출한다. `RoiTransforms`는 크기/속성 변경 때 자식 그룹까지 확인한다. 이미지 경계와 그룹 경계를 모두 만족하는 공통 비율을 적용한다. 정확한0도 초기화가 불가능하면 상태를 바꾸지 않고 오류를 반환한다.

`RoiGroupEditor`의 편입/새 그룹, EditorSession의 추가/일괄 추가도 그룹 승격 시 불변식을 확인한다. 일반 리프에는 기존 자유 편집을 유지한다. `RoiSelectionFacts`와 그룹 해제는 실제 그룹 역할을 사용하여 연결로 만들어진 부모도 처리한다.

기존 테스트 두 곳은 과거에 허용했던 상위 밖의 **중간 그룹**을 포함하고 있었다. 새 규칙에 맞춰 그 중간 그룹만 상위 안으로 배치하고, 부모 밖 **리프**의 이동·삭제·Undo/Redo 검증은 그대로 유지했다. 실패 검사를 삭제하거나 기준을 낮춘 것이 아니다.

## 검증

- PASS: 솔루션 Release 빌드, 경고0/오류0. `artifacts/group-bounds-final-build.log`.
- PASS: 전체1,281개 = Core333 + 앱301 + Canvas235 + Algorithm249 + Compute52 + Inspection111. 실패/건너뜀0. `artifacts/group-bounds-full-tests.log`.
- PASS: 변경 C# 공백 정리와 명명/중괄호/namespace/using 스타일 검사. `artifacts/group-bounds-whitespace.log`, `artifacts/group-bounds-style.log`.
- PASS: GroupBoundsTests17개. 동기/준비된 이동·회전, 미리보기, 핸들/숫자 크기 변경, 부모 축소, 원/삼각형/오목 폴리곤, 경계 일치, 리프 자유 이동, 편입/그룹 승격 실패의 원자성, 언그룹/Undo/Redo.
- PASS: VM 테스트의 반복 창 초기화 실패·재시도·취소·이미지 소유권, 성능/adapter 상태, 통합 ROI 명령의 네 도형/선택/단축키/이력.
- PASS: 실제 RTX3060 Laptop GPU/debug의 전체 앱 회귀와 별도 계층 실행. `--smoke --gpu-debug --hierarchy-rules-smoke`에서 네 단계 기본/사용자 색상 픽셀, HWND의 ROI 그룹/모듈 드래그 제한과 이력, 모듈 숫자 resize, 연결로 형성한 모듈의 언그룹/Undo를 확인하고 종료0. `artifacts/group-bounds-20261015/gpu-hierarchy/artifacts/smoke.log`. 전체 앱 결과는 같은 상위 폴더의 `gpu-full/artifacts/smoke.log`.
- PASS: `--smoke --software-rendering`의 실제 WARP 앱에서 부모/통합 ROI 버튼, 포커스·목록·이력, 설정창 재열기/크로스헤어, 도형/마스크/필터, 8K live 시작·정지/렌더 재시작, 종료0. `artifacts/group-bounds-20261015/warp-full/artifacts/smoke.log`.
- PASS: `--inspection-smoke --gpu-debug`, 종료0. 실제 패턴 패널에서 `0`→`0.`→`0.720`, 잘못된 숫자의 학습 버튼 차단, 교정 후 활성화, 매칭 방식 전환 후 잘못된 초안 복원, 일반 도형 패널과 기존 원본 검사/표시. `artifacts/group-bounds-20261015/inspection/artifacts/inspection-smoke.log`.
- PASS: `--repeated-roi-benchmark --software-rendering`, 종료0. WARP/8192² 원본/100,020,001개 반복 셀에서 초기화·네이티브 더블클릭 선택 교체·개별 이동/회전·Undo/Redo·미리보기 취소·종료. `artifacts/group-bounds-20261015/repeated/artifacts/repeated-roi.json`의 Correctness=PASS. 이번 목적은 창/VM 기능 회귀이며 이 결과를 메인 편집기의 그룹 성능 보장으로 사용하지 않는다.
- PASS: 매뉴얼 본문55개 소스/발췌 일치, HTML11개·로컬 참조298개, `git diff --check`. 브라우저 배치·인쇄·실제 학습 효과 검증은 이번 범위에서 실행하지 않았다.

## 설계 원칙 검토

| SOLID | 확인 |
|---|---|
| SRP | View의 WPF 입력 추출, VM의 상태/명령, Core의 그룹 제약, 기하의 포함 판정을 분리했다. |
| OCP | 기존 입력/편집 경로에 공통 제약을 연결했다. 도메인에 WPF나 렌더러별 분기를 넣지 않았다. |
| LSP | 동기/준비된 편집이 같은 제한·Undo/Redo를 지킨다. 로더 실패/취소/늦은 완료의 소유권 테스트를 유지했다. |
| ISP | 기존 이미지/입력/장면 계약을 유지했다. 화면 조작을 위한 새 범용 인터페이스는 추가하지 않았다. |
| DIP | VM은 구체 Window/Viewport를 소유하지 않는다. 구현체 주입은 App이며 Core/Geometry는 WPF와 독립이다. |

| GRASP | 확인 |
|---|---|
| Information Expert | 실제 윤곽 포함은 RoiGeometry, 상하위 관계 제약은 RoiGroupBounds가 판단한다. |
| Creator | 문서가 생성/그룹 승격의 불변식을 지킨다. 서비스 생성과 해제는 App이 맡는다. |
| Controller | UI는 입력 값/오류/명령을 전달하고 VM/EditorSession이 작업을 조정한다. |
| Low Coupling | View의 구체 컨트롤과 Core의 변환 제약을 직접 연결하지 않았다. |
| High Cohesion | 반복 창의 화면 연결, 로딩 상태, 진단 보고서 책임을 각각 모았다. |
| Polymorphism | 기존 IImageLoader/IImageDisplay와 렌더러 계약을 유지했다. 새 가상 구현을 위한 계층은 불필요하다. |
| Pure Fabrication | 기존 진단 실행기와 변환 선택 캐시가 기술 책임을 담당한다. 도메인 모델에 UI 수명을 넣지 않았다. |
| Indirection | 바인딩/작은 이벤트 어댑터와 App 조립으로 UI·구현 서비스 사이를 연결한다. |
| Protected Variations | WPF 검증 오류·창 API·DX12는 화면 계층에, 도형·계층 규칙은 순수 Core/Geometry에 둔다. |

설계 원칙 예외와 새 패키지는 없다. 별도PC·현장 영상·대량 그룹 편집60FPS는 이번 기능 회귀로 검증한 것이 아니다. 저장된 기존 문서/과거 이력을 자동 보정하지 않으며, 포함 판정은 게시된 도형 기준이다. 오목 도형에서 연속 이동 경로 전체를 충돌 검사하는 기능은 추가하지 않았다.
