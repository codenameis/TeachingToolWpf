# 단계별 작업 목록

## 2026-10-02 계층별 ROI 색상과 두께

- [x] S1: 부모·자식·기본의 독립적인 불변 색상/두께 계약 및 유효 값 검증. 기본 부모 핑크, 자식/기본 기존색.
- [x] S2: 실제 계층의 역할 변경·선택/핸들·GPU 캐시·Undo/Redo와 연동. 오른쪽 설정 패널에 역할별 두께 적용/초기화.
- [x] S3: Core 158 + Imaging/WPF 97 통과. 실제 RTX/DX12 debug·150% DPI에서 설정 패널 입력/적용·색상/두께·자식 이탈/복원 확인. 20만 개 평면/PCB 회전에서 반복 ROI 업로드 0 B, 정확한 Undo/Redo 확인. Release 배포와 문서 갱신. [설계](../docs/decisions/0011-roi-role-styles.md), [검증](../docs/roi-style-validation.md).

## 2026-10-02 대량 ROI 회전의 반복 작업 제거

- [x] G1: 기존 Release의 4만/20만 ROI 회전과 이미지 경계 회전을 재현. Move마다 전체 형상 복제·경계 탐색 할당이 UI를 막는 병목을 측정했다.
- [x] G2: 불변 선택 + 공통 강체 변환, DX12 보관 instance의 셰이더 회전, 최초 경계 이탈 각도 캐시, 전체 강체 확정의 BVH refit 구현. 속성·핸들·부모 태그·OpenCV가 같은 미리보기 형상을 사용한다.
- [x] G3: 최초 자손/부분 선택 준비를 worker로 이동하고 최신 요청 하나만 유지. 취소 후 재드래그도 준비 worker를 직렬화하며 Mouse Up은 기존 원자적 확정과 한 번의 Undo/Redo를 사용한다.
- [x] G4: Core 158 + Imaging/WPF 69 통과. 최종 Release 실제 RTX/DX12 debug smoke(회전 버튼·OpenCV 회전 미리보기·취소·이력·미니맵·라이브) 종료 0. 4만/20만/40만, PCB 부모 선택 3회, 부분 선택, 경계, logical CPU 1개, debug 등 10개 회전 시험에서 정확한 Undo/Redo 확인. [실측](../docs/rotation-performance.md), [원칙 14개 검토](../docs/decisions/0010-retained-rigid-rotation.md), [원시 자료](../docs/data/rotation-measurements.zip).
- [ ] G5: 최초 대규모 준비가 끝나기 전 표시 지연, 큰 확정/이력의 GC·UI 꼬리 지연, 실제 저사양 GPU 및 현장 PCB+8K 라이브 동시 부하. 반복 입력 개선을 모든 구간 무지연 달성으로 표현하지 않는다.

## 2026-10-02 CPU 제약 환경의 편집 응답성

- [x] L1: `fb97900`와 최종판의 32,940개 평면/PCB 분산 선택을 logical CPU 1개 조건에서 각 3회 비교. 중앙값 약 3.4/3.8배 개선, UI 할당 약 69–71% 감소.
- [x] L2: 대량 선택/이동 확정/삭제/붙여넣기/이력의 불변 준비·검증·원자적 게시, 단일 pending, 처리 중 탐색·Esc, UI 목록 슬롯/Reset 일괄 처리, worker 우선순위 제한 구현.
- [x] L3: Core 117 + Imaging/WPF 46, 변이 검증, Release 경고/오류 0, 최종 publish RTX/DX12 debug smoke, 300→19,200 PCB 복제, 32,940개 반복/10만 개 정확성 및 입력 응답 검증. [실측·한계](../docs/low-spec-performance.md), [14개 원칙 검토](../docs/decisions/0009-responsive-bulk-editing.md).
- [ ] L4: 일반 복사·생성·그룹화·필터 등록·resize preview 및 큰 인덱스 초기 구축, GC/스케줄링 꼬리 지연. 회전은 G1–G5에서 별도 개선·검증한다. 실제 저사양 CPU/GPU·현장 PCB+8K 라이브 장시간 시험. 단일 코어에서 10만 이동 완료 약 0.97–1.54초/최대 UI 간격 139ms가 남아 있어 무지연 완료로 표시하지 않는다.

## 2026-10-02 반복 렌더·입력 비용 제거

- [x] R1: 1만/32,940개 평면·PCB·중첩에서 UI/렌더 단계 시간·할당·업로드 기준 측정. 경계 드래그의 40회 전체 복제와 매 프레임 CPU 윤곽/태그 생성 확인.
- [x] R2: DX12 instance block 재사용·선택 facts·정확한 BVH hit·화면 밀도 태그 및 생성 예산·불변 이동 preview 적용. 필터/속성/이름표도 표시 위치를 공유하며 이력·정확성 유지.
- [x] R3: 32,940개 pan/wide/zoom 각 3회, 10만/100만 pan, 이동 확정/Undo/Redo/취소, Core 68 + Imaging/WPF 38, RTX/DX12 debug 캡처·150% DPI·기존 미니맵/필터 smoke 검증. [실측과 검토](../docs/retained-roi-performance.md). B1 정식 장시간 수용 시험 및 물리 포인터 전체 E2E와 구분.
- [ ] R4: 변형 확정/목록/일부 전체 명령은 L1–L3에서 개선. 회전 반복 형상 경로는 G1–G5로 추적한다. resize, 남은 명령과 실제 PCB+8K 라이브 지속 부하는 L4에서 계속 추적.

