# 구현 품질 검토

## 수백만 PCB ROI 후속 검토

2026-10-02. [ADR-006](decisions/0006-large-roi-interaction.md)에 따라 원칙을 유지하면서 순회·할당·색인 재구축을 줄였다.

| 원칙 | 이번 변경의 검토 근거 |
|---|---|
| SRP | `RoiSnapshot`은 파생 색인, `RoiVisibility`는 화면 후보, `RoiRows`는 UI 목록, `ValueTextBox`는 값 표시/편집 수명만 담당 |
| OCP | 라벨 Strategy 계약을 바꾸지 않았고 속성 컨트롤은 기존 WPF TextBox 계약과 MVVM 값 바인딩으로 확장 |
| LSP | BVH를 전수 회전 포함과 비교하고 이전 스냅샷 조회도 검증. Value 패턴은 실제 값과 읽기 전용/disabled 계약을 유지 |
| ISP | Core 색인에 UI/GPU 메서드를 추가하지 않음. 목록 페이지 선택 의도만 작은 이벤트로 전달 |
| DIP | Core 외부 의존성 없음. `IViewportSurface`·`ITextRasterizer`·필터 계약 유지, 새 외부 패키지 없음 |
| Information Expert | bounds·공간 탐색은 기하 색인, 부모 영향 집합은 계층, 목록 인덱스/페이지는 UI 표현 책임 |
| Creator | 문서가 변경 ID와 불변 색인 스냅샷 생성, 목록이 자신의 페이지 복사본 생성 |
| Controller | 포인터·목록·키보드 모두 `EditorSession` 선택/편집을 호출. 속성 Apply/Undo 경로 유지 |
| Low Coupling | UI 전용 가변 행 캐시는 렌더로 전달하지 않음. 렌더는 불변 문서·색인만 소비 |
| High Cohesion | 전체 계층 재구축을 선택 이벤트에서 분리. 표시 요약은 도메인 형상이나 편집 규칙을 변경하지 않음 |
| Polymorphism | 기존 제스처·소스·라벨 계약 유지. 성능 개선 명목으로 사용처 없는 범용 플러그인 추가 없음 |
| Pure Fabrication | 파생 색인·목록 캐시·표시 후보 캐시를 별도 책임으로 분리 |
| Indirection | Win32/WPF 입력 경계가 선택 의도를 전달하고 색인은 GPU와 독립적으로 검증 |
| Protected Variations | WPF의 unresolved 항목 순회·TSF 지연을 UI 어댑터 안에서 해결. DX12/필터/카메라 계약 유지 |

검토에서 Lazy 팩터리 체인의 메모리 보존, 페이지 밖 선택 잔존, pan에 따른 페이지 되돌림을 수정했다. 큰 이동 변이를 기존 테스트가 놓쳐 회귀 검증을 보강하고 반전 조건이 실패함을 확인했다. 캐시 평가 전후의 문서 동등성·해시도 검증한다. 성능 원칙 예외, 전역 강제 GC, 비활성화된 Undo, 무시하는 예외는 없다. 세부 실측과 미검증 조건은 [성능 보고서](roi-scalability.md)를 따른다.

2026-10-02. 샘플 소스의 책임, 소유권, 오류/취소, 이력, 실제 GPU 경로를 검토했다. 원칙의 성능 예외를 적용하거나 승인받은 기록은 없다. 아래는 현재 구현의 근거이며 추후 변경의 자동 준수를 보장하는 선언은 아니다.

| SOLID | 구현 근거 |
|---|---|
| SRP | 기하 `RotatedRect`, 계층 `RoiHierarchy`, 이력 `EditHistory`, 제어 `EditorSession`, 이미지 준비 `ImageCoordinator`, GPU 이미지 수명 `GpuImageResources` 분리 |
| OCP | `IFrameSource`, `ITileSource`, `IFramePreparer`, `ITextRasterizer`, `IGesture`의 실제 변형을 구현으로 연결 |
| LSP | 프레임 소유권, 타일 형식/경계/취소, scalar/AVX2 동일 결과, 이미지 로더 실패 시 상태 보존을 테스트 |
| ISP | 소스·뷰포트·프레임 게시·텍스트의 소비자별 계약. 카메라 구현에 편집/렌더 메서드를 요구하지 않음 |
| DIP | Core에 외부 패키지 참조 없음. UI/파일/GPU 구현을 MainWindow/Dx12Viewport 조립 지점에서 주입 |

