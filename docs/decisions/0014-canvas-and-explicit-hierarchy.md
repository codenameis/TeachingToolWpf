# ADR-014: 프로젝트 정리, VisionCanvas 분리와 명시적 ROI 계층

- 날짜: 2026-10-04
- 결정: 사용자 요청 반영. 종전 공간 포함 기반 부모 자동 판정은 폐기한다.
- 범위: 프로젝트 경계·계층 UX 변경. 기존 미커밋 VisionAlgorithm/검사 연결을 보존한다.

## 프로젝트 경계

| 프로젝트 | 책임 | 프로젝트 참조 |
|---|---|---|
| src/TeachingToolWpf | 실행 앱, 패널·VM, 이미지 교체·라이브 제어, 검사 adapter, 통합 진단 | Core, VisionCanvas, VisionAlgorithm |
| src/VisionCanvas | HwndHost, 네이티브 포인터 입력, DX12·셰이더·GPU 자원, 영상·타일·필터 표시, viewport 계약 | Core |
| src/TeachingToolWpf.Core | 기하, ROI, 명시적 계층, 입력 상태, 편집·이력 | 없음 |
| src/VisionAlgorithm | 전통적 C# 패턴 매칭 | 없음 |

앱 파일을 루트에서 src/TeachingToolWpf로 이동했다. 캔버스 파일은 src/VisionCanvas에 실제로 이동했으며 링크된 중복 소스나 앱 역참조가 없다. Core의 기존 이름은 유지한다. 기존 C# namespace도 보존해 파일 이동과 공개 이름 변경을 분리했다. XAML은 `assembly=VisionCanvas`로 외부 컨트롤을 참조한다.

IViewportSurface와 IFramePresenter는 Canvas의 좁은 소비자 계약이다. DX12/영상 패키지 버전과 GPU/타일/프레임 수명은 유지했다. Canvas의 내부 Win32 진단을 재사용하는 앱 smoke에만 InternalsVisibleTo를 허용했고, 테스트도 별도 friend assembly로 선언했다. 앱·알고리즘·Core의 상호 참조 규칙을 바꾸는 서비스 로케이터는 없다.

셰이더는 Canvas에서 빌드/배포 디렉터리로 복사한다. 라이선스·공통 문서·측정 자료는 앱에서 기존 상대 경로를 유지해 복사한다. tests/VisionCanvas.Tests는 앱 참조 없이 87개 캔버스·영상 테스트를 실행한다. 기존 Imaging.Tests에는 앱/WPF 통합 45개가 남는다. 솔루션은 src/tests/benchmarks 폴더로 정리했다.

## 계층 계약

1. `부모 추가`/`부모 그리기`/R은 독립 루트 ROI를 만든다. 기존 Add API와 평면 AddRange도 루트를 생성한다.
2. ROI 하나를 선택한 뒤 `자식 추가`/`자식 그리기`를 실행한다. 즉시 추가는 선택 ROI 중심에 1/3 크기로 생성하며 도형 선택을 따른다. 그리기는 시작할 때 지정한 부모 ID를 사용한다. 자식 아래 자식도 가능하다.
3. 공간 겹침·완전 포함·면적·이동·회전·resize·속성 편집으로 ParentId를 바꾸지 않는다. 부모 밖 자식도 허용하며 이미지 경계 제약은 유지한다. 부모의 이동·회전·삭제는 모든 명시적 자손에 한 번씩 적용된다.
4. 추가한 부모가 아직 자식이 없을 때는 기존 기본 ROI 표시를 사용한다. 실제 연결된 자식이 있으면 부모 색과 이름표를 표시한다. 목록은 부모/자식을 명시한다.
5. 존재하는 부모에 새 ID의 자식을 추가하므로 순환·자기 참조가 생기지 않는다. 일괄 생성은 RoiCreation의 ParentIndex가 같은 배치의 선행 항목만 가리키도록 검증하며 실패 시 전체 상태·이력·순서를 유지한다.
6. 복사는 모든 새 ID를 먼저 만든 후 내부 ParentId를 매핑한다. 부모가 자식보다 늦게 생성된 그룹도 복사할 수 있다. 복사 루트는 독립 루트이며 기존 ROI에 붙지 않는다.
7. 그룹화는 유효 선택 루트와 모든 자손의 외곽을 감싸되 선택 루트만 새 그룹에 연결한다. 모든 루트가 같은 기존 부모를 가지면 그룹도 그 부모 아래에 둔다. 부모가 다르면 새 그룹은 루트다. 공간 안의 미선택 독립 ROI는 유지한다.
8. 그룹 해제는 선택한 그룹만 제거하고 가장 가까운 남은 조상에 연결한다. 여러 부모의 자식을 모았던 그룹을 해제할 때 과거 부모를 추측하지 않는다. **그룹화 Undo**가 원래 관계와 선택을 정확히 복원한다.
9. 부모 삭제는 기존처럼 전체 자손을 연쇄 삭제한다. 부모와 겹치는 독립 ROI는 삭제하지 않는다. 이미지 교체·필터·레시피와 공통 Undo/Redo 정책은 유지한다.

