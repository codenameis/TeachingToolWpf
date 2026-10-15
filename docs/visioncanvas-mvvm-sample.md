# VisionCanvas MVVM 샘플 검증

요구 범위와 명령은 [계획](../tasks/visioncanvas-mvvm-sample.md), 사용자 안내는 [샘플 README](../samples/VisionCanvas.MvvmSample/README.md), 결정은 [ADR-052](decisions/0052-minimal-mvvm-canvas-sample.md)를 따른다.

## 재사용성 평가

기존 Canvas는 Scene/PointerInput/LayoutState/KeyAction/IsInputEnabled 바인딩과 EditorSession/InteractionController를 이미 제공한다. 이 경계는 그대로 사용했다. 앱 내부에만 있던 이미지 교체·이력 준비 서비스는 라이브러리로 이동하여 복제를 없앴다. 새 앱은 파일 선택 계약 하나만 추가하고 두 개의 프로젝트를 직접 참조한다. 창의 소스 코드·검사·진단을 복사하거나 링크하지 않는다.

Scene 조합, 키 명령 연결, 오류의 UI 스레드 전달은 여전히 호스트에 필요하다. 이 정도는 화면 정책과 WPF 수명의 책임이며 범용 컨트롤러·이벤트 버스로 감추지 않았다. 기존 전이 필터 의존성은 남아 있으므로 배포 크기까지 최소화했다고 주장하지 않는다.

## 검증 증거

- PASS: UI 없는 VM8개 + 실제 WPF 창2개, 총10개. 빈 화면 버튼 상태,8K 로드, 그리기/그룹/복사/삭제/Undo/Redo, 부모·자식과 그룹 해제, 다각형 취소/완료, 사라진 부모의 그리기 도구 해제, 파일 선택 취소·실패, 종료 후 늦은 소스 해제.
- PASS: 실제 WPF XAML 창/명령 바인딩과 HWND 마우스·Delete 입력.8K 정적 타일·24,576×8,192 Gray8 PNG 파일, ROI 픽셀, 이미지 교체 Undo/Redo, 창 크기 변경·종료.
- PASS: RTX3060 Laptop GPU와 강제 WARP. `artifacts/mvvm-sample/Automatic/result.txt`, `Software/result.txt`와 같은 폴더의 `8k.png`, `roi.png`, `24k.png`에 증거를 남긴다. 초기 ROI 픽셀 검사에서 실제 민트색의 파란 채널을 고려하지 않아 실패했고, 캡처와 표준 팔레트를 확인하여 기대 색상을 수정했다. 제품 코드를 바꾸거나 실패를 숨기지 않았다.
- 장치: `NVIDIA GeForce RTX 3060 Laptop GPU`, VRAM5.9GiB, Budget5.1GiB. WARP는 `Microsoft Basic Render Driver`, 타일 캐시256MiB.
- PASS: ImageCoordinator 이동 직후 기존 이미지 이력7개 회귀.
- PASS: 기존 Canvas235개 + 기존 앱280개 회귀. 최종 새 샘플은 Release 빌드에서 `TreatWarningsAsErrors=true`로 검사한다.
- PASS: 종료 보호 조건을 잠시 제거하면 종료 후 명령 비활성화 테스트가 실패한다. 원복 후 최종 테스트를 통과했다. 초기 샌드박스의 .NET 레지스트리 접근 제한은 승인된 테스트 실행 권한으로 재실행했다.
- NOT_RUN: 실제 파일 대화상자의 수동 클릭, 별도 저사양PC, 실시간 카메라, 대량ROI 연속입력 프레임 간격. 이번 시험으로 기존 엄격한60FPS FAIL을 대체하지 않는다.

## 원칙별 검토