| GRASP | 구현 근거 |
|---|---|
| Information Expert | 좌표와 회전 포함은 기하, 부모 선택은 계층, history cursor는 이력이 관리 |
| Creator | EditorSession이 ID/순서/이력을 가진 ROI 생성, GPU 소유자가 texture/fence 생성·해제 |
| Controller | 버튼/키보드/포인터가 EditorSession의 공통 편집 규칙을 사용 |
| Low Coupling | Core 불변 문서/영상 계약을 UI와 렌더가 소비. UI 컨트롤을 GPU 코드에 전달하지 않음 |
| High Cohesion | ScenePainter는 mesh 구성, SpritePipeline은 GPU pipeline, ImageCoordinator는 소스 교체와 이력 조율 |
| Polymorphism | 정적/합성 소스, 포인터 제스처, 프레임 준비와 텍스트 어댑터 |
| Pure Fabrication | 기술 책임인 EditHistory, TileWorker, GpuLabelCache, FrameUploadArena를 분리 |
| Indirection | Win32 입력과 NetVips/WPF 텍스트/DX12 세부 구현을 어댑터 경계로 격리 |
| Protected Variations | 카메라/디코더/표시 API가 ROI 포함·편집 이력 계약을 바꾸지 않음 |

패턴은 MVVM, 변경 명령과 Memento, 제스처 State/Strategy, ROI 트리의 Composite 관계, 좁은 Adapter, 수명 있는 Observer, Composition Root다. 타입 수를 늘리기 위한 범용 엔진/서비스 로케이터는 두지 않았다. `VipsRuntime`의 한 번만 수행하는 네이티브 라이브러리 설정은 영상 인프라에 국한한다.

검토에서 수정한 사항은 타일 큐 슬롯의 정확히 한 번 반환, 늦은 렌더 콜백과 종료 경합, 네이티브 파일 매핑 해제, Gray16 메모리 복사, 다중 선택과 목록 동기화 재진입, 이력 예산 초과 시 Redo 보존, 부모 resize의 초안 일치, 회전 핸들과 태그 겹침이다.

제약: sample 규모의 `EditorSession`/ViewModel은 아직 한 클래스에 편집 진입점을 모은다. 변형 명령이나 검사 기능이 늘어나면 사용 사례별로 나누되 현재 필요 없는 추상화는 추가하지 않는다. 성능 CSV는 진단 실행에서만 매 프레임 기록한다. 메모리의 추정 이력 예산, GPU 논리 texture bytes, OS working set은 서로 다른 값이다.

실제 테스트/장비 결과는 [런타임 검증](runtime-validation.md), 성능 원시 자료는 [성능 기록](performance-baseline.md)에 연결한다. 혼합 DPI/실제 장치 제거/장시간 안정성 검증이 남았으며 해당 항목을 통과로 표시하지 않는다.

## ROI 경계·선택 UI·OpenCV 후속 검토

원칙별로 새 변경을 다시 검토했다. 승인되지 않은 원칙 예외는 없다.

| 원칙 | 새 변경의 근거 |
|---|---|
| SRP | `RoiImageBounds`, `RoiPixelExtractor`, `OpenCvRoiFilter`, `FilterPreviewViewModel`, `GpuFilterPreview`가 기하/추출/연산/세션/GPU 수명을 각각 소유 |
| OCP | UI는 `IRoiFilterPreview`를 소비하므로 필터 처리 구현을 대체할 수 있음. 실제 두 필터 분기는 OpenCV 어댑터에 국한 |
| LSP | 비동기 계약의 취소·비파괴 입력·선택 변경 결과 폐기를 실제 구현과 지연 fake로 검증 |
| ISP | 필터 계약은 취소 가능한 처리 메서드 하나이며 UI/장치/편집 메서드를 요구하지 않음 |
| DIP | Core에 OpenCV 참조 없음. MainWindow에서 로더/프레임 lease 공급 함수를 주입 |
| Information Expert | 이미지 경계와 변형 제한은 Core 기하/변형 계층에서 수행 |
| Creator | 영상 어댑터가 Mat/픽셀 결과를 만들고 GPU 소유자가 texture를 생성·폐기 |
| Controller | UI/포인터의 ROI 편집은 EditorSession, 미리보기 요청은 별도 ViewModel에서 조율 |
| Low Coupling | 결과는 형상+Gray8 데이터이며 OpenCV Mat가 UI/DX12로 넘어가지 않음 |
| High Cohesion | 작은 필터 패널과 처리·추출·수명 컴포넌트 분리. 창 코드에는 조립만 추가 |
| Polymorphism | 실제/테스트 필터 처리기와 정적/라이브 소스가 작은 계약을 따름 |
| Pure Fabrication | 최신 요청 교체와 GPU preview 수명을 도메인 ROI로부터 분리 |
| Indirection | OpenCV/Win32 세부 사항을 각각 어댑터로 격리. 임의 전역 서비스 조회 없음 |
| Protected Variations | 필터 변경이 ROI 포함/Undo와 DX12 기하 계약에 영향을 주지 않음 |