이 결정은 ADR-002/005/010/011/012의 자동 포함·자동 재부모화 설명을 대체한다. 해당 문서의 기존 실행 결과는 당시 정책의 역사적 기록이며 새 성능 측정값이 아니다.

## 검증

- 프로젝트 이동만 적용한 단계: 기존 331개 테스트 PASS.
- 최종 전체 회귀: Core 182 + 앱/WPF 45 + Canvas 87 + Algorithm 25 = **339개 PASS**, skip 0. `artifacts/canvas-hierarchy-tests.log`와 프로젝트별 TestResults/canvas-hierarchy.trx.
- Release 솔루션 빌드: 경고 0, 오류 0. `artifacts/canvas-build.log`.
- 실제 DX12 debug smoke: RTX 3060 Laptop GPU, VRAM 5.9 GiB, Budget 5.1 GiB. 부모/자식 버튼·네이티브 그리기·겹침 독립·연쇄 삭제·Undo/Redo·기존 도형·필터·미니맵·8K 라이브 시작/정지 확인. `artifacts/canvas-hierarchy-runtime/artifacts/smoke.log`와 `explicit-hierarchy-app.png`.
- 추가 경계: 부모 밖 자손의 이미지 경계 제한, 부모 삭제 중 자식 폴리곤 초안 실패와 이력 보존, 중첩 그룹 동시 해제, 생성 순서와 다른 부모 연결의 복사, 일괄 생성 잘못된 참조의 원자성.
- 최종 `pwsh -File scripts/Format-Code.ps1 -Verify`: PASS, exit 0. WPF 디자인타임 로더가 외부 컨트롤을 읽도록 Canvas Debug 참조 어셈블리를 먼저 준비한다. `artifacts/canvas-format-verify.log`.
- 최종 배포 smoke: PASS, exit 0. RTX 3060 Laptop / driver 581.83 / 6144 MiB / 실행 budget 5.1 GiB. `artifacts/canvas-publish-runtime/exit-code.txt`와 하위 `artifacts/smoke.log`. 배포 DLL·셰이더·문서/라이선스 경로 검증.
- 변이 검증: AddChild의 부모 연결을 임시 제거하면 신규 테스트 5개 실패. 즉시 원복 후 전체 339개 PASS. `artifacts/hierarchy-mutation.log`. [기계 판독 검증 요약](../data/canvas-hierarchy-validation-2026-10-04.json).
- 물리 마우스 전 과정, 실제 카메라/촬영 파일, 24K 실파일·대량 부하의 성능 재측정은 NOT_RUN. 기존 실측을 이번 정책에서 재측정한 것으로 표현하지 않는다.

## 설계 검토

독립 읽기 전용 리뷰와 주 작업 리뷰에서 확인했다. 승인된 설계 원칙 예외는 없다.

| 원칙 | 검토 |
|---|---|
| SRP | HWND/GPU/영상은 Canvas, UI 명령·검사는 앱, 계층·이력은 Core로 분리. ROI 생성 VM 코드는 partial 파일에 모음 |
| OCP | 기존 영상·viewport 계약과 도형 전략 유지. 명시적 RoiCreation으로 배치 입력 표현 |
| LSP | 기존 소스/필터 구현과 취소·소유권 테스트 보존. Canvas 단독 87개 테스트로 기존 계약 검증 |
| ISP | viewport와 프레임 게시 계약을 분리해 앱에서 소비 |
| DIP | Canvas→Core, 앱 조립→Canvas/Algorithm. 알고리즘과 캔버스 사이 참조 없음 |
| Information Expert | 계층 순회와 부모 지정은 EditorSession/RoiHierarchy, 경계는 기하에 유지 |
| Creator | 세션이 새 ID·순서·부모를 원자적으로 생성. GPU 자원 생성·반환 소유자는 유지 |
| Controller | 버튼·단축키·포인터가 같은 Core 명령을 사용. VM은 검색/계층 계산을 하지 않음 |
| Low Coupling | 라이브러리는 앱 VM/패널을 참조하지 않음. C# namespace 유지와 assembly 경계 구별 |
| High Cohesion | Canvas 관련 파일·셰이더·테스트를 함께 이동. 검사 adapter는 앱에 유지 |
| Polymorphism | 프레임/타일·필터·도형 전략의 기존 변형점 유지. 계층에 불필요한 전략 인터페이스 추가 없음 |
| Pure Fabrication | 이력·GPU 수명·타일 worker를 별도 책임으로 유지 |
| Indirection | 앱 조립과 IViewportSurface/IFramePresenter가 외부 렌더러 연결을 담당 |
| Protected Variations | DX12/NetVips/OpenCV 변경은 Canvas 안에, 공급자 변화는 VisionAlgorithm과 앱 adapter 안에 격리 |