2026-10-15 초보자용 주석 후속: 샘플의 시작·바인딩·명령·파일 선택·종료와 공통 ImageCoordinator의 이미지 소유권에 한국어 설명을 추가했다. C#7개는 Roslyn으로 주석/공백을 제외한 코드 토큰이 전후 동일함을 확인했고, XAML2개·csproj1개는 주석을 제외한 XML 내용이 동일했다. 결과는 `artifacts/mvvm-sample/comment-equivalence.json`에 있다. Release 경고를 오류로 처리하는 빌드와 기존 VM8개 테스트를 통과했다. 실행 코드·책임 경계·의존성 변경이 없어 아래 원칙 검토는 그대로 적용되며, 새 테스트나 GPU/FPS 재측정은 추가하지 않았다. 설명이 실제 초보자에게 충분한지는 독자 피드백으로 확인해야 한다.

| 원칙 | 적용과 확인 |
|---|---|
| SRP | App은 조립, VM은 화면 상태/명령, View는 UI 전달, Coordinator는 이미지 이력 수명, Core는 편집 규칙 |
| OCP | IImageLoader/IImageDisplay를 교체하며 Canvas에 디코더 분기 추가 없음 |
| LSP | 가짜 loader/display와 실제 loader/viewport가 같은 소유권·실패·취소 계약으로 동작 |
| ISP | 파일 선택은 Pick 하나. 기존 ImageDisplay와 Layout/Input 계약을 사용하고 거대한 호스트 인터페이스 추가 없음 |
| DIP | VM은 파일 선택/영상 계약에 의존. 구체 Window/ImageLoader/Viewport 연결은 App |
| Information Expert | 포함·그룹·계층·기하·이력은 기존 Core 전문가에 위임. VM이 좌표나 그룹 규칙을 복제하지 않음 |
| Creator | App이 서비스 조립, EditorSession이 ROI 생성, Viewport가 네이티브 수명 소유 |
| Controller | 버튼/키는 같은 VM 명령 → EditorSession/InteractionController로 전달 |
| Low Coupling | 새 샘플에서 기존 앱·알고리즘·검사 프로젝트 참조 없음 |
| High Cohesion | UI/명령/파일 선택/이미지 수명/편집을 책임별로 유지 |
| Polymorphism | 실제 변형 지점인 파일 선택·이미지 로더·표시 계약에만 적용 |
| Pure Fabrication | 이미지 이력 조율과 ICommand 경계만 별도 기술 책임으로 유지 |
| Indirection | App과 파일 선택 어댑터가 외부 API를 연결. 새 중계 계층·서비스 로케이터 없음 |
| Protected Variations | GPU/WARP·파일 디코더 차이를 안정된 기존 계약으로 격리 |
| Clean Code / DRY | 영어 식별자, 명시적 오류·취소, 도메인 모델과 이미지 이력 중복 없음. 버튼/단축키 실행 경로 통합 |
| YAGNI | 진단 모드·라이브/검사·설정 프레임워크·새 DI 패키지·범용 VM 기반 클래스 없음 |

MVVM, Command, 불변 문서/Memento, Composite, Adapter와 알림 패턴은 각각 실제 책임에 맞는 기존 구현을 사용한다. 모든 패턴을 추가하는 것을 품질 지표로 삼지 않았고, 승인된 원칙 예외도 없다. 작업 권한은 로컬 수정·검증이며 커밋·푸시하지 않는다.

## 2카메라 모델 후속 검증

