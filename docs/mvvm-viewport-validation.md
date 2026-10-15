# 기존 부모 재사용·MVVM Viewport 검증

2026-10-14. [ADR-046](decisions/0046-parent-reuse-and-mvvm-viewport.md). 로컬 수정만 수행하며 기존 미커밋 입력·매뉴얼·ROI 변경을 보존한다.

## 확인한 문제와 변경

### 2026-10-15 MainWindow 코드 비하인드 재검토

기존 ROI/영상/렌더 동작과 자원 해제 순서를 유지한다. MainWindow에 섞여 있던 서비스 생성/해제는 App, 실행 진단 선택은 DiagnosticStartup, 초기화 명령의 오류 경계와 성능 표시 문구는 VM으로 옮겼다. View는 UI 연결만 담당하며 남긴 코드별 근거는 [ADR-046](decisions/0046-parent-reuse-and-mvvm-viewport.md#view에-남기는-코드와-이유)에 적었다. StartupUri 대신 App이 생성 → 주입/Bind → 진단 또는 일반 초기화 연결 → Show를 수행한다. 설정창·UI 타이머·포커스 판별을 VM으로 옮기는 중계 계층은 추가하지 않았다.

`WpfTextRasterizer` 두 partial 파일은 `TextRasterizer`로 변경했다. WPF/STA 구현과 ITextRasterizer/ITextBatchRasterizer 계약, 독립/atlas 글자 픽셀과 버퍼 소유권은 유지한다. 외부 프로젝트가 구체 타입을 직접 사용하면 새 이름으로 소스 수정이 필요하다.

- PASS: 앱 297개(새 초기화 성공/오류·재시도/성능 상태 4개 포함), Canvas 235개, 실패/skip0. `artifacts/main-window-mvvm-{app,canvas}-tests.log`. VM 검증은 Window 없이 대역 서비스로 수행하며 GPU 미측정을 0ms로 표시하지 않는지와 문서 불변을 확인했다.
- PASS: 앱 및 솔루션 Release 빌드0경고/0오류. `artifacts/main-window-mvvm-build.log`, `artifacts/main-window-mvvm-solution-build.log`. 솔루션 빌드에 샘플/도구의 타입 참조도 포함한다.
- PASS: RTX3060 Laptop GPU/debug 및 강제 WARP 전체 앱 smoke, 두 프로세스 모두 종료0. 부모/독립/자식 버튼·실제 입력·Undo/Redo·설정창 재열기·교차선·표시 글자·도형·검색범위·필터·8K 라이브·재시작/종료 확인. `artifacts/main-window-mvvm-20261015/{gpu,warp}/artifacts/smoke.log`. WARP 표시와 선택형 필터 CUDA는 독립적이다.
- PASS: 변경 C# 공백 적용·스타일/import 검사 및 `git diff --check`. `artifacts/main-window-mvvm-{whitespace,style}.log`. 매뉴얼11개/로컬 참조298개·코드 발췌55개 일치.4장의 VM 조립 설명도 App 조립/VM 조정으로 수정했다.
- NOT_RUN: 별도 PC, 현장 영상, 대량 편집60FPS 재측정, 모든 진단 플래그의 개별 실행. 진단 선택 분기는 기존 순서/인자를 보존했다. 이번 테스트 범위는 앱/Canvas이며 다른 프로젝트의 이전 전체 테스트 결과와 구별한다. 커밋·푸시 없음.

| 원칙 | 이번 변경 검토 |
|---|---|
| SRP | View의 UI 연결, VM의 상태/명령, App의 조립/종료, Diagnostics의 실행 선택 분리 |
| OCP | 기존 영상·검사·텍스트 계약을 유지하며 새 구현 분기를 VM에 추가하지 않음 |
| LSP | 이미지 소유권·실패/재시도 및 글자 atlas 픽셀/뷰포트 수명 회귀 통과 |
| ISP | VM에 Window/Dispatcher/타이머/CUDA 종료용 계약을 추가하지 않음 |
| DIP | 구현 서비스는 App에서 주입. VM은 컨트롤·GPU 구현체를 생성하지 않음 |
| Information Expert | UI 포커스/스레드는 View, 성능 표시 상태는 VM, 서비스 수명은 생성한 App이 관리 |
| Creator | App이 검사/필터 구현과 VM을 조립하며 렌더러의 HWND/자원 소유자는 그대로 유지 |
| Controller | View 이벤트는 VM 명령/메서드에 전달. ROI 업무 규칙은 기존 세션에 유지 |
| Low Coupling | MainWindow에서 구체 검사·로더·CUDA 생성 의존성 제거 |
| High Cohesion | 설정창 표시와 UI 링크만 View에, 구현체 생성/해제는 같은 App 조립 지점에 배치 |
| Polymorphism | 기존 표시/원본/검사/텍스트 인터페이스 사용. 단일 호출용 추상 계층은 추가하지 않음 |
| Pure Fabrication | 기존 AsyncCommand 오류 경계를 재사용하며 새 서비스 로케이터나 메시지 버스 없음 |
| Indirection | App의 주입과 기존 Scene/Input/Layout 바인딩으로 역할 연결 |
| Protected Variations | Dispatcher/STA와 native 자원 해제 순서 보존. 텍스트 타입 Rename의 외부 소스 영향은 명시 |

원칙 예외 없음. 새 I/O/외부 전송·권한 경계나 hot path 자료 복사는 추가하지 않았다.

### 2026-10-15 부모 버튼 복구·MVVM 폴더 구조

독립 ROI 버튼 수정 때 사라진 `부모 추가/부모 그리기`를 별도 명령으로 복구했다. `EditorSession.AddParent`가 자식 없는 그룹 하나를 생성하고, 생성 즉시 부모 스타일을 사용한다. 그리기 미리보기도 부모 색상을 사용하며 폴리곤·일반 도형·Undo/Redo를 유지한다. 기존 초록색 독립 ROI와 선택 부모의 자식 명령도 유지한다. DrawAsGroup과 DrawParentId는 동시에 활성화되지 않는다.

앱의 Presentation 폴더를 Models/Views/ViewModels와 Commands/Services/Infrastructure로 나눴다. MainWindow XAML/코드 비하인드는 Views, 바인딩 상태/명령은 ViewModels, 검사 판정/조건/선택 옵션/목록은 Models에 둔다. Models에 WPF·View/ViewModel 참조가 없고 ViewModels에 구체 Viewport·View·Controls 참조가 없음을 검색으로 확인했다. 서비스 조립과 UI 수명은 기존 View 책임으로 유지한다. [폴더별 안내](code-guide.md).

- PASS: 부모 추가·그리기4도형×2명령, 이미지 없음 비활성, 한 번의 Undo/Redo·빈 부모 색상·자식 추가, 취소 후 모드 전환 회귀. 기존 그룹/그리기 포함24개로 기능을 확인한 뒤 폴더를 이동했다. 최초 전환 테스트는 드래그 중 비활성인 버튼을 실행하려고 해 실패했다. 기존 입력 계약을 유지하고 취소 후 전환을 검증하도록 수정했다.
- PASS: 솔루션 Release0경고/0오류(`artifacts/mvvm-folders-build.log`), 최종 앱 Release0경고/0오류(`artifacts/mvvm-folders-final-build.log`). 폴더 이동 도중 드러난 필터 라이브러리와 앱의 명령/알림 타입 이름 충돌은 필요한 필터 VM만 명시적으로 가져와 해소했다.
- PASS: 전체1,256개(Core316·앱293·Canvas235·Algorithm249·Compute52·Inspection111), skip0. `artifacts/mvvm-folders-tests.log`.
- PASS: GPU RTX3060 Laptop/debug 및 강제 WARP에서 앱 시작 경로·복구한 부모 버튼·빈 부모/그리기 중/확정 후 부모 색상 픽셀·자식 연결·독립 초록색 ROI·사용자 색상·4단계·이력. `artifacts/mvvm-folders-20261015/{gpu,warp}/artifacts/smoke.log`. `explicit-hierarchy-app.png`에서 세 종류의 버튼이 함께 보임을 확인했다.
- PASS: 최종 GPU 전체 앱 smoke 종료0. 설정창 바인딩/재열기·교차선·도형·검색 범위·마스크·필터·8K live·재시작/종료. `artifacts/mvvm-folders-20261015/gpu-complete/artifacts/smoke.log`.
- PASS: 이동된 경로와 기존 ROI 변경으로 어긋난 매뉴얼 코드 발췌를 갱신했다. HTML11개/참조298개·전체55개 실제 소스 발췌 일치. 아래 이전 후속에서 기록한 발췌 불일치는 해소했다. 브라우저 배치/학습 효과는 이번 검증 범위가 아니다.
- PASS: 변경 소스 import·공백 포맷, 명명/중괄호/namespace 스타일 검사 및 diff 검사. `artifacts/mvvm-folder-{imports,whitespace,style}.log`.
- NOT_RUN: 다른 PC·현장 영상·대량 입력60FPS 재측정. 커밋·푸시 없음.

| 원칙 | 이번 후속 검토 |
|---|---|
| SRP | 화면/바인딩/데이터/실행 작업/명령을 역할별 폴더와 namespace로 분리 |
| OCP | 기존 ROI 명령을 유지하고 부모 생성 명령을 별도로 연결 |
| LSP | 이미지/표시/검사 서비스의 취소·소유권 계약 변경 없음, 전체 회귀 유지 |
| ISP | 새 범용 인터페이스나 중계 VM 없음 |
| DIP | VM의 View/구체 Viewport 의존 없음, 기존 주입 계약 유지 |
| Information Expert | 그룹 생성 불변식은 EditorSession, 도형/역할 색상은 Core/Rendering |
| Creator | 세션이 한 ROI와 한 이력을 생성하며 UI에서 빈 자식을 추가하지 않음 |
| Controller | 부모 버튼도 VM 명령을 거쳐 세션에 위임 |
| Low Coupling | Models에 WPF/Views/ViewModels 참조 없음, XAML 시작 경로와 namespace를 함께 이동 |
| High Cohesion | 모델 옵션은 Models, 바인딩 입력인 RoiStyles는 ViewModels |
| Polymorphism | 기존 영상/표시/검사 서비스와 편집 제스처 계약 유지 |
| Pure Fabrication | 이미지 교체·라이브·편집 준비를 Services, 명령을 Commands에 배치 |
| Indirection | 기존 바인딩/주입을 유지하고 새 메시지 버스·서비스 로케이터를 추가하지 않음 |
| Protected Variations | 공통 Canvas/Core, native 캡처·GPU 수명·파일 계약과 프로젝트 경계 유지 |

원칙 예외 없음. 부모 미리보기 역할은 기존 overlay 생성 시 지정하며 일반 포인터 입력마다 추가 overlay 복제를 하지 않는다.

### 2026-10-15 초록색 기본 ROI·이름 후속

- 기본 버튼은 이미 `ParentId = null`, `IsGroup = false`인 ROI를 만들었다. 문제는 부모 없는 리프를 Default 색상으로 분류한 표시 경로였다. 독립/연결된 리프 모두 Child 스타일로 통일하고 그리기 미리보기·설정 설명·버튼의 잘못된 ‘부모’ 표시도 맞췄다. 그룹·편입·4단계 규칙은 유지한다.
- `RoiStyleDraft`는 `RoiStyles`, 메인 창에 연결하던 `EditorViewModel`은 `MainWindowViewModel`로 직접 변경했다. 세 partial 파일과 호출부/문서 경로도 갱신했다. `MainWindow.xaml.cs`의 `DataContext = _model`이 XAML 명령/장면 바인딩을 연결한다.
- PASS: 독립 ROI의 Child 역할 회귀가 수정 전 실패한 뒤 통과. 앱284개(`artifacts/roi-leaf-app-tests.log`), Canvas235개(`artifacts/roi-leaf-canvas-tests.log`). 기존 Default 기대값 중 실제 리프에 해당하는 항목만 갱신했으며 축소 요약은 Default 유지, 요약/리프 혼합 캐시 회귀 추가.
- PASS: Release 앱 빌드0경고/0오류(`artifacts/roi-leaf-build.log`). 실제 GPU/WARP 모두 종료0, 최초 추가의 선택/비선택 색상·기본 드래그·그리는 중 미리보기·설정된 색상 픽셀 확인. `artifacts/roi-leaf-20261015/{gpu-final,warp-final}/artifacts/`의 로그와 `leaf-*.png`.4단계 색상·편입·깊이 제한·Undo/Redo도 통과.
- PASS: GPU 전체 앱 smoke에서 설정창 바인딩/이름·재열기·입력/이력·도형·검사 범위·필터·8K live·재시작 확인. `artifacts/roi-leaf-20261015/gpu-settings/artifacts/smoke.log`.
- PASS: 매뉴얼 HTML11개/로컬 참조298개 및 이름을 바꾼 MainWindowViewModel.Editing 전체 코드 발췌 일치. 전체 발췌 동기화 검사에는 이전 작업에서 바뀐 InteractionController의 매뉴얼 원문 불일치가 남아 있다. 이번 이름 변경 검증과 구별하며 매뉴얼 전체 최신화로 주장하지 않는다.
- NOT_RUN: 별도 PC/현장 영상/대량 편집60FPS. 커밋·푸시 없음.
- PASS: 변경 파일 C# 공백·명명/중괄호/namespace 스타일 및 `git diff --check`. `artifacts/roi-leaf-whitespace.log`, `artifacts/roi-leaf-style.log`.

이번 후속도 아래 SOLID5/GRASP9 검토를 적용했다. SRP/Information Expert/High Cohesion은 표시 역할을 Rendering에, 명령을 VM에 유지했다. OCP/LSP/ISP/DIP/Polymorphism/Protected Variations의 서비스·소스 소유권 계약은 바꾸지 않았다. Creator/Controller는 기존 세션 생성·명령 경로를 사용한다. Low Coupling/Pure Fabrication/Indirection은 중계 타입이나 의존성을 추가하지 않고 기존 역할 캐시를 사용한다. 캐시는 요약 슬롯과 실제 ROI를 보존하며 Undo/Redo 시 형상을 다시 열거하지 않는 회귀도 유지했다. 원칙 예외 없음.

- 부모 하나와 독립 ROI를 선택해 그룹화하면 외곽 ROI가 추가되던 동작을 재현했다. 동기/비동기 × 독립/중첩 부모 4개 테스트가 수정 전에 실패했고 수정 후 통과했다. 기존 부모에 붙일 때 개수·부모 형상·ID·번호를 보존하며 한 번의 Undo/Redo로 복원한다.
- 기존 상위 그룹 테스트는 부모 두 개를 묶는 입력으로 바꿨다. 상위 그룹 생성, 자손 보존, 그룹 해제, 클립보드, 4단계 제한 검증은 유지한다.
- VM의 직접 Submit, Dx12Viewport 참조, 파일 대화상자 생성, DispatcherTimer, 검사 구현체 생성을 제거했다. Scene/Input/Layout 바인딩, 작은 이미지 표시 서비스, View의 파일 선택, 조립 지점의 서비스 생성으로 바꿨다. 실제 입력 캡처·프레임 소유권·DX12 fence/표시 worker는 유지한다.
- 반복 ROI 키 처리/이미지 로드와 검사 로그 저장 책임도 정리했다. 창·컨트롤을 만들지 않는 VM 테스트로 그룹화/이력·크기/DPI·소스 소유권·키 입력·구독 해제를 검증한다.

## 검증 상태

- PASS: Core 316개. `artifacts/group-fix-tests.log`.
- PASS: 앱 281개. `artifacts/mvvm-tests.log`. 새 MVVM 테스트 4개 포함.
- PASS: 새 Viewport 바인딩 4개. 최초 테스트는 WPF Dispatcher 큐를 실행하지 않아 초기 바인딩 전에 검사해 실패했다. 실제 바인딩 큐를 처리하도록 고친 후 DataContext 교체/장면/입력/레이아웃 검증이 통과했다.
- PASS: 전체 솔루션 Release 회귀 1,242개(Core316·앱281·Canvas233·Algorithm249·Compute52·Inspection111). `artifacts/mvvm-full-tests.log`. 이후 진단 시나리오 명칭/입력 보완 후 앱 Release 빌드0경고/0오류(`artifacts/mvvm-app-build.log`).
- PASS: 변경 C# 공백 포맷 및 명명/중괄호/namespace/import 스타일 검사. `artifacts/mvvm-changed-csharp.txt`, `artifacts/mvvm-style.log`.
- PASS: RTX3060 Laptop GPU, DX12 debug, 실제 앱 전체 smoke 종료0. HWND 입력·그룹화·Undo/Redo·설정창 바인딩·가이드·resize·재시작·도형/검색범위·필터·8K live를 확인했다. `artifacts/mvvm-20261014/gpu-complete/artifacts/smoke.log`.
- 최초 전체 앱 검증은 옛 `부모 ROI 선 두께` 이름과 부모 밖으로 걸치는 폴리곤 입력 때문에 실패했다. 현재 이름을 사용하고 자식 교차 폴리곤을 부모 안에 배치했으며 내부 포함 assertion을 추가했다. 교차 닫기·Undo/Redo 검사를 유지한 후 GPU 전체 통과를 확인했다.
- PASS: 강제 WARP(Microsoft Basic Render Driver) 전체 앱 smoke 종료0. `artifacts/mvvm-20261014/warp-complete/artifacts/smoke.log`. GPU 없는 별도 PC 검증과 구별하며 검사 필터의 선택형 CUDA는 표시 WARP와 독립적이다.
- PASS: GPU/WARP 각각 부모 재사용(ROI 수/부모 형상 유지·Undo)·5단계 거부·4단계 기본/변경 색상 픽셀·검색 타입 즉시 적용 종료0. `artifacts/mvvm-20261014/{gpu-hierarchy,warp-hierarchy}/artifacts/smoke.log`, `hierarchy-default.png`, `hierarchy-custom.png`.
- PASS: `git diff --check`. 커밋·푸시하지 않았다.
- 환경: 사용자 .NET/Visual Studio 업데이트 중 SDK 실행 파일이 잠시 없어졌다. 설치 복구 후 SDK 11.0.100-rc.2.26504.105, .NET/WindowsDesktop 10.0.13 런타임으로 재개했다. 프로젝트 대상 net10.0/net10.0-windows는 변경하지 않았다.
- NOT_RUN: 별도 저사양 PC, 8K/24K 현장 영상, 대량 편집 중 엄격한 60FPS 재측정. 기능 테스트로 성능 목표 달성을 주장하지 않는다.

## SOLID 5개·GRASP 9개 검토

| 원칙 | 검토 결과 |
|---|---|
| SRP | VM은 표시 상태/명령, View는 화면 연결, Core는 계층, 서비스는 이미지/로그 I/O를 담당 |
| OCP | 기존 직접 호스트 API와 렌더 worker를 보존하면서 바인딩 경로 추가. 알고리즘 변경 없음 |
| LSP | 기존 IViewportSurface의 소스 이전/재시작 계약 유지, 서비스 대역과 이미지 실패/취소 회귀로 확인 |
| ISP | VM용 IImageDisplay는 소스 이전/재시작만 제공. Submit/Visual 크기는 제외 |
| DIP | VM에서 구체 컨트롤/대화상자/검사 구현 생성 제거. 앱 조립 지점이 의존성 주입 |
| Information Expert | 포함/계층은 기존 기하/Core, 실제 DIP/DPI는 View, 사용 가능한 명령은 VM |
| Creator | 그룹 편집기가 새 그룹 필요 여부 결정, HWND/렌더 자원은 기존 소유자가 생성 |
| Controller | XAML 명령/입력 처리기가 VM/세션에 편집을 위임. View의 직접 반복 ROI 편집 제거 |
| Low Coupling | ViewModel→Dx12Viewport 참조와 레이아웃 이벤트 구독 제거. DTO·서비스 계약으로 연결 |
| High Cohesion | 렌더 제출은 컨트롤, 파일 창은 View, 로그 직렬화는 진단 데이터 서비스에 유지 |
| Polymorphism | 기존 이미지/검사 서비스 변형을 주입. UI를 위한 가짜 상속 계층 없음 |
| Pure Fabrication | ViewportLayout은 화면 치수 데이터만 보유. 범용 메시지 버스·서비스 로케이터 없음 |
| Indirection | WPF 바인딩과 기존 이미지 서비스가 UI·렌더 자원·상태의 의존 방향을 분리 |
| Protected Variations | 네이티브 캡처/fence/버퍼 소유권은 변경하지 않고 바인딩 연결만 추가 |

원칙 예외 없음. 모든 코드 비하인드 제거를 MVVM 준수 기준으로 삼지 않는다. View에 남긴 항목과 이유는 ADR에 명시했다.