검토에서 같은 이미지 다시 열기의 뷰 Undo, 실패한 열기가 라이브를 먼저 정지하던 순서, 라이브 시작 후 벤치마크 ROI 개수 보존을 수정했다. 0도 초기화는 경계에서 임의 각도로 멈추지 않고 선택/자손을 함께 맞춰 정확한 0도를 보존한다. 이를 위한 자손 균일 축소는 일반 resize와 구별한 명시적 기능 규칙이다.

성능 범위: 원본 24K 전체를 Mat로 복사하지 않고 필요한 LOD 타일만 읽는다. 출력은 ROI별 1024px/전체 약 4Mpx이며 단일 실행/최신 요청 하나로 제한한다. 라이브 필터는 최대 5Hz이고 기본 꺼짐 상태에서는 연산하지 않는다. 미리보기 해상도 제한을 UI/README에 표시했다. 정식 8K 부하와 장시간 안정성은 기존 측정과 새 기능 검증을 구분한다.

## ROI 그리기 후 선택 전환 수정

원인은 한 개를 그린 뒤에도 Draw 상태를 유지해 다음 클릭을 그리기로 해석하던 동작이다. 생성이 성공한 제스처의 완료 시점에만 Select로 전환한다. 취소·빈 드래그는 기존 상태와 이력을 유지한다. 단순 선택은 새 편집 이력을 만들지 않으며 Undo/Redo는 ROI 생성 자체를 되돌린다.

- SOLID: SRP는 입력 상태 전환을 제스처/컨트롤러 안에 유지하고 UI는 안내만 표시한다. OCP·LSP는 기존 `IGesture`의 완료/취소 계약을 유지한다. ISP는 새 인터페이스를 추가하지 않으며 DIP는 Core가 WPF/DX12에 의존하지 않는 경계를 유지한다.
- GRASP: Information Expert·Controller는 생성 성공을 아는 `DrawGesture`와 도구를 소유한 `InteractionController`에 전환을 둔다. Creator는 기존 `EditorSession.Add`를 그대로 사용한다. Low Coupling·High Cohesion은 UI와 편집 규칙의 분리를 유지한다. Polymorphism은 기존 제스처 계약을 사용한다. Pure Fabrication·Indirection·Protected Variations는 이력 서비스와 HWND 입력 어댑터를 변경하지 않아 생성/선택 규칙이 외부 API와 분리된다.

State 패턴의 전환 보정이며 별도 모드/전역 상태/추상화를 추가하지 않았다. 성능상 원칙 예외는 없다. 회귀 검증은 자동 도구 전환, 떨어진 ROI 선택, 선택 시 형상/이력 보존, Undo/Redo, R로 다시 그린 자식의 계층, 취소/빈 드래그를 포함한다.

## 필터 등록·복사·메모리 후속 검토

| 원칙 | 이번 변경의 근거 |
|---|---|
| SRP | ROI 설정 값, 복사 스냅샷, 미리보기 요청 조율, OpenCV 연산과 메모리 진단을 별도 책임으로 유지 |
| OCP | 필터 처리는 기존 `IRoiFilterPreview` 구현에 맡기고 Core에 네이티브 분기를 추가하지 않음 |
| LSP | 지연 fake와 실제 처리기의 취소/입력 보존/ROI별 설정 계약, `ITileSource` 종료 소유권을 검사 |
| ISP | 클립보드에 이미지 소스/렌더 메서드를 요구하지 않으며 기존 작은 처리 계약 유지 |
| DIP | Core의 `FilterSettings`는 값 타입 계약이며 OpenCV/WPF 참조 없음. 앱 조립 지점에서 처리기 주입 |
| Information Expert | ROI가 필터 설정을 보관하고 복사 집합을 가진 클립보드가 배치를 계산. 최종 포함은 기존 계층 책임 사용 |
| Creator | 새 ID·순서·이력은 EditorSession이 생성. 클립보드는 원본 GPU/이미지 자원을 복제하지 않음 |
| Controller | 버튼/키보드가 같은 Copy/Paste/Delete 응용 명령을 사용하고 필터 등록은 EditorSession에 위임 |
| Low Coupling | 파생 픽셀과 등록 설정을 분리하여 이력에 Mat/텍스처/작업 Task가 들어가지 않음 |
| High Cohesion | 필터 선택 동기화는 FilterPreviewViewModel, 복사 값/배치는 RoiClipboard, 진단은 Diagnostics에 배치 |
| Polymorphism | 기존 처리/소스 계약의 실제 구현과 검증용 구현을 사용. 클립보드에는 불필요한 대체 계층을 만들지 않음 |
| Pure Fabrication | 클립보드와 메모리 진단을 도메인 ROI 데이터에서 분리 |
| Indirection | UI 명령과 EditorSession/처리 어댑터 사이의 기존 경계 유지 |
| Protected Variations | 필터 엔진/렌더러의 수명이 ROI 복제·설정 이력 형식을 침범하지 않음 |

