# 구현 품질 검토

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