## 2026-10-02 대량 복제 프리징과 미니맵

- [x] C2: 7,000개 마퀴 선택 → 복사/붙여넣기 재현. 중첩 계층 검색과 필터 GPU 슬롯 고갈 수정, 비동기 준비/취소, 새 ID·계층·Undo/Redo 검증. 300개부터 평면/PCB/중첩 각각 19,200개까지 반복 복제·UI heartbeat 통과. [실측 및 남은 지연](../docs/clipboard-minimap-validation.md).
- [x] N1: 전체 이미지/현재 가시 영역의 GPU 미니맵, 클릭/드래그 탐색, 확대·현재 DPI·이미지 교체/라이브 연동. 혼합 DPI 모니터 이동은 기존 미검증 범위 유지.
- [x] N2: Core 60 + Imaging/WPF 32 통과, Release 경고/오류 0. 최종 배포본 RTX/DX12 debug smoke 종료 0, 8K/24K/라이브 미니맵 캡처 확인, 문서·14개 원칙 검토·배포 갱신. 물리 포인터 전체 E2E 미검증.

## 2026-10-02 대량 PCB ROI 성능 개선

- [x] P1: 1,550/3,550개 평면/PCB 계층의 선택·hover·click·pan·drag UI 지연/할당과 렌더 지표 기준 측정.
- [x] P2: 공유 BVH/변경 ID 이력/목록 인덱스와 비활성 속성 표시로 순회·TSF 비용 제거. 이전 스냅샷·큰 이동·수명·Undo 회귀 검증.
- [x] P3: 10만/100만/200만 고정 ROI 실측. PCB 부품+8패드와 평면, 전체 요약 보기/확대 편집을 분리. [측정과 한계](../docs/roi-scalability.md).
- [x] P4: 최종 Release 경고/오류 0, Core 52 + Imaging/WPF 29 통과. 최종 publish RTX/DX12 debug smoke 종료 0, 원칙 14개 검토·재현 명령·반복 전후 수치 보존.
- [ ] 수백만 ROI의 초기 생성/계층 구축과 전체 선택·삭제·복원 중 UI 점유 제거. 200만 PCB 구축 약 49초, 백만 전체 명령 최대 6.84초가 남아 있음. 실제 PCB 데이터와 8K 라이브 동시 지속 부하도 미검증.

## 2026-10-02 필터 등록·복제·메모리 후속

- [x] M1: 실제 8K RGBA / 24K Gray16 파일을 각 60회 교체하는 메모리 진단과 수정 전 기준 측정. Private Bytes/managed heap/handles/이력 크기 및 DX12 오류 기록.
- [x] F1: 필터 설정을 ROI 값으로 저장하고 Undo/Redo에 포함. 등록된 ROI 재선택 시 자동 표시, 미등록 ROI는 원본, 다중 선택은 각각의 설정 적용. 숫자는 LostFocus 확정, 혼합 선택 등록 해제 지원.
- [x] C1: 부모/자손 포함 복사·붙여넣기, 새 ID·이미지 경계·이력 원자성, 버튼/Ctrl+C/Ctrl+V/Del 및 텍스트 편집 단축키 보존. Core와 실제 WPF/DX12 명령·HWND 메시지 smoke 검증.
- [x] M2: 같은 조건 120회 및 600회 교체 재측정. 지속 누수 징후 미관찰. 타일 큐 backpressure/100회 소스 교체의 정확히 한 번 해제 회귀 검증. 상세 수치와 한계는 [메모리 보고서](../docs/memory-replacement-validation.md).
- [x] 문서·설계 원칙 14개 검토, Release 경고/오류 0, Core 44 + Imaging 26 통과, 최종 publish DX12 debug smoke 종료 코드 0. 물리 입력 전체 E2E 및 10분 이상 부하는 미검증으로 유지.

기준: [구현 계획](plan.md), [작업 지침](../AGENTS.md). 상태: 2026-10-02 샘플 기능 구현 및 실제 RTX 검증. 체크된 항목만 해당 수용 범위 검증을 완료했고, 남은 항목은 아래에 구체적으로 기록한다. 실행/제약은 [README](../README.md), 근거는 [런타임 검증](../docs/runtime-validation.md)과 [성능 기록](../docs/performance-baseline.md)을 따른다.

각 단계의 예상 변경 파일은 대표 경로다. 한 단계가 약 5개 파일 또는 한 작업 세션을 넘는다면 구현 전에 하위 작업으로 나눈다. 테스트 파일과 문서도 변경 범위에 포함한다. 상위 의존 작업이 완료되기 전에 해당 기능 완료를 선언하지 않는다.