- PASS: 샘플17개. 기존 VM8개, 문서 로딩3개, 두 카메라 모델/소스2개, 실제 단일 창·두 카메라 창 GPU/WARP4개.
- PASS: 최종 Release 회귀는 Core385개 + Canvas241개 + 기존 앱/이미징281개 + 샘플17개 = 총924개, 실패0. 각 명령에 `-p:TreatWarningsAsErrors=true`를 적용했다. 변경 파일의 공백 포맷 검증도 통과했다.
- PASS: 각 카메라8192×8192, 각 그룹2개+리프5개, 총14개 ROI/그룹·10개 알고리즘 설정 연결. 카메라별 모델 재로딩/삭제/Undo/Redo, 목록-Canvas 선택과 설정 표시, 다른 카메라 문서 불변.
- PASS: 잘못된 부모·순환·이미지 경계·그룹 검색 범위·필터 값·실제 이미지 크기 불일치 거부. 준비 이후 문서 변경·취소 거부. 초기 로딩 이력 없음과 재로딩 한 건, 다음 ROI 생성 번호와 Undo/Redo 화면 배율 보존.
- PASS: 실제 네이티브 화면2개와 XAML 바인딩. 카메라2 HWND Delete 후 Undo, 바인딩/입력/타일 오류 없음, 두 HWND 종료.
- GPU: NVIDIA GeForce RTX3060 Laptop GPU, VRAM5.9GiB, Budget5.1GiB. WARP: Microsoft Basic Render Driver, 화면당 타일 캐시256MiB.
- 증거: `artifacts/mvvm-sample/two-cameras/Automatic/`와 `Software/`의 `result.txt`, `camera-1.png`, `camera-2.png`. 렌더 캡처에서 두 색상, 실제 ROI 도형·그룹 계층 이름을 확인했다.

내장 모델만 로딩하며 알고리즘은 이름/설정 데이터다. 실제 검사·외부 모델 파일·실시간 카메라·60FPS나 현장 정확도를 검증한 결과가 아니다. 두 카메라의 성공/실패는 독립적이며 전체 모델 트랜잭션은 제공하지 않는다.

### 후속 설계 원칙 검토

| 원칙 | 이번 변경에서 확인한 책임 |
|---|---|
| SRP | 예제 데이터, 두 카메라 조정, 각 편집 화면, 문서 검증, 이미지 소유권을 구분 |
| OCP | 합성 카메라는 IImageLoader 어댑터로 추가. Canvas에 카메라/알고리즘별 분기 없음 |
| LSP | 예제/파일 로더의 취소·타일 소유권 유지. 실제 GPU/WARP와 테스트 표시 구현 사용 |
| ISP | 범용 모델 저장소/실행 인터페이스 추가 없음. 기존 작은 영상 계약 사용 |
| DIP | App이 구체 로더·창을 연결. VM은 구체 Viewport/Window/Dispatcher를 참조하지 않음 |
| Information Expert | ROI 계층/경계/순서/이력 검증은 Core, 알고리즘 연결은 샘플 Model |
| Creator | App이 카메라마다 독립 문서·입력·VM 생성. 모델 팩토리는 값만 생성 |
| Controller | 모델 로딩 버튼은 VM 명령, 목록/키 입력도 기존 Core 편집 경로로 전달 |
| Low Coupling | Canvas에 알고리즘/검사 프로젝트 참조 없음. ROI ID만으로 설정 연결 |
| High Cohesion | 같은 편집 UI는 EditorView 한 곳. 두 카메라 VM은 자신 문서만 관리 |
| Polymorphism | 실제 이미지 변형만 IImageLoader/ITileSource 계약으로 처리 |
| Pure Fabrication | 이미지 조율과 명령 객체는 기존 기술 책임 유지. 범용 관리자 추가 없음 |
| Indirection | App과 DemoImageLoader가 구체 이미지 소스 차이를 격리 |
| Protected Variations | 원본 크기 불일치/취소/잘못된 계층을 게시 전 거부. 기존 문서 보존 |
| DRY / Clean Code | EditorView 공유, 기존 불변 ROI·기하·이력 사용, 한국어 역할/소유권 주석 |
| YAGNI | 알고리즘 실행 엔진·카메라 SDK·직렬화 형식·저장소·DI 컨테이너를 추가하지 않음 |

첫 설계 검토에서 문서 재로딩의 화면 맞춤 값이 이력에 함께 남아야 함을 확인해 준비 결과에 명시했다. 해당 값과 Undo/Redo를 테스트한다. 기존 이력 예산 검사를 재사용하여 원본 이전 전에 확인한다. 설계 원칙 예외는 없다.