MVVM과 Command/Memento를 재사용하고 클립보드는 값 스냅샷으로 구현했다. 별도 글로벌 서비스나 OS 클립보드 프로토콜은 추가하지 않았다. 독립 리뷰에서 발견한 숫자 한 글자마다 쌓이는 Undo와 혼합 선택의 등록 해제 경로를 각각 LostFocus 확정/명시적 해제 명령으로 보완했다.

회귀 시험의 유효성은 등록 ROI만 고르는 조건을 일시 반전했을 때 재선택 테스트가 실패함을 확인한 뒤 원복/재실행했다. 원칙 예외와 검증 완화는 없다. 메모리 측정에서는 지속 누수 원인을 찾지 못했으며, 진단 시점 외에 강제 GC를 도입하지 않았다. 실제 Windows/GPU 검증과 미검증 범위는 [실행 검증](runtime-validation.md), 측정은 [메모리 검증](memory-replacement-validation.md)을 따른다.

## 대량 붙여넣기·미니맵 검토

| 원칙 | 이번 변경의 근거 |
|---|---|
| SRP | `RoiPaste`는 불변 결과 준비, Session은 확정/이력, `FilterAtlas`는 패킹, `GpuFilterPreview`는 GPU 소유권, 미니맵 기하/제스처/표시를 분리 |
| OCP | 기존 제스처·프레임 준비 계약을 재사용하며 Core에 그래픽 API 분기를 추가하지 않음 |
| LSP | 취소/실패 시 문서·Redo 보존, 제스처 취소 복원, 기존 atlas 표시 순서/픽셀 계약 검증 |
| ISP | 미니맵 때문에 소스나 렌더 계약에 불필요한 도메인 편집 메서드를 추가하지 않음 |
| DIP | Core의 미니맵과 붙여넣기는 WPF/DX12 비의존. 앱 조립과 기존 프레임 준비 어댑터 유지 |
| Information Expert | 레이아웃이 좌표/가시 영역, 계층이 정확한 부모 판정, atlas가 배치와 UV를 계산 |
| Creator | Session이 새 ROI 확정과 이력을 소유하며 각 GPU 컴포넌트가 자신이 생성한 texture를 해제 |
| Controller | 버튼/키 명령은 동일 PasteAsync 경로, 미니맵은 입력 제스처를 통해 viewport만 갱신 |
| Low Coupling | 작업 스레드는 불변 값만 받고 UI 컬렉션/이력에 접근하지 않음. GPU는 미니맵 Core 값만 사용 |
| High Cohesion | 준비·패킹·개요 texture·입력 책임을 별도 작은 타입으로 유지. MainWindow에는 기하 계산 없음 |
| Polymorphism | `IGesture`의 완료/취소와 `IFramePreparer`의 기존 형식/수명 계약 사용 |
| Pure Fabrication | 패킹과 GPU 개요 수명을 도메인 ROI에서 분리 |
| Indirection | GPU 표시/Win32 입력이 공통 Core 레이아웃을 소비. 비동기 결과는 Session 확정을 경유 |
| Protected Variations | 필터 atlas와 DX12 리소스 변경이 ROI 데이터/이력 형식을 바꾸지 않음 |

Command/Memento와 제스처 State/Strategy를 유지했다. 성능상 원칙 예외·의존성 추가·ROI 생략은 없다. 독립 모델의 읽기 전용 리뷰에서 비동기 결과 유효성, fence 수명, UV/그리기 순서, 미니맵 입력, 캡처 버전 경합에 대한 확정 결함이 없었다. 캡처는 첫 프레임/타일이 오지 않으면 보류되므로 진단 호출측 timeout을 유지한다.

미니맵 클릭/드래그 구분 조건을 반전한 mutation은 해당 테스트를 실패시켰으며 복원 후 Core 60개가 통과했다. 실제 GPU와 응답 간격·남은 UI 지연은 [검증 기록](clipboard-minimap-validation.md)에 구분했다.