총 21개 작업이다. T04A에서 Undo/Redo 기반, T08A에서 부모 생성형 그룹화, T10A에서 뷰포트 밖 수동 조작 버튼을 구현한다. 모든 작업의 공통 완료 조건은 클린 코드·적절한 디자인 패턴·SOLID 5개·GRASP 9개 검토다. 성능 때문에 원칙 예외가 불가피하면 측정 근거와 대안을 제시하고 사용자 명시적 승인 전에는 해당 변경을 적용하지 않는다. [예외 절차](../AGENTS.md#필수-설계-원칙과-성능-예외-절차)를 따른다.

## 공통 검증 명령

아래 축약명은 각 작업의 검증 항목에서 사용한다. 테스트/벤치마크 프로젝트를 만든 이후에만 실행할 수 있다.

```powershell
# B: 솔루션 빌드 (패키지 변경 직후에는 먼저 restore)
dotnet restore .\TeachingToolWpf.slnx
dotnet build .\TeachingToolWpf.slnx -c Release --no-restore

# TC: Core 자동 테스트
dotnet test .\tests\TeachingToolWpf.Core.Tests\TeachingToolWpf.Core.Tests.csproj -c Release

# TI: 이미지/버퍼 자동 테스트
dotnet test .\tests\TeachingToolWpf.Imaging.Tests\TeachingToolWpf.Imaging.Tests.csproj -c Release

# RUN: 실제 WPF/GPU 동작 확인
dotnet run --project .\TeachingToolWpf.csproj -c Release

# BM: CPU 측정 도구 (GPU 및 표시 성능은 실제 앱에서 별도 측정)
dotnet run --project .\benchmarks\TeachingToolWpf.Benchmarks\TeachingToolWpf.Benchmarks.csproj -c Release
```

## T01 — Core와 테스트의 프로젝트 경계

- [x] 완료
- 목적: WPF와 무관한 모델/좌표 테스트를 추가할 수 있는 최소 구조를 만든다.
- 수용 기준: 루트 앱이 하위 소스를 중복 컴파일하지 않음; Core가 WPF/DX12 패키지를 참조하지 않음; 기존 WPF 창이 계속 실행됨.
- 검증: B, RUN. 테스트 프로젝트의 검색/실행 경로를 확인하되 빈 테스트 실행을 기능 검증으로 기록하지 않는다.
- 의존: 없음.
- 예상 파일: `TeachingToolWpf.csproj`, `TeachingToolWpf.slnx`, `src/TeachingToolWpf.Core/TeachingToolWpf.Core.csproj`, `tests/TeachingToolWpf.Core.Tests/TeachingToolWpf.Core.Tests.csproj` (4개).

## T02 — WPF 내부 DX12 표시 실험

- [x] 완료
- 목적: 전용 자식 HWND에 DX12 배경색을 표시하고 기술 선택을 실제 환경에서 확인한다.
- 수용 기준: 기준 RTX 3060 Laptop을 우선 선택하고 실제 adapter/feature 정보 표시; WPF 주변 패널과 GPU viewport가 표시됨; 종료 후 GPU/창 자원이 정리됨.
- 검증: B, RUN, DX12 debug layer. Ryzen 5 5600H / RAM 32 GB / RTX 3060 Laptop 6 GB에서 adapter·드라이버·전원 조건·패키지 버전·배포 요구사항을 `docs/rendering-spike.md`에 기록. 내장 GPU 사용/지원 실패는 명시적으로 표시한다.
- 의존: T01.
- 예상 파일: `TeachingToolWpf.csproj`, `MainWindow.xaml`, `Controls/Dx12Viewport.cs`, `Rendering/Dx12Renderer.cs`, `docs/rendering-spike.md` (5개).

## T03 — 좌표 변환과 viewport 입력

- [ ] 전체 수용 기준 검증 완료
- 현재: 구현됨. 실제 150% DPI/기본 resize/커서 앵커 검증. 100/200% 실제 모니터 전환은 미검증.
- 목적: 확대·이동·Fit·1:1 및 HWND 포인터/키보드 입력 경계를 만든다.
- 수용 기준: 커서 중심 확대와 역변환 왕복이 허용오차 안에 있음; DIP와 물리 픽셀·1:1 의미가 일치; 포커스/포인터 캡처/기본 resize가 동작함.
- 검증: B, TC, RUN. 합성 격자에서 100/150/200% DPI와 큰 이미지 좌표의 왕복/커서 앵커 검증.
- 의존: T02.
- 예상 파일: `src/TeachingToolWpf.Core/ViewportTransform.cs`, `tests/TeachingToolWpf.Core.Tests/ViewportTransformTests.cs`, `Controls/Dx12Viewport.cs`, `Rendering/Dx12Renderer.cs`, `MainWindow.xaml` (5개).

### 체크포인트 A — 표시 경로

- [ ] 실제 하드웨어에서 창 표시·입력·resize·DPI·종료를 확인하고 T02의 기술 선택 근거를 기록했다.
- [ ] 소프트웨어 렌더 결과와 하드웨어 결과, 검증 불가능한 항목을 구분했다.

## T04 — 8K 배경 및 24K 폭 분할 표시

- [ ] 전체 수용 기준 검증 완료
- 현재: 8K/24K 분할 표시는 구현·실행됨. 분수 배율 선형 필터의 타일 gutter/seam 보장은 후속 항목.
- 목적: 실제 배경 이미지와 채널 보존 업로드를 연결한다. 첫 단계부터 단일 texture 축을 넘는 입력도 검증한다.
- 수용 기준: 8K Gray8/Gray16 및 BGRA32 정적 소스 표시; 24576×1024 합성 스트립이 분할되어 경계 없이 표시; 파일 교체·실패 시 ROI 좌표 정책과 소스 세대 정책 적용.
- 검증: B, RUN. 패턴 이미지의 픽셀/계조·stride padding·타일 경계를 확인하고 업로드량 기록. 정적 파일 교체 시 이전 결과가 뒤늦게 나타나지 않음 확인.
- 의존: T03.
- 예상 파일: `Imaging/ImageFrame.cs`, `Imaging/StaticImageSource.cs`, `Rendering/ImageTextureUploader.cs`, `Shaders/Image.hlsl`, `Rendering/Dx12Renderer.cs` (5개).

## T04A — 필수 Command/Memento 및 Undo/Redo 기반

- [x] 완료
- 목적: 첫 ROI 편집부터 공통 명령과 복원 가능한 문서 상태를 사용하도록 만든다.
- 수용 기준: WPF 비의존 편집 명령/이력과 ROI 문서 계약; Undo/Redo 및 성공한 새 명령의 Redo 분기 소거; 실패/취소/no-op가 문서와 이력을 바꾸지 않음.
- 검증: B, TC. 단순 ROI 생성/변경 명령으로 반복 Undo/Redo, 빈 스택, 새 분기, 상태 스냅샷 불변성, 건수/바이트 예산을 검증한다. 이력이 이미지/GPU 객체를 복제하지 않는지 리뷰한다.
- 의존: T01, T04.
- 예상 파일: `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiDocument.cs`, `src/TeachingToolWpf.Core/IEditCommand.cs`, `src/TeachingToolWpf.Core/EditHistory.cs`, `tests/TeachingToolWpf.Core.Tests/EditHistoryTests.cs` (5개).

## T05 — ROI 생성·단일 선택·삭제

- [x] 완료
- 목적: T04A의 회전 사각형 모델과 이력에 기본 사용자 편집을 연결한다.
- 수용 기준: 생성·삭제가 공통 명령을 통해 Undo/Redo 가능; ROI 내부/경계 hit test가 zoom/DPI에 일관됨; 퇴화/비정상 수치 거부.
- 검증: B, TC, RUN. 큰 좌표와 작은 ROI에서 hit test·네 꼭짓점 표시 일치, 생성/삭제의 Undo/Redo 및 ID 보존 확인.
- 의존: T04A.
- 예상 파일: `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `Rendering/RoiRenderer.cs`, `Controls/Dx12Viewport.cs`, `tests/TeachingToolWpf.Core.Tests/RoiGeometryTests.cs` (5개).

## T06 — 크기·회전·절대 0도 초기화

- [x] 완료
- 목적: 핸들 및 수치 입력으로 ROI 형상을 조절하고 각도를 읽을 수 있게 한다.
- 수용 기준: 회전된 상태의 8개 resize 핸들과 회전 피벗 동작; 이미지 기준 절대 각도 표시/입력/0도 초기화; Shift 회전 스냅과 편집 취소 동작.
- 검증: B, TC, RUN. 0/90/180/359.9도, 반대 핸들 고정, 각도 wrap, 최소 크기 경계 확인. resize/회전/0도 초기화 각각이 한 명령으로 Undo/Redo되는지 검증.
- 의존: T05.
- 예상 파일: `src/TeachingToolWpf.Core/RoiEditor.cs`, `Rendering/RoiRenderer.cs`, `ViewModels/RoiPropertiesViewModel.cs`, `MainWindow.xaml`, `tests/TeachingToolWpf.Core.Tests/RoiTransformTests.cs` (5개).

### 체크포인트 B — 단일 ROI 티칭

- [ ] 8K 이미지 교체 → 확대/이동 → 생성 → resize/회전 → 0도 초기화 → 삭제가 연결되고 ROI 변경을 Undo/Redo할 수 있다.
- [ ] 24K 폭 합성 스트립에서 texture 경계와 ROI 위치가 맞는다.

## T07 — 다중 선택과 겹침 탐색

- [ ] 전체 수용 기준 검증 완료
- 현재: Ctrl/Shift/마퀴/Alt 겹침 탐색 및 다중 이동·회전 구현. 별도 활성 ROI와 혼합 수치의 일괄 입력 UI는 미구현; 다중 선택은 개수/공통 경계로 표시.
- 목적: Ctrl/Shift·마퀴·Alt 순환 선택과 다중 이동/회전을 제공한다.
- 수용 기준: 선택 집합과 활성 ROI가 명확함; 선택별 표시/공통 피벗/혼합 속성 표시 동작; 텍스트 입력 중 단축키가 가로채지 않음.
- 검증: B, TC, RUN. 겹침·마퀴 완전 포함·선택 토글·빈 영역/취소 동작, 다중 이동/회전의 한 단계 Undo/Redo 확인.
- 의존: T06.
- 예상 파일: `src/TeachingToolWpf.Core/SelectionState.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `Rendering/RoiRenderer.cs`, `Controls/Dx12Viewport.cs`, `tests/TeachingToolWpf.Core.Tests/SelectionTests.cs` (5개).

## T08 — 회전 ROI의 부모·자식 관계

- [x] 완료
- 목적: 완전 포함에 따른 관계와 부모 변형 상속을 구현한다.
- 수용 기준: 최소 포함 부모·동률·경계 정책이 결정적이며 순환 없음; 재부모화 시 이미지 기준 형상 보존; 부모+자식 동시 선택 변형의 중복 방지와 부모 연쇄 삭제/전체 복원 동작.
- 검증: B, TC, RUN. 3단계 중첩, 부분 겹침, 바깥 ROI 나중 생성, 자식 이탈, 부모 resize/회전, 중복 선택 변형 검증. 선택하지 않은 자손도 연쇄 삭제되고 겹친 타 ROI는 남으며, 한 번의 Undo로 전체 트리/ID/이름/형상/선택이 복원되고 Redo로 같은 집합이 삭제됨을 테스트.
- 의존: T07.
- 예상 파일: `src/TeachingToolWpf.Core/RoiHierarchy.cs`, `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `tests/TeachingToolWpf.Core.Tests/RoiHierarchyTests.cs`, `tests/TeachingToolWpf.Core.Tests/SelectionTests.cs` (5개).

## T08A — 새 부모 ROI로 그룹화 및 그룹 해제

- [x] 완료
- 목적: 선택 ROI들을 감싸는 실제 부모 ROI를 만들고, 형상을 보존하며 묶음을 해제한다.
- 수용 기준: 회전 형상과 여백을 포함하는 0도 부모 및 생성 경로 메타데이터; 완전 포함 규칙과 모든 선택 ROI의 소속 검증; 그룹화/해제/연쇄 삭제의 구별과 한 단계 Undo/Redo.
- 검증: B, TC. 회전된 여러 ROI, 선택된 부모+자식, 선택 밖 ROI 포함, 기존 더 작은 부모와 충돌하는 경우, 중첩 그룹, 해제 후 world 형상/재부모화 및 반복 Undo/Redo를 검증. 실패/no-op는 문서/Redo를 보존한다.
- 의존: T08, T04A.
- 예상 파일: `src/TeachingToolWpf.Core/GroupRoisCommand.cs`, `src/TeachingToolWpf.Core/UngroupRoisCommand.cs`, `src/TeachingToolWpf.Core/Roi.cs`, `src/TeachingToolWpf.Core/RoiDocument.cs`, `tests/TeachingToolWpf.Core.Tests/GroupCommandTests.cs` (5개).

## T09 — 부모 이름표와 ROI 트리

- [x] 완료
- 목적: 계층과 부모 이름을 화면·패널에서 확인하고 수정할 수 있게 한다.
- 수용 기준: 한글 부모 이름과 자식 수 표시; ROI 회전/zoom에도 이름표는 읽을 수 있음; 트리 선택/이름/각도와 viewport가 동기화됨.
- 검증: B, RUN. 한글/영문/긴 이름과 이름 변경 Undo/Redo, 재부모화, 중첩 배지, DPI 이동 및 화면 밖 ROI의 트리 접근 확인.
- 의존: T08A.
- 예상 파일: `Rendering/LabelAtlas.cs`, `Rendering/RoiRenderer.cs`, `ViewModels/RoiTreeViewModel.cs`, `ViewModels/RoiPropertiesViewModel.cs`, `MainWindow.xaml` (5개).

### 체크포인트 C — 계층 편집

- [ ] R1–R6의 핵심 경로와 다중 선택의 부모·자식 동작을 실제 화면에서 재현했다.
- [ ] 사용자 요청대로 부모 resize의 초안 정책(자식 크기 유지)을 계획/지침/테스트에 동일하게 반영했다.
- [ ] 부모 연쇄 삭제와 전체 하위 트리 Undo/Redo가 확인되며, 클린 코드·SOLID·GRASP 검토 및 사용 패턴의 이유를 기록했다.

## T10 — 필수 Undo/Redo 통합 및 실패 원자성

- [x] 완료
- 목적: T04A부터 연결한 필수 이력을 전체 편집·수동 배경 교체 경로에 통합하고 실패 처리를 완성한다.
- 수용 기준: 한 드래그/다중 편집/연쇄 삭제가 한 undo 단위; 형상·계층·이름·선택 및 수동 소스 교체 복원; 취소/실패/no-op가 문서와 이력 위치를 유지.
- 검증: B, TC, RUN. 전체 편집의 반복 왕복, 새 명령 뒤 Redo 제거, 예산 경계, 원본 파일 변경/삭제로 배경 복원 실패, 부분 실패 rollback, 캡처 상실 확인. 라이브 프레임 도착은 이력을 쌓거나 지우지 않아야 한다.
- 의존: T09.
- 예상 파일: `src/TeachingToolWpf.Core/EditHistory.cs`, `src/TeachingToolWpf.Core/RoiEditor.cs`, `Controls/Dx12Viewport.cs`, `ViewModels/TeachingViewModel.cs`, `tests/TeachingToolWpf.Core.Tests/EditHistoryTests.cs` (5개).

## T10A — 외부 수동 조작 툴바와 속성 패널

- [x] 완료
- 목적: 단축키를 모르는 사용자도 이미지 교체, zoom/pan, ROI 편집, 그룹화를 화면 버튼으로 시작할 수 있게 한다.
- 수용 기준: 계획 7.1절의 파일/보기/도구/편집/속성 버튼이 실제 명령과 연결; 버튼·단축키가 같은 명령/이력 사용; 이미지/선택/로딩/이력에 맞는 CanExecute와 활성 도구/배율 표시.
- 검증: B, TC, RUN. 버튼으로 이미지 열기→확대/축소/Fit/1:1→이동 도구/방향 이동→ROI 2개 생성→그룹화→각도 초기화→그룹 해제→Undo/Redo→부모 연쇄 삭제를 재현한다. 빈 문서, 잘못된 배율, 선택 부족, 파일 취소/실패, 도구 전환 중 드래그 취소, 작은 창/고 DPI를 확인한다. 합성 라이브 시작/정지 연결은 T12에서 검증한다.
- 의존: T10, T08A.
- 예상 파일: `MainWindow.xaml`, `ViewModels/TeachingViewModel.cs`, `ViewModels/ViewportViewModel.cs`, `Controls/Dx12Viewport.cs`, `tests/TeachingToolWpf.Core.Tests/ToolbarCommandTests.cs` (5개). Core 테스트는 WPF 타입 없이 공통 명령의 실행 가능 조건을 검증하고 WPF 바인딩은 RUN으로 확인한다.

## T11 — 프레임 수명과 제한된 표시 큐

- [x] 완료
- 목적: 장비 소스로 연결할 수 있는 lease·세대 ID·취소·최신 프레임 전달을 구현한다.
- 수용 기준: 대기 슬롯 1개와 제한된 진행 프레임; 버린/완료된 버퍼가 정확히 한 번 반환; GPU 작업 완료 전 재사용 없음.
- 검증: B, TI. 생산/소비 속도 역전, 소스 교체, 취소·종료, stride/길이 불일치, GPU 완료 지연 모의 시험. imaging 테스트 프로젝트를 솔루션에 등록한다.
- 의존: T04, T10A.
- 예상 파일: `Imaging/FrameMailbox.cs`, `Rendering/ImageTextureUploader.cs`, `tests/TeachingToolWpf.Imaging.Tests/TeachingToolWpf.Imaging.Tests.csproj`, `tests/TeachingToolWpf.Imaging.Tests/FrameLifetimeTests.cs`, `TeachingToolWpf.slnx` (5개).

## T12 — 최소 8K 라이브 소스와 계측

- [ ] 전체 수용 기준 검증 완료
- 현재: 7680×4320 Gray8/Gray16/BGRA32 + ROI 500, 각 3회 측정 완료. 8192² 실시간/모든 추가 진단 지표는 미검증. 정지 후 마지막 원본 유지.
- 목적: 프레임마다 달라지는 8K 패턴을 표시하면서 ROI를 계속 편집한다.
- 수용 기준: 외부 시작/정지 버튼으로 7680×4320 이상 합성 입력을 조작하고 Gray8/Gray16/BGRA32 각각 시험; FPS·source-to-Present 지연·드롭·업로드량 측정; ROI/선택/뷰포트가 프레임 도착으로 초기화되지 않고 정지 시 마지막 프레임 유지.
- 검증: B, TI, RUN. B2의 임시 60 FPS 부하를 형식별로 측정하고 8192×8192 스트레스 결과를 분리. 원본 생산 속도와 렌더 속도를 각각 기록한다.
- 의존: T11.
- 예상 파일: `Imaging/SyntheticFrameSource.cs`, `Rendering/RenderMetrics.cs`, `Rendering/Dx12Renderer.cs`, `ViewModels/DiagnosticsViewModel.cs`, `MainWindow.xaml` (5개).

### 체크포인트 D — 실시간

- [ ] 4K 결과로 대체하지 않고 최소 8K 실측을 기록했다.
- [ ] GPU fence·UI 스레드·프레임 큐 간 교착이 없고 10분 실행 중 버퍼 수가 제한된다.
- [ ] 기준 장비의 실제 adapter/드라이버/전원 조건, 미정인 입력 FPS/픽셀 형식과 달성하지 못한 목표를 기록했다.

## T13 — 24K급 일반 이미지 파일의 메모리가 제한된 읽기

- [x] 완료
- 목적: 스캔 완료된 큰 이미지를 파일 열기로 읽어 디코더가 제공하는 영역/순차 읽기를 타일 계약에 연결한다.
- 수용 기준: 지원 일반 형식의 크기·픽셀 형식 자동 인식; 디코더의 영역/순차 기능에 맞게 제한된 메모리로 읽고 불가하면 이유 표시; 취소·소스 교체·큰 크기/오프셋 검증.
- 검증: B, TI. 실제 또는 생성한 24K급 PNG/JPEG/BMP 파일 열기와 취소, 형식별 메모리 사용, 손상된 메타데이터를 검증. 3/6 GiB RAW fixture는 별도 오프셋/스트레스 시험에만 사용하고 일반 파일 시험을 대체하지 않는다. TIFF/BigTIFF는 지원한 변형을 별도 기록한다.
- 의존: T11.
- 예상 파일: `Imaging/ITileSource.cs`, `Imaging/IImageDecoder.cs`, `Imaging/ImageDecoderFactory.cs`, `Imaging/ImageImportService.cs`, `tests/TeachingToolWpf.Imaging.Tests/ImageImportTests.cs` (5개).

## T14 — 피라미드와 취소 가능한 전처리

- [ ] 전체 수용 기준 검증 완료
- 현재: 디스크 단계 생성/버전/manifest 마지막 게시/사전 취소 테스트 구현. 프로세스 강제 종료 뒤 partial 캐시 자동 정리와 디코드 도중 강제 취소 반복 시험은 후속 항목.
- 목적: 24K급 긴 이미지를 축소 표시할 overview와 재개방 캐시를 만든다.
- 수용 기준: 제한된 작업 메모리로 단계 생성; 파일 정체성/메타데이터/변경 버전에 따라 캐시 무효화; 진행률·취소·중단 캐시 복구 동작.
- 검증: B, TI, RUN. 비정형 크기/Gray16 계조/타일 경계/캐시 변경 검증. cold 생성 시간과 warm 재개방 시간 분리.
- 의존: T13.
- 예상 파일: `Imaging/PyramidBuilder.cs`, `Imaging/PyramidTileSource.cs`, `Imaging/DerivedImageCache.cs`, `ViewModels/ImageLoadViewModel.cs`, `tests/TeachingToolWpf.Imaging.Tests/PyramidTests.cs` (5개).

## T15 — 가시 타일 요청과 메모리 예산

- [ ] 전체 수용 기준 검증 완료
- 현재: 512px LOD/overview/32개 준비 큐/가시 타일 우선 LRU 구현. 가시 타일·fence 지연 해제는 논리 캐시 예산과 별도이고 gutter는 미구현. 엄격한 전체 프로세스 메모리 상한을 주장하지 않음.
- 목적: 현재 viewport와 zoom에 필요한 타일을 우선 표시한다.
- 수용 기준: LOD/가시 영역/제한된 prefetch 적용; CPU/GPU 캐시가 바이트 상한 준수; 저해상도 대체 표시와 고해상도 교체 시 seam·세대 혼입 없음.
- 검증: B, TI, RUN. B3 3/6 GiB 원본의 빠른 pan/zoom, eviction, 취소, 작은 GPU 예산 모의 시험. tile hit/miss·캐시 사용량·전체 프로세스 메모리를 각각 기록.
- 의존: T12, T14.
- 예상 파일: `Imaging/VisibleTileScheduler.cs`, `Imaging/CpuTileCache.cs`, `Rendering/GpuTileCache.cs`, `Rendering/Dx12Renderer.cs`, `tests/TeachingToolWpf.Imaging.Tests/TileSchedulingTests.cs` (5개).

### 체크포인트 E — 대용량

- [ ] 24K급 폭이 여러 texture로 나뉘어 동작하고 3/6 GiB 원본을 전체 적재하지 않는다.
- [ ] 스캔 완료된 일반 파일을 사용자 메타데이터 수동 입력 없이 열었고, 카메라 라인 획득/조립 기능을 범위에 추가하지 않았다.
- [ ] 전체 Fit을 위한 최초 overview 비용과 이미 준비된 타일의 탐색 성능을 별도로 기록했다.

## T16 — 창·장치·종료 수명 강화

- [ ] 전체 수용 기준 검증 완료
- 현재: resize/최소화 중 제출 중단/복원/재연결/종료 경로 구현. 실제 장치 제거 및 혼합 DPI 모니터 이동은 미검증.
- 목적: 사용 중 창/장치 상태가 바뀌어도 편집 데이터와 자원 수명을 유지한다.
- 수용 기준: 최소화/복원/0 크기/반복 resize/다중 DPI에서 정상 동작; device removal에 복구 또는 명시적 재시도 제공; 닫는 중 작업 취소·GPU 완료·HWND 정리 순서가 안전함.
- 검증: B, TI, TC, RUN 및 DX12 debug layer. 최소화·resize·소스 교체 반복, 모의 장치 제거, 닫는 도중 업로드 확인.
- 의존: T15.
- 예상 파일: `Controls/Dx12Viewport.cs`, `Rendering/Dx12Renderer.cs`, `Rendering/DeviceRecovery.cs`, `Imaging/FrameMailbox.cs`, `docs/runtime-validation.md` (5개).

## T17 — 선택 디자인과 키보드 검증

- [ ] 전체 수용 기준 검증 완료
- 현재: 고정 DIP 이중 윤곽/핸들/각도/태그/외부 버튼/단축키 구현. 150% 합성 패턴 캡처 확인. 전체 접근성/모든 DPI와 대비 조건 수동 검증은 후속 항목.
- 목적: 선택·호버·포커스·각도 HUD·작은 ROI 조작을 일관된 제품 동작으로 마무리한다.
- 수용 기준: R8 상태 구분과 고정 DIP 크기 유지; 밝고 어두운 영상 모두 선택/이름이 읽힘; 키보드/트리/속성으로 마우스 조작을 보완하고 포커스가 사라지지 않음.
- 검증: B, RUN. 100/150/200% DPI, 밝기/패턴이 다른 8K 영상, 조밀한 다중 선택과 작은 ROI를 수동 검증해 기록한다.
- 의존: T16.
- 예상 파일: `Rendering/RoiRenderer.cs`, `Rendering/LabelAtlas.cs`, `Controls/Dx12Viewport.cs`, `MainWindow.xaml`, `docs/runtime-validation.md` (5개).

## T18 — 성능 비교와 샘플 사용 문서

- [x] 완료
- 목적: B1–B5와 R1–R12의 최종 결과를 재현 가능한 형태로 남긴다.
- 수용 기준: 기준 장비·입력·설정·반복 조건/결과 공개; 미달 항목은 병목 근거와 함께 유지하고 원칙 예외는 사전 사용자 승인 및 ADR 확인; 필수 Undo/Redo·설치·실행·단축키·지원 형식·알려진 제약 문서화.
- 검증: B, TC, TI, BM, RUN. B1–B5를 계획의 절차로 측정하고 변경 전후 비교. 성능 수정이 필요하면 별도 작은 작업으로 나눈 후 관련 시나리오만 재측정한다.
- 의존: T17.
- 예상 파일: `benchmarks/TeachingToolWpf.Benchmarks/TeachingToolWpf.Benchmarks.csproj`, `benchmarks/TeachingToolWpf.Benchmarks/Program.cs`, `TeachingToolWpf.slnx`, `docs/performance-baseline.md`, `README.md` (5개).

### 최종 체크포인트

- [ ] R1–R12의 수용 기준과 미검증 항목을 구분해 기록했다.
- [ ] 최소 8K 실시간과 24K급 라인 스캔을 실제 또는 명시된 합성 입력으로 검증했다.
- [ ] ROI 포함·절대 각도·0도 초기화·다중 선택·부모 이름·부모 연쇄 삭제·필수 Undo/Redo가 같은 규칙을 따른다.
- [ ] 외부 버튼으로 기본 사용자 흐름을 완료했고 그룹화/해제와 삭제의 결과 및 Undo/Redo가 각각 검증되었다.
- [ ] 클린 코드·디자인 패턴·SOLID 5개·GRASP 9개를 검토했고, 예외가 있다면 사용자 승인 내용과 구현 범위가 일치한다.
- [ ] 빌드/테스트/실행/메모리·성능 측정 결과가 있으며, 통과하지 않은 목표를 임의로 낮추지 않았다.

## 이번 구현의 검증 요약

- [x] Core 28개 / 영상 12개 자동 테스트 통과; Release 빌드 경고·오류 0.
- [x] 실제 DX12/RTX HWND 입력, 그룹/연쇄 삭제/Undo/Redo, 수치 회전, Escape, GPU 재연결을 smoke로 검증.
- [x] 8K Gray8/Gray16/BGRA32 + ROI 500개 3회 반복 측정, 드롭/백분위/조건을 기록.
- [x] 24576×131072 BigTIFF Gray8/Gray16, 3/6 GiB 디코드 데이터와 마지막 타일 검증. RAW 시험은 실제 BigTIFF 시험으로 대체.
- [x] 24K 폭 PNG/JPEG 및 8K WebP의 채널/경계 검증. WebP 자체 크기 제한 때문에 24K WebP는 지원 대상으로 표시하지 않음.
- [x] SOLID 5개 / GRASP 9개와 적용 패턴의 근거 기록. 승인되지 않은 원칙 예외 없음.
- [ ] B1 연속 정적 편집의 정식 반복 측정, 8192² 실시간, B4 GPU 10,000 ROI 조작, B5 10분 연속 안정성.
- [ ] 실제 카메라/현장 이미지, 혼합 DPI, 실제 장치 제거, 물리 모니터 표시 지연 측정.

위 미완료 항목은 구현된 샘플의 제한/추가 검증이며 완료 체크로 덮지 않는다. 앞의 원래 체크포인트는 전체 조건을 검증하기 전까지 미체크로 유지한다.

## 2026-10-02 사용자 피드백 후속

- [x] STATIC의 HTTRANSPARENT 입력 원인 수정, 전용 HWND 및 실제 포인터로 ROI 생성 확인.
- [x] 도구/목록/캔버스/속성 분리, ROI 즉시 추가와 그리기 도구 분리.
- [x] 이미지 교체 시 ROI/선택 초기화, Undo/Redo로 소스/형상/뷰 복원, 실패 시 라이브 보존.
- [x] 그리기 교집합 및 이동/크기/회전/그룹/수치의 이미지 경계 제약, 0도 복원 보정.
- [x] 얇은 파란 선택 윤곽·흰 resize 핸들·원형 회전 핸들.
- [x] OpenCV Binarize/Canny 필터 선택, 명시적 미리보기, 선택/변형 변경 갱신, 원본 복귀.
- [x] 대용량 LOD 추출, 회전 ROI 픽셀 매핑, 단일 작업/늦은 결과 폐기, 라이브 최대 5Hz.
- [x] Core 36개 + Imaging 22개 테스트 통과; 실제 클릭/드래그와 RTX DX12 디버그 smoke 확인.
- [x] SOLID 5 / GRASP 9 검토, ADR/사용법/의존성 라이선스 갱신.
- [ ] 최종 publish 빌드에서 OpenCV 필터 포함 smoke 및 화면 표시 재확인.
