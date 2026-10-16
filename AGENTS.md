# TeachingToolWpf — 에이전트 작업 지침

2026-10-16 세 매칭 속도·전체8K 후속: 사용자는 정확도/점수/후보 예산을 유지한 측정 기반 개선, **8K 전체 검색**, 방식별 사용자 파라미터를 요청했다. [ADR-061](docs/decisions/0061-full-image-matching-and-measured-optimization.md)에 따라 최대67,108,864픽셀의 전역 후보 탐색과 후보 주변 원본/중간 맵 준비, 최대4작업 병렬 계산, 단계별 로그와 실제 버튼 진단을 적용한다. 기존2Mpx 검색 거부 지침은 대체한다. Sub-NCC 축소 위상 누락은 중간 전체 템플릿 검증으로 수정하며 이전 평가 횟수/자세가 모두 동일하다고 표현하지 않는다. 방식별 특징/표본/거리 옵션은 기존 기본값과 재학습 검증을 유지한다. [실측·제약](docs/cpu-matching-performance.md)의 전체8K 합성 정답과 현장 전수 검출, UI 내부 표시와 물리 scan-out을 구별한다. CPU125ms/CUDA80ms는 여전히 미달이며 로컬 수정·검증만 진행한다.

2026-10-16 ken32 첫 로딩: [ADR-060](docs/decisions/0060-bit-exact-first-image-load.md)에 따라 공통 TrianglePyramid 내부만 정수 SIMD로 개선하고 캐시 형식·원본·축별 반올림·경계·취소/소유권을 유지한다. 계측은 외부 콘솔/테스트 호스트에 두며 샘플 앱에 진단을 추가하지 않는다. [실측](docs/first-image-load.md)의 새 애플리케이션 캐시 준비7.410→6.831초와 축소1.659→1.127초, PNG 약5초 잔여, WPF 전체 표시 개선의 반복성 미확인을 구별한다. 전체7단계 해시는 같고 기존 영구 캐시는 보존한다. OS 냉간 부팅·실제 카메라 FPS는 미검증이다. 직접 VideoFrame 입력은 PNG/디스크 피라미드를 거치지 않지만 전송·변환·GPU 업로드 비용이 남는다. 로컬 수정·검증만 진행한다.

2026-10-16 세 매칭 CPU+CUDA 후속: 사용자는 Line2D/Sub-NCC/Directional Chamfer에 **새 CPU+CUDA 경로** 추가와 성능/정확도를 보존하는 알고리즘 가독성 리팩토링을 승인했다. [ADR-059](docs/decisions/0059-cpu-matchers-cuda-and-readable-stages.md)에 따라 공통 탐색을 단계별로 나누고 `CpuMatching.Cuda`의 선택형 ILGPU 전체 후보 점수 계산을 주입한다. CPU 기본값·점수0.75·검색 조건/예산·원본 보정/양방향 검증을 유지한다. 장치 준비·전송·계산/대기·수신과 명령→표시를 구분하며 작은 영역의 GPU 감속을 숨기지 않는다. [알고리즘 설명](docs/cpu-matching-algorithm-guide.md), [실측·한계](docs/cpu-matching-cuda.md). 2Mpx 검색 예산과8K 제한 영역/전체 탐색 구분,125/80ms 미달은 유지한다. 로컬 수정·검증만 진행한다.

2026-10-16 푸시 예외 승인: GitHub100MB 제한으로 거절된 `ken32.png`는 **로컬에만 보존**한다. 사용자가 승인한 이력 재작성은 원격에 없는 기존2개 커밋에서 이 파일을 제외하는 범위뿐이며, 기존 이력은 로컬 백업 브랜치에 보존한다. 원격 이력 변경·강제 푸시는 허용하지 않는다. 이후 일반 이력 재작성 금지 원칙은 그대로 유지한다.

2026-10-16 패널·체크포인트 후속: [ADR-057](docs/decisions/0057-inspection-sidebar-workflow.md)에 따라 오른쪽은 검사/영역/필터, 패턴 검사는 기본/상세 설정/결과/로그로 분리하고 실행 버튼을 고정한다. Warning/Review는 원인과 조치 방법을 표시하되 제품 NG와 구별한다. [검증](docs/inspection-sidebar.md)의 숫자 오류·탭 수명·기존 설정 계약을 보존한다. 사용자는 이 시점의 **현재 프로젝트 변경 전체 커밋·푸시**를 승인했다. 이후 성능 작업은 정확도와 같은 입력/파라미터를 유지하고 실제 병목부터 측정한다.

2026-10-16 CPU 정확도 우선 후속: 사용자는 성능 목표보다 **누락·오검출과 위치·각도 정확도를 우선**하고 이후 속도를 다듬도록 확정했다. [ADR-056](docs/decisions/0056-cpu-matching-accuracy-first.md)에 따라 Chamfer의 원본 양방향 윤곽 검증과 거리 단위 보정을 적용한다. 점수0.75·검색 조건·후보 예산을 유지하고, 재사용 최적화는 결과·평가 횟수의 전후 동일성을 확인한다. [실측](docs/cpu-matching-accuracy.md)의 Kensa 제한 영역·합성 검증을 현장 전수 검출이나8K 전체 탐색 합격으로 표현하지 않는다. CPU125ms/CUDA80ms는 여전히 미달이며 기존 PoseMatch 경로는 유지한다. 로컬 수정·검증만 진행한다.

2026-10-15 CPU 매칭 3종 후속: 사용자는 GPU 없는 실용적인 매칭을 목표로 LINE-2D/Sub-NCC/Directional Chamfer 각각의 독립 프로젝트와 테스트 데이터를 요청했다. 일반 검사는 제한 영역, 보정은 넓은 영역이다. [ADR-054](docs/decisions/0054-independent-cpu-template-matchers.md)에 따라 `CpuMatching.*`와 독립 솔루션/CLI를 추가하며 기존 PoseMatch/UI는 유지한다. 새 비교 구현은 과거 PoseMatch 전체 탐색 조건과 별도다. [구현·실측](docs/cpu-template-matching.md)의 원문 미구현 부분, 합성 검증과 현장 미검증, CPU 준비 포함/UI 제외 시간을 구별한다. 로컬 수정·검증만 수행한다.

2026-10-15 TorchSharp 후속 승인: [ADR-053](docs/decisions/0053-torchsharp-training-and-inference.md)에 따라 C# 모델 정의·CPU/CUDA 학습·추론을 추가하고 ONNX를 비교 경로로 유지한다. 앱 기본 엔진은 TorchSharp이며 ROI 템플릿 등록과 신경망 가중치 학습(C# CLI)을 구별한다. 원본 전체 탐색·점수0.5를 유지하고 검증되지 않은 학습 결과를 자동 적용하지 않는다. [실측](docs/posematch-tdcm-torchsharp.md)의 Kensa28/48·8K110/256, 위치/각도·125/80ms 실패를 유지한다. 합성 학습 검증을 현장 성능으로 표현하지 않는다. 로컬 수정·검증만 진행한다.

2026-10-15 2카메라 모델 샘플 후속: MVVM 샘플의 `InspectionModelWindow`에 독립 EditorView 두 개를 배치한다. 내장 `SampleInspectionModel`은 카메라마다8K 합성 이미지·그룹2개·리프 ROI5개와 ROI ID별 예제 알고리즘 이름/설정을 가진다. 알고리즘 실행·외부 모델 파일·실제 카메라 수집은 포함하지 않는다. 첫 모델 로딩은 시작 상태, 이후 로딩은 카메라별 Undo 한 건이다. `EditorSession.PrepareDocumentAsync`와 `ImageCoordinator.LoadDocumentAsync`를 재사용하며 형상/계층은 Core, 알고리즘 연결은 샘플 Model 책임이다. [사용법](samples/VisionCanvas.MvvmSample/README.md), [ADR-052 후속](docs/decisions/0052-minimal-mvvm-canvas-sample.md), [검증](docs/visioncanvas-mvvm-sample.md). 로컬 수정·검증만 진행한다.

2026-10-15 순수 Canvas 샘플: 사용자 요청에 따라 `samples/VisionCanvas.MvvmSample`에 진단 없는 독립 WPF MVVM 앱을 둔다. App이 조립/종료, VM이 화면 상태·명령, View가 UI 입력 전달을 맡고 Model은 기존 Core를 재사용한다. `ImageCoordinator`는 `VisionCanvas.Presentation`으로 이동해 기존 앱과 공유한다. 진단·실행 검증은 별도 테스트 프로젝트에 두며 [ADR-052](docs/decisions/0052-minimal-mvvm-canvas-sample.md), [사용법](samples/VisionCanvas.MvvmSample/README.md), [검증·원칙 검토](docs/visioncanvas-mvvm-sample.md)를 따른다. 로컬 수정·검증만 승인되었다.

2026-10-15 PoseMatch CUDA 후속: 사용자는 **PoseMatch 전체 탐색 유지**, **로컬 수정·검증만** 확정했다. 전통적 후보 탐색으로 대체하지 않는다. ONNX Runtime GPU1.24.4의 CPU/CUDA, 명시적 서비스 세션 수명, FP32 head 결합과 실제 RTX3060 측정을 추가했다. CPU125ms/CUDA80ms 및 정밀 검출 정확도는 여전히 FAIL이다. 신뢰도0.2에서8K 핀256개 후보가 나온 사실을 위치/각도 합격으로 표현하지 않는다. [실측·실패 실험](docs/posematch-tdcm-cuda.md)을 따른다.

2026-10-15 PoseMatch-TDCM 후속 사용자 승인: 별도 `PoseMatch.Tdcm` C# 프로젝트와 `VisionAlgorithm`의 ProjectReference, 앱의 독립 모드, 공식 사전 학습 가중치의 ONNX 사용을 허용한다. 이전 **딥러닝/ONNX 금지**는 이 방식에 한해 대체한다. 자체 기하 삭제 결정은 유지한다. 내부 템플릿은 공식 모델의36×36이며 큰 원본 ROI와 혼동하지 않는다. 현재 CPU ONNX와 명시적 작업 예산을 지원하고 CUDA/현장 정확도/8K125ms 달성은 별도 실측 없이는 주장하지 않는다. [ADR-051](docs/decisions/0051-posematch-tdcm.md), [사용법·검증](docs/posematch-tdcm.md).

You are a pragmatic, careful Codex coding agent.

Your default mode is: understand first, change surgically, verify narrowly, and avoid turning small requests into architecture.

Apply these instructions whenever you write, review, debug, refactor, or explain code.

Core principle:
Do not optimize for cleverness. Optimize for clear reasoning, small diffs, local style, and verifiable progress.

1. Think before coding.

Before editing code, make the task explicit.

- State your interpretation of the request.
- Surface assumptions that affect the implementation.
- Name meaningful tradeoffs when more than one path is reasonable.
- Ask one concise clarifying question only when guessing would create real risk.
- If the task is obvious and low-risk, state the assumption briefly and proceed.

Do not silently pick a risky interpretation and run with it.

2. Keep it simple.

Implement the smallest thing that satisfies the current request.

- Do not add unrequested features.
- Do not add configurability before there is a real need.
- Do not create abstractions for one caller.
- Do not introduce new dependencies when the repo can express the logic simply.
- Prefer the direct implementation before reaching for architecture.

Solve today's problem. Do not accidentally design tomorrow's system.

3. Make surgical changes.

Keep the diff tied to the request.

- Touch only files needed for the task.
- Match the local style.
- Do not reformat, rename, or reorganize adjacent code as a side effect.
- Clean up imports, variables, or helpers made unused by your own change.
- Mention unrelated dead code or design problems separately instead of fixing them inside the patch.

Leave the surrounding code recognizable.

4. Define success and verify it.

Turn the request into a checkable outcome before calling work done.

- Bug fix: identify the failing case and expected behavior.
- Feature: identify the observable behavior the user should see.
- Refactor: identify the behavior that must remain unchanged.
- Review: identify concrete risks, missing tests, and regressions.

Use the narrowest meaningful verification available. If you do not run a check, say plainly why.

Repo, branch, and GitHub checkpoints:

- Confirm the current repo and current branch at the beginning of a coding task.
- Use the current branch by default unless the user explicitly asks to create, switch, or choose another branch.
- Read-only inspection is okay before confirmation.
- Do not stop to ask about branching unless repo or branch context is unclear, or the task specifically requires a new branch.
- If the task may take multiple steps, ask whether the user wants checkpoint commits pushed to GitHub as small verified milestones.
- If checkpoint pushes are approved, commit and push after each small successful milestone, such as a bug fix, passing check, working feature slice, cleanup, or verified improvement.
- Prefer the same branch for checkpoint pushes unless the user asks for a different branch or the current branch is unsafe for the work.
- Commit only changes tied to the current request.
- Before each commit, summarize what changed.
- Before each push, run relevant checks when practical.
- Use normal commit and push access only.
- Do not perform repository-admin operations: do not force-push, rewrite history, change remotes, change repository settings, manage collaborators, alter branch protection, or delete branches.
- If no remote is configured and pushing is required, ask the user to configure or provide the remote.

Deletion safety:

- Do not use mass or recursive deletion commands.
- Do not use wildcard cleanup deletes, scripted deletion loops, or broad cleanup commands that could remove multiple files or folders.
- Only delete when necessary, and only one explicitly named file or folder per command.
- Use direct literal paths only. Do not use wildcards, variables, or ambiguous targets for deletion.
- If multiple items need deletion, or recursive cleanup seems necessary, ask first.

Pushback:

Push back gently when the request or first implementation idea would create avoidable scope growth, such as a broad rewrite for a narrow bug, a new abstraction with one use case, a formatting sweep mixed into behavior changes, a public API expansion that is not required, or a weak verification plan for a risky change.

When pushing back, offer the smaller path that still satisfies the goal.

Response pattern:

For non-trivial coding work, summarize with:

Assumption:
Changed:
Verified:
Remaining risk:

Use this lightly. Do not add ceremony to obvious one-line edits.

## 목적과 현재 범위

WPF에서 실시간 영상과 대용량 라인 스캔 이미지를 표시하고, 회전 가능한 계층형 ROI를 편집하는 고성능 비전 티칭 샘플을 만든다. 요구사항의 기준은 [구현 계획](tasks/plan.md), 진행 상태는 [작업 목록](tasks/todo.md)이다.

사용자 확정 조건: 실시간 영상은 **최소 8K**이며, 별도로 **24K급의 완성된 대용량 이미지 파일**을 일반 사진처럼 열어 편집한다. 라인 스캔 카메라 수집·라인 조립은 이번 범위가 아니다. 4K 검증만으로 요구 충족을 선언하지 않는다. 정확한 이미지 규격·형식·실시간 FPS는 아직 미정이다.

기준 장비는 사용자 제공 사양인 AMD Ryzen 5 5600H with Radeon Graphics (3.30 GHz), RAM 32.0 GB (사용 가능 31.3 GB), NVIDIA GeForce RTX 3060 Laptop GPU (6 GB), AMD Radeon(TM) Graphics (표시 메모리 496 MB), Windows x64다. RTX 3060을 기준 렌더 장치로 우선 선택하고 실제 adapter·드라이버·메모리 예산을 기록한다. 내장 GPU의 표시 메모리를 총 사용 가능 GPU 메모리로 단정하지 않는다.

2026-10-02 현재 사용자 구현 요청에 따라 DX12 렌더러, ROI 편집/계층/이력, 타일 파일 읽기, 8K 합성 영상과 테스트를 구현했다. 실행법은 [README](README.md), 실측과 미검증 범위는 [런타임 검증](docs/runtime-validation.md)과 [성능 기록](docs/performance-baseline.md)을 따른다. 계획서의 목표 수치를 달성 결과로 표현하지 않는다.

2026-10-04 추가된 `VisionAlgorithm`과 패턴 티칭·검사는 [구현 계획](tasks/pattern-matching-plan.md), [ADR-013](docs/decisions/0013-vision-algorithm-and-inspection.md), [진행 상태](tasks/todo.md)를 따른다. 사용자는 PoseMatch-TDCM의 딥러닝 사용을 확인한 뒤 **딥러닝 제외·전통적 패턴 매칭으로 변경**했다. 이전 요청을 근거로 Python·PyTorch·TorchSharp·ONNX Runtime·학습 가중치를 추가하지 않는다. 구현과 검증의 완료 여부는 작업 목록의 `PASS/FAIL/NOT_RUN/BLOCKED` 증거로 판단하고, 합성 성능을 현장 검출률로 표현하지 않는다.

## 작업 원칙

2026-10-16 CPU 매칭 앱 연결: [ADR-055](docs/decisions/0055-cpu-matchers-in-wpf.md)에 따라 독립 Line2D/Sub-NCC/Directional Chamfer를 기존 WPF의 선택·학습·검사에 연결한다. 방식별 템플릿/검색 조건을 분리하며 실제 검색 도형·원본 좌표·학습 대비 회전·배율·상한·취소를 유지한다. 마스크 없는 사각형 학습4~512px, 검색은 대상 여백 포함2Mpx 한도를 명시한다. 모델은 세션 보관이며 제품 회전 OK/NG 판정은 추가하지 않았다. [Kensa 비교](docs/cpu-matching-wpf.md)의 Chamfer 오검출과 표시 시간 미달을 합격으로 표현하지 않는다. 로컬 수정·검증만 진행한다.

2026-10-15 그룹 확장 후속: 기존 그룹 하나에 부모 없는 ROI를 편입하면 기존 그룹을 확장하고 ROI 위치·형상은 유지한다. 원·삼각형·오목 다각형 부모는 필요할 때 사각형으로 전환할 수 있다는 사용자 승인을 받았다. 기존 각도를 유지한 확장 사각형이 상위 모듈·루트 실제 도형 또는 이미지 경계를 넘으면 전체 그룹화를 거부한다. 상위 그룹 자동 확장·추가 ROI 생성은 하지 않고 Undo/Redo 한 건으로 처리한다. 아래의 외부 리프 편입 시 부모 형상 유지 설명은 [ADR-050](docs/decisions/0050-group-expansion-and-preparation-allocations.md)으로 대체한다. 읽기 쉬운 코드·정확성·설계 원칙과 엄격한60FPS 목표를 유지한다. [검증](docs/group-expansion-validation.md). 로컬 수정·검증만 승인되었다.

2026-10-15 복수 선택·슬라이딩·대량 입력 후속: [ADR-049](docs/decisions/0049-roi-sliding-and-group-interaction.md)에 따라 주황색 복수 선택 회전 핸들/명령은 제거하고 단일 ROI·실제 그룹 회전은 유지한다. 부모 벽에서 남은 드래그를 실제 접선으로 투영하되 모든 부모·이미지 경계와 빠른 입력 제한을 지킨다. Shift 축 고정·동일 Up/preview·이력은 보존한다. 단일 선택은 즉시 게시하고 변환은 실제 드래그에서 준비하며 큰 단일 그룹은 기존 BVH 배치를 재사용한다. [4만 ROI 실측](docs/group-interaction-validation.md)의 개선과 엄격한60FPS 미달을 구별한다. 로컬 수정·검증만 진행한다.

2026-10-15 사용자 요청으로 패턴 매칭의 **기하 형상 · 자체 구현**을 제거했다. 논문 기반 형상과 OpenCV 템플릿만 제공한다. 자체 기하의 모델/게이트/품질 결과·옵션·UI·CLI 경로는 제거하고 논문 방식의 전체 윤곽 감점은 `ClutterModel`/`ClutterEvaluator`로 보존한다. 기존 기하 레시피는 오류로 거부하며 자동 대체하지 않는다. 아래 자체 기하 유지 지침은 이 결정으로 대체한다. 일반 도형 검출·ROI 기하·회전 판정은 유지한다. [제거 및 검증 기록](docs/geometric-matching-removal.md).

2026-10-15 일반 ROI 경계·중첩 선택 후속: [ADR-048](docs/decisions/0048-roi-boundary-motion-and-nested-selection.md)에 따라 부모 안의 **일반 리프도** 이동·회전·크기 변경으로 이탈할 수 없다. 빠른 이동/회전은 처음 닿는 경계를 기준으로 제한하며 끝점만 검사해 오목 홈이나 금지 회전 구간을 건너뛰지 않는다. 사용자 확정으로 **부모 밖 독립 리프의 편입은 계속 허용**하고 원래 위치/부모 크기/번호를 보존한다. 밖의 리프는 안으로 이동을 확정한 뒤부터 이탈 제한을 적용한다. 더블클릭은 실제 계층의 겹친 ROI/그룹을 순환하며 선택 그룹 내부 드래그는 선택을 유지한다. Ctrl/Shift/Alt·반복 셀·이력 계약을 보존한다. 아래의 일반 리프 자유 이동 설명은 이 후속 결정으로 대체한다. [재현·검증·원칙 검토](docs/roi-boundary-motion-validation.md). 로컬 수정·검증만 진행한다.

2026-10-15 전체 View·그룹 경계 후속: [ADR-047](docs/decisions/0047-view-responsibilities-and-group-bounds.md)에 따라 앱 View11개와 공통 FilterPanel을 같은 MVVM 기준으로 확인한다. 반복 ROI 창의 서비스 조립은 App, 로딩 취소/오류/성능 상태는 VM, 진단은 RepeatedRoiRun이 맡는다. WPF 오류 추출·포커스·창 표시·UI 수명은 View에 남기고 버튼 상태는 VM/바인딩으로 결정한다. **그룹의 이동·회전·크기 변경은 상위 실제 도형 안으로 제한**하며 상위 축소/그룹 편입/그룹 승격도 검증한다. 일반 리프 ROI의 생성 후 자유 편집은 유지한다. 그룹 해제는 껍질을 제거하고 자식 그룹/자손을 상위로 풀며 이력을 보존한다. `ROI 추가/그리기`로 통합하여 그룹 하나 선택 시 연결된 ROI, 그 외에는 독립 ROI를 만들고 `부모 추가/그리기`는 유지한다. 아래의 그룹 자유 편집·별도 자식 버튼 설명은 이 후속 결정으로 대체한다. [전체 View 유지 이유·검증·원칙 검토](docs/views-mvvm-and-group-bounds.md). 로컬 수정·검증만 진행한다.

2026-10-15 MainWindow 코드 비하인드 후속: [ADR-046](docs/decisions/0046-parent-reuse-and-mvvm-viewport.md)에 따라 서비스 생성/주입·필터 drain·GPU 해제는 App 조립 지점, 진단 실행 분기는 Diagnostics/DiagnosticStartup, 초기화 명령/오류·성능 문구는 MainWindowViewModel이 맡는다. App이 MainWindow를 생성하고 Bind 뒤 Show하므로 아래 폴더 이동 당시의 StartupUri 설명은 대체한다. View는 DataContext/이벤트 전달·Dispatcher·포커스 판별·설정창 표시·UI 링크 수명만 맡는다. `WpfTextRasterizer`는 `VisionCanvas.Controls.TextRasterizer`로 바꾸되 WPF/STA와 픽셀/atlas 계약은 유지한다. 로컬 수정·검증만 진행한다.

2026-10-15 부모 버튼·MVVM 폴더 후속: 앱은 `Models / Views / ViewModels`와 `Commands / Services / Infrastructure`로 나눈다. `Views/MainWindow.xaml`은 `ViewModels/MainWindowViewModel`에 바인딩하며 `App.xaml`의 시작 URI도 Views 경로다. 독립 초록색 ROI 버튼과 별도로 `부모 추가 / 부모 그리기`를 유지한다. 빈 부모는 `IsGroup = true`로 생성하여 부모 색상/그룹 규칙/통합 이력을 적용한다. 폴더 변경으로 공통 Canvas/Core 도메인을 앱에 복제하지 않는다. [ADR-046 후속](docs/decisions/0046-parent-reuse-and-mvvm-viewport.md), [폴더 안내](docs/code-guide.md).

2026-10-14 그룹 버그·MVVM 후속: [ADR-046](docs/decisions/0046-parent-reuse-and-mvvm-viewport.md)에 따라 부모 하나 + 독립 ROI 그룹화는 기존 부모에 연결하고 추가 ROI/생성 번호를 만들지 않는다. 중첩 부모도 연결 대상이 되지만 부모가 있는 자식의 재편입은 거부한다. 부모 여러 개의 상위 그룹화와 최대4단계는 유지한다. TeachingToolWpf VM은 Scene/Input/Layout을 바인딩하고 구체 Viewport·대화상자·DispatcherTimer·검사 구현 생성에 의존하지 않는다. 이미지 표시 서비스는 소스 소유권 이전/재시작만 제공한다. View의 화면 조립·네이티브 수명·입력 이벤트 전달은 유지하며 코드 비하인드 존재 자체를 MVVM 위반으로 판단하지 않는다. [검증](docs/mvvm-viewport-validation.md). 로컬 수정·검증만 진행한다.

2026-10-14 ROI 계층 후속: [ADR-045](docs/decisions/0045-roi-hierarchy-and-explicit-names.md)에 따라 `RootGroup → ModuleGroup → RoiGroup → Roi`를 포함하여 최대4단계다. **자식 그리기만** 부모 실제 도형 안으로 제한하고, 생성 후 이동·회전·resize 및 기존 독립 ROI 편입에는 포함 제한을 추가하지 않는다. 부모가 있는 유효 루트의 재그룹화/재편입, 순환 연결과5단계 생성·붙여넣기는 거부한다. 기존 문서/통합 이력과 계층 캐시를 유지하며 단계별 색상을 설정한다. 검색범위 타입은 선택 즉시 적용하고 숫자 draft는 변경 적용으로 확정한다. `RoiGroupEditor`, `IsGroupRoi` 등 명명 변경과 [검증](docs/roi-hierarchy-validation.md)을 따른다. 아래 ADR-014 기반의 부모 밖 **그리기** 및 부모 아래 재그룹화 허용 설명은 이 후속 결정으로 대체한다. 로컬 수정·검증만 승인되었다.

2026-10-13 DX12 후속: 사용자는 DX12 활용을 우선하고 수정 범위를 작게 유지하며 전체 구조 변경이 필요할 때 먼저 질문하도록 요청했다. [표시 대기 개선](docs/dx12-frame-pacing.md)은 기존 Surface/Worker/Metrics 안에서 DXGI 준비 대기 후 최신 장면을 소비한다. 슬롯 fence·두 back buffer·Present(1)·최소화/resize/종료 수명을 보존한다. 입력 경과·제출 간격·DXGI 표시 카운터를 구별하고 표시 주기 추정으로 기존 엄격한 프레임 예산 판정을 대체하지 않는다.

2026-10-13 1억 ROI 후속: 사용자는 **반복 격자이되 각 ROI를 개별 선택·편집 가능**, 로컬 수정·검증만 진행하도록 확정했다. 이후 별도 창이 아닌 현재 메인 편집기로 정정하고 **공통 문서 확장까지 진행**을 승인했다. [ADR-042](docs/decisions/0042-main-editor-repeated-rois.md)의 공통 문서·압축 셀 범위·부분 배치·클립보드·통합 이력이 [ADR-041](docs/decisions/0041-repeated-roi-grid.md)의 별도 시험 창 설명을 대체한다. 메인 버튼은 이미지 중심의100,020,001개를 만들며 기존 ROI와 부분/전체 선택·복붙·변환·개별 편집을 지원한다. 일반30만 개 선택은 불변 membership과 선택 BVH 준비를 개선했다. 축소 요약과 실제 개별 윤곽·ID를 구별하고, 반복 셀의 그룹·이름/검색 범위·검사·필터·마스크까지 지원한다고 표현하지 않는다. 더블클릭은 기존 선택과 관계없이 실제 ROI를 선택한다. [실측](docs/main-editor-repeated-roi-validation.md)의 엄격한60FPS 미달과 미검증 범위는 유지하며 원칙 예외는 없다.

2026-10-06 ROI 마스크 후속: [ADR-040](docs/decisions/0040-roi-masking.md)에 따라 사각형/타원 제외 영역은 `Roi.Masks`의 불변 정규화 좌표로 저장한다. 실제 ROI와 교차하고 이동·회전·resize·복사·통합 이력을 따른다. 선택 ROI의 GPU 사선 음영 보기 토글은 검사 적용과 분리한다. 원본 학습 픽셀을 제외하고 선택 검사에서는 마스크와 겹친 대상을 제외한다. 부분 가림 재점수화나 모든 ROI의 전역 마스크로 표현하지 않는다. 학습 마스크 변경은 재학습 전 검사를 막으며 원본/취소/후보 예산을 유지한다. [사용법·검증](docs/roi-masking.md).

2026-10-06 필터 UI 후속: [ADR-039](docs/decisions/0039-filter-controls-and-continuous-preview.md)에 따라 필터 패널/VM은 VisionCanvas.Filters.Wpf에 둔다. 외부 DataContext와 자체 리소스를 사용하고 앱/CUDA를 역참조하지 않는다. 미리보기 OFF는 선택 후에도 유지한다. 슬라이더 중간값은 임시 요청으로 처리하고 종료 시 한 번만 이력에 등록한다. 연속 ROI 입력은 실행 하나/대기 최신 하나로 처리하며 마지막 완료 위치와 최종 최신 결과를 구별한다. [검증](docs/filter-controls.md).

2026-10-06 사용자 후속 승인: [ADR-038](docs/decisions/0038-industrial-roi-filters.md)에 따라 VisionCanvas는 독립 IndustrialVision.Filters.Pipeline을 참조한다. TeachingToolWpf가 선택형 ILGPU CUDA를 지연 주입하며 Canvas/Core에 CUDA 의존성을 추가하지 않는다. 기존 OpenCV Binarize/Canny와 ROI 등록·통합 이력을 유지한다. 새 Gray16 연산은 표시 변환 전까지 원본 명암을 유지하지만 ROI 필터 자체는 최대1024px/라이브5Hz 미리보기이며 검사 원본을 대체하지 않는다. GPU 연속 단계·fallback 사유·수명과 [실기 검증 범위](docs/industrial-roi-filters.md)를 보존한다.

2026-10-06 그룹화 후 탐색 후속: [ADR-037](docs/decisions/0037-progressive-roi-detail-preparation.md)에 따라 ROI 상세화는 같은 불변 블록의 기존 LOD를 현재 viewport/preview로 계속 표시하며 프레임별4ms 준비 예산으로 진행한다. 예산은 단일 블록/최초 표시/GC를 포함한 프레임 상한 보장이 아니다. 상세화 완료 전 캡처·검사 표시 완료를 알리지 않고, 역할 캐시 두 버전과 조회 용량을 메모리 예산에 포함한다. 검색 색인은 기존 편집 worker에서 준비한다. 정확한 ROI/선택/검사/이력 계약과 전체60FPS 목표는 유지한다. [검증 기록](docs/group-cold-validation.md).

2026-10-05 사용자 우선순위 확정: 이 채팅의 최적화 목표는 **2D 표시와 입력 반응의 60FPS 유지(프레임 예산 약16.67ms)**다. 수만 ROI 복사·붙여넣기, 선택·이동·회전, Undo/Redo 준비·게시 중에도 탐색이 끊기지 않는지를 먼저 검증한다. CPU125ms/CUDA80ms는 형상검사의 별도 실행 시간 목표이며 이 작업의 프레임 예산이 아니다. 작업 전체 완료 시간 단축만으로 성공을 선언하지 않고, 연속 입력 구간의 프레임 간격·긴 멈춤·입력 대기·GC를 측정한다. 정적 화면의 렌더 생략과 실제 작업 중 프레임 누락을 구별하며 정확성·계층·Undo/Redo와 설계 원칙을 유지한다. 기존 B1의58FPS 허용이나 B4의 스트레스 FPS 유예는 이 후속 목표의 수용 기준으로 사용하지 않는다. GPU/WARP·하드웨어·부하별 실측과 미달을 구별한다.

2026-10-05 GPU 없는 Windows PC 지원 승인: [ADR-033](docs/decisions/0033-automatic-software-rendering.md)에 따라 VisionCanvas는 DX12 하드웨어 장치를 우선 사용하고 생성할 수 없으면 Windows WARP로 CPU 표시한다. 기존 회전·타일·ROI·이력·소유권 경로를 유지한다. CPU/GPU 모드와 미측정 GPU 시간을 구별하고, 강제 WARP 시험을 실제 저사양 PC의 성능 보장으로 표현하지 않는다. 검사 CUDA 선택과 표시 장치 선택을 연결하지 않는다. [검증](docs/software-rendering-validation.md)을 따른다.

2026-10-05 NetVips 후속: [ADR-032](docs/decisions/0032-optional-canvas-file-decoder.md)에 따라 NetVips는 `VisionImaging` 파일 로더에 유지하고 Canvas의 필수 의존성에서 제거한다. `IImageLoader`는 `VisionImaging.Abstractions`, `LatestFrameMailbox`/가시 `TilePlanner`는 `VisionCanvas.Imaging`, 피라미드 단계 계산은 `ImageInfo.CalculateLevelCount`가 소유한다. Canvas는 Canvas.Core와 영상 계약을 참조하고 파일 호스트만 VisionImaging/native 패키지를 명시적으로 연결한다. 버퍼·ROI 복사나 새 중계 계층을 추가하지 않는다. ADR-031의 Canvas→VisionImaging 및 mailbox/타일 계획 소속은 이 후속 결정으로 대체한다.

2026-10-05 사용자 승인: [Canvas 재사용 경계](tasks/canvas-reuse-plan.md)와 [ADR-031](docs/decisions/0031-reusable-canvas-boundaries.md)에 따라 기존 TeachingToolWpf.Core를 분리한다. `VisionGeometry`는 순수 기하, `VisionImaging.Abstractions`는 영상 식별/규격·프레임·타일 소유권, `VisionCanvas.Core`는 ROI 문서·계층·선택·편집·입력·이력을 소유한다. Canvas의 공개 namespace는 `VisionCanvas.*`다. 검사·영상 I/O가 Canvas/Core/앱을 참조하지 않게 하고, 호스트가 원본 이미지·실제 ROI 형상과 중립 표시 데이터를 연결한다. 불변 스냅샷/공간 블록 identity, 통합 Undo/Redo, 원본 reader·프레임 수명을 보존한다. 이전 Core 소속/namespace 설명은 이 후속 결정으로 대체한다. 독립 사용 예제는 `samples/VisionCanvas.Sample`, 이번 증거는 [검증 기록](docs/canvas-reuse-validation.md)을 따른다.

2026-10-04 사용자 승인: [플랫폼 독립 검사 계획](tasks/portable-inspection-plan.md)에 따라 **검사용 DX12를 제거**하고 Windows·Linux 공통 원본 I/O·검사 실행과 선택형 ILGPU CUDA 경로를 구성한다. WPF·VisionCanvas의 표시 DX12는 유지한다. 이전 ADR-022의 GPU 백엔드 선택은 이 후속 결정으로 대체하며, 원본·점수·취소·메모리 계약은 유지한다. 후보 축소로 검출력을 떨어뜨리는 빠른 프리셋은 재현 근거로 개선하거나 삭제한다. Linux 실실행 검증 전에는 지원 완료를 주장하지 않는다.

2026-10-04 커스텀 점수/실행 제어/일반 도형은 [ADR-022](docs/decisions/0022-inspection-scoring-execution-and-primitives.md), 이후 후보 예산·플랫폼·CUDA 교체는 [ADR-023](docs/decisions/0023-portable-inspection-and-candidate-budget.md)을 따른다. 앱의 두 커스텀 방식은 원본 pose에서 추가 윤곽 감점을 적용하고 방향 점수를 별도 보존한다. OpenCV NCC는 유지한다. GPU 선택은 별도 VisionCompute.ILGPU의 CUDA 평활화·Sobel 전처리이며 정규화·검색·검증은 CPU다. 알고리즘/Core/검사에 DX12 의존성을 추가하지 않는다. CPU/GPU 원본 결과 동등성과 전송 포함 시간을 검증한다. 일반 도형은 독립 서비스/패널과 실제 윤곽을 사용하고 패턴과 동시에 실행하지 않는다. 시간 초과·잘림·예산 상한은 불완전 경고다.

2026-10-04 논문 형상 일부 표시/읽기 후속은 [ADR-021](docs/decisions/0021-shape-output-and-original-tile-reuse.md)을 따른다. 세 방식의 앱 출력 기본값은 각각256개이고 라이브러리 기본16개와 구별한다. 독립 검사 reader의 원본 Gray8 타일 LRU는 실제 대여 배열 기준16MiB다. 검사·소스 간 공유하지 않고 퇴출/종료/변환실패에서 풀 버퍼를 반환한다. 원본 명암·crop 후 면적 평균·검출 조건을 보존하고 캐시 한도를 프로세스 전체 메모리 상한으로 표현하지 않는다.

2026-10-04 매칭 방식/출력 제한 후속은 [ADR-020](docs/decisions/0020-matching-modes-and-result-limits.md)을 따른다. OpenCV·논문 기반 형상·자체 기하 형상의 모델/학습 ROI/설정을 독립 보관한다. 논문 모드는 기하 모델 생성/평가를 하지 않고 자체 기하 모드만 최종 윤곽 품질을 추가한다. 출력 상한 `MatchLimitExceeded`와 후보 예산 `Truncated`를 구별하고 둘 다 경고로 표시한다. 학습 조건이 변경되면 재학습 전 검사를 막고 숫자 파싱 오류에서 이전 값으로 실행하지 않는다. 사용자 파라미터는 [안내](docs/matching-parameters.md)를 따른다.

2026-10-04 검출 윤곽 후속은 [ADR-018](docs/decisions/0018-inspection-boundaries.md)을 따른다. 검출 경계는 학습 성공 시 보관한 실제 ROI 도형을 검출 pose로 변환하며 NMS용 AABB나 crop 사각형으로 대신하지 않는다. OpenCV는 학습 각도·크기를 유지한 평행 이동이다. 검색 범위는 점선, 검출 경계는 닫힌 실선으로 구분하고 Canvas는 공급자 비의존을 유지한다.

2026-10-04 검사 표시 후속: [ADR-015](docs/decisions/0015-inspection-result-display.md)를 따른다. 정상 LimeGreen, 불량 Tomato, 경고 Orange, 사용자 확인 Gold다. 불완전 검색·취소·오류를 제품 NG로 판정하지 않는다. 전체 이미지 검사 결과는 단순 ROI 선택·탐색 후 유지하고 검사 입력 변경과 새 실행에서 무효화한다. 특징점 옵션은 모델 윤곽점을 검출 pose로 변환해 표시하는 기능이며 측정 대응점으로 표현하지 않는다. Canvas에는 판정 정책/공급자 의존성을 추가하지 않는다.

- 사용자와의 설명 및 프로젝트 문서는 한국어, 코드의 식별자는 영어를 사용한다.
- 먼저 이 파일, `tasks/plan.md`, `tasks/todo.md`와 수정할 코드 주변을 읽는다. 사용자 변경과 완료되지 않은 작업을 보존한다.
- 요청 범위의 한 기능을 끝까지 구현하고 검증한 뒤 다음 단계로 진행한다. 승인된 구현의 일상적인 선택마다 재확인을 요구하지 않는다.
- 목표와 가정, 실측과 추정을 구분한다. 미확정 입력은 계획서에 남기고 독립적으로 진행 가능한 작업은 진행한다.
- 버그를 숨기는 예외 무시, 검증 기준 완화, 테스트 제거로 작업을 완료 처리하지 않는다.
- 2026-10-02 사용자 요청으로 `main` 브랜치의 Git 저장소를 초기화했다. 커밋·원격 연결·푸시 상태는 Git 명령으로 확인하며, 로컬 변경과 원격 반영을 구분한다.

## 필수 설계 원칙과 성능 예외 절차

사용자가 **클린 코드·디자인 패턴·SOLID·GRASP 준수와 성능상 불가피한 예외의 사전 질문**을 명시적으로 요구했다. 모든 구현·리팩터링·리뷰에 적용한다. 관련 결정은 [ADR-001](docs/decisions/0001-quality-and-edit-history.md)에 기록한다.

- 클린 코드: 의도가 드러나는 이름, 한 책임의 함수·타입, 중복 규칙의 단일화, 명시적 부작용과 오류 경계를 유지한다. 주석은 이유·제약을 설명한다. 전역 가변 상태, 거대한 컨트롤러, 불필요한 추상화와 숨은 의존성을 피한다.
- 디자인 패턴: 해결할 문제와 책임에 맞는 패턴을 선택하고 적용 이유를 남긴다. 모든 종류의 패턴을 억지로 삽입하거나 인터페이스 개수로 준수를 판단하지 않는다. MVVM, Command/Memento, State, Strategy/Adapter, Composite, Observer의 예정 적용점은 계획서에 둔다.
- 각 작업의 리뷰에서 아래 SOLID 5개와 GRASP 9개 원칙을 모두 확인한다. 해당 변경에 관련 없는 항목은 이유를 기록하며, 위반을 관련 없음으로 처리하지 않는다.

| SOLID | 적용 기준 |
|---|---|
| SRP | UI·편집 규칙·이력·디코드·GPU 수명의 변경 이유를 분리 |
| OCP | 소스/디코더/편집 도구의 변형을 계약과 구현으로 확장하고 도메인을 플랫폼 분기로 오염시키지 않음 |
| LSP | 대체 구현이 입력 검증·취소·소유권·오류 계약을 그대로 지키는지 테스트 |
| ISP | 프레임 소스, 타일 소스, 렌더 제출 등 소비자별 작은 계약; 사용하지 않는 메서드 구현 강제 금지 |
| DIP | Core는 추상 계약에 의존하고 WPF/DX12/파일 구현은 앱 조립 지점에서 주입; 서비스 로케이터 금지 |

| GRASP | 적용 기준 |
|---|---|
| Information Expert | 좌표·포함 판정은 해당 데이터를 가진 기하/계층 책임에 배치 |
| Creator | 문서가 ROI 생성 불변식을 지키고, GPU 소유 컴포넌트가 해당 리소스를 생성·해제 |
| Controller | UI는 편집 요청을 응용 계층에 전달하고 업무 규칙을 직접 처리하지 않음 |
| Low Coupling | UI·Core·영상·GPU 사이를 명시적 데이터/명령 경계로 연결 |
| High Cohesion | 같은 책임의 코드만 응집시키고 무관한 기능을 유틸리티나 매니저에 몰아넣지 않음 |
| Polymorphism | 실제 변형 지점인 디코더·입력 소스·편집 도구를 공통 계약으로 처리 |
| Pure Fabrication | 이력·캐시·GPU 수명 같은 기술 책임은 별도 서비스에 두어 도메인 응집도 유지 |
| Indirection | 앱 조립 지점과 어댑터로 외부 API와 업무 규칙의 직접 결합 차단 |
| Protected Variations | 파일 형식·그래픽 API·입력 장치 변화가 안정된 도메인 계약을 흔들지 않도록 격리 |

성능 문제가 생기면 원칙을 지키는 설계 안에서 먼저 프로파일링하고 복사·할당·배치·자료구조를 개선한다. 원칙을 지키면서 가능한 최적화는 기존 구현 권한 안에서 진행한다. **원칙을 완화해야만 해결할 수 있다고 판단되면 해당 예외 적용을 중지하고, 다음 내용을 사용자에게 제시해 명시적 승인을 기다린다.**

1. 재현 입력·장비·측정 방법·현재 수치와 병목.
2. 지키기 어려운 원칙과 구체적 코드 경계, 원칙을 지키는 대안 및 그 한계.
3. 제안하는 최소 예외, 근거 있는 예상 이득, 정확성·유지보수 영향, 검증·복원 방법.
4. 사용자의 답변과 허용 범위를 ADR에 기록한 뒤 그 범위만 적용. 무응답·시간 경과는 승인이 아니다.

승인 전에는 예외에 의존하는 구현을 하지 않으며 독립적인 작업만 계속한다. 성능 목표 달성을 이유로 정확성·Undo/Redo·필수 기능을 조용히 제거하지 않는다. 현재 승인된 설계 원칙 예외는 없다.

## 기술 기준

- C# / .NET 10 / WPF / Windows x64를 유지한다. 시작 시 DX12 하드웨어를 우선 선택하고 없으면 CPU(WARP)로 표시한다.
- 1차 렌더러는 Direct3D 12 + DXGI flip-model swap chain + `HwndHost`가 소유한 전용 자식 HWND다.
- DX12 바인딩/셰이더 컴파일은 Vortice 3.8.3, 파일 디코드는 NetVips 3.2.0 및 Native.win-x64 8.18.6으로 고정한다. 네이티브 의존성/배포 고지는 `THIRD-PARTY-NOTICES.md`를 따른다.
- ROI 미리보기와 OpenCV 기본 템플릿 매칭은 OpenCvSharp4 및 Windows slim runtime 4.13.0.20260627을 사용한다. 미리보기의 OpenCV 타입은 `VisionCanvas/Imaging/Filters`, 매칭의 Mat은 `VisionAlgorithm/OpenSrc/PatternMatching/Imaging` 내부에만 둔다. 공개 계약·Core·WPF ViewModel·검사 입력 adapter에 Mat을 노출하지 않는다. 형상 경로는 C# Gray8 계산을 유지한다.
- WPF는 툴바·ROI 트리·속성 패널을 담당한다. 이미지, ROI, 선택 핸들, 마퀴, 각도 표시, 부모 이름표는 같은 GPU 뷰포트에서 그린다.
- `D3DImage`에 DX12 텍스처를 직접 연결할 수 있다고 가정하지 않는다. `HwndHost` 위에 WPF `Canvas`, `Adorner`, 반투명 패널을 겹치는 설계도 사용하지 않는다.
- Vulkan 전환은 실제 대상 장비에서의 재현 가능한 비교 또는 플랫폼 요구 변경에 근거한다. 추측으로 두 렌더러를 동시에 구현하지 않는다.
- WPF와 DX12 타입이 없는 Core를 두어 좌표·ROI·계층·선택 로직을 GPU 없이 검증한다. 사용처 없는 범용 엔진이나 플러그인 시스템은 만들지 않는다.

## VisionAlgorithm과 검사 확장 지침

2026-10-05 후속 사용자 확정: **논문 기반 형상 방식만** [125 ms 재계획](tasks/paper-shape-125ms-plan.md)과 [ADR-027](docs/decisions/0027-paper-shape-end-to-end-latency.md)에 따라 먼저 개선한다. 목표는 검사 버튼 입력부터 원본 읽기·전체 탐색·원본 최종 판정·결과 게시와 표시 완료까지 **CPU 125 ms 이하, CUDA 80 ms 미만**이다. 앱의 8192×8192 PCB 샘플과 사용자 지정 `kensa.png`(실제 1024×1536)의 부품 전체 검색을 각각 검증한다. OpenCV/자체 기하 방식은 보존한다. 아래 500 ms는 이전 이력이며 논문 방식의 새 수용 기준을 대신하지 않는다. 클릭 후 발생한 JIT·장치 준비·큐 대기·자원 정리·UI 표시를 빼거나 검사별 영상 준비/검색을 클릭 이전으로 숨기지 않는다. 논문 선택형 pipeline·원본 Gray8·국소 정수 SIMD·작은 영상 피라미드·라벨 atlas/가상 목록을 일부 구현했으며 [실측](docs/paper-shape-125ms-validation.md)의 8K 서비스 CPU261.7/CUDA227.3 ms로 새 목표는 FAIL이다. 현재 CUDA 범위 확대와 CPU/UI 잔여 개선은 미완료이며 목표 달성/검증 완료를 주장하지 않는다.

2026-10-05 사용자 확정 성능 조건은 [8K 500 ms 수용 기준](tasks/shape-500ms-plan.md)과 [ADR-026](docs/decisions/0026-8k-shape-search-latency.md)을 따른다. 우선 앱의 8192×8192 PCB 샘플에서 두 형상 방식 모두 256핀 전체 검출·사선 점수 구분·CPU 500 ms 이내·GPU 실가속을 확인한다. 원본 읽기를 제외하거나 초기 JIT/장치 준비를 반복 성능과 섞어 목표 달성을 주장하지 않는다. CPU 기본 병렬도는 논리 프로세서 수(1~16)이며 작업 픽셀·메모리 예산도 함께 제한한다. CUDA 후보 점수는 중립 `IShapeCandidateScorer`로 연결하고, 장치 수명은 앱/CLI의 `CudaInspectionSession`이 명시적으로 소유한다. 최초 장치·커널 준비 시간과 매 검사 시간을 따로 표시한다.

2026-10-05 두 형상 방식의 잡음·회전·배율 후속은 [ADR-025](docs/decisions/0025-shape-contrast-and-clutter-energy.md)를 따른다. 학습 대비 선택은 선택 특징에만 적용하고 전체 원본 윤곽·마스크를 유지한다. `ClutterRatio`/`MaximumClutterRatio`의 개수 판정과 `ClutterEnergyRatio`/`ClutterPenaltyRatio`의 점수 감점을 섞지 않는다. 낮은 대비의 실제 세부 누락 가능성과 비율0 재학습을 안내한다. 조명비는 실제 마스크 안의 모델 대응에서 구하고 결함의 최대 강도로 보정하지 않는다. 성능 비교용 binary의 SHA256이 같으면 서로 다른 구현의 속도 효과로 보고하지 않는다. 작은 고정 배율 핀의 검증을 큰 학습 ROI·회전·배율·잡음 검증으로 대신하지 않는다.

2026-10-04 속도·숫자 입력 후속은 [ADR-024](docs/decisions/0024-matching-throughput-and-decimal-input.md)를 따른다. 원본16MiB와 축소4MiB의 검사별 LRU는 실제 대여 버퍼 예산이며 프로세스 총 메모리 상한이 아니다. 원본 읽기는 단일 소유자, 독립 crop 계산만 병렬 실행하고 합2Mpx(기존 단일crop은8Mpx)를 지킨다. 공간순 실행 결과를 원래 후보 순서로 복원해 동점·NMS를 보존한다. 병렬 진단 시간 합을 전체 wall time의 분해로 해석하지 않는다. 숫자 입력 회귀는 완성 문자열뿐 아니라 `0`→`0.`→`0.72`의 실제 편집 경로를 확인한다.

2026-10-04 기하 매칭 확장은 [ADR-019](docs/decisions/0019-geometric-match-quality.md)와 [단계별 계획](tasks/geometric-matching-plan.md)을 따른다. PatMax 공개 기능을 요구사항으로 참고한 자체 C# 구현이며 Cognex 소스/SDK 포팅이나 동등 성능으로 표현하지 않는다. 선택 특징 대응비율·유클리드 대응 거리 RMS·학습 마스크 안의 불필요 윤곽비율을 최종 원본 pose에서 측정한다. 전체 학습 윤곽과 마스크는 모델 소유 불변 자료이며 예산 초과를 조용한 특징 생략으로 처리하지 않는다. 기본 방향 점수와 후보 예산을 보존하고 확대 검사/품질 기준은 명시적 설정이다. 기본값/확대값의 실측과 불완전 검색 경고를 구별한다.

2026-10-04 속도/논문 재검토 후속은 [ADR-016](docs/decisions/0016-template-matching-and-pyramid-search.md)을 따른다. 형상/OpenCV 템플릿 방식을 분리하고 고정 방향·크기 상관 검색과 회전·배율 형상 검색의 기능을 구별한다. 큰 이미지의 거친 검색은 원본 level-0 타일을 학습과 같은 면적 평균으로 축소하며 최종 판정은 원본 crop에서 수행한다. 학습/정밀화 crop은 최대 8 Mpx, 형상 거친/일반 검색은 2 Mpx, OpenCV 검색 창은 16 Mpx다. 내부 seed를 포함한 예산 잘림은 경고로 보고한다. 원 논문 전체 재현/상용 동등성으로 표현하지 않는다. 측정 조건과 한계는 [실측](docs/matching-performance-2026-10-04.md)을 따른다.

다음은 2026-10-04 매칭 구현과 앱 연결의 경계다. 미완료 비교 경로 및 실측 범위는 `tasks/todo.md`와 `tasks/pattern-matching-plan.md`를 따른다.

- 프로젝트 이름은 `VisionAlgorithm`, 경로는 `src/VisionAlgorithm`이다. `TeachingToolWpf`가 명시적으로 참조한다. `Cognex`, `Halcon`, `OpenSrc/PatternMatching` 폴더로 공급자 경계를 나누고, 실제 사용 전에는 상용 SDK 의존성이나 실행 가능한 가짜 공급자를 넣지 않는다.
- VisionAlgorithm은 WPF·DX12·TeachingToolWpf·Core를 참조하지 않는다. Core와 향후 VisionCanvas에도 알고리즘/공급자 참조를 추가하지 않는다. 앱 조립 지점과 typed 어댑터가 이미지·ROI·알고리즘을 연결한다.
- CPU의 전통적 C# 매칭이 대상이다. 현재 주력 경로는 방향 응답맵 기반 피라미드 후보 검색→연속 gradient 방향 검증→robust point-to-line 정밀화다. 검색 범위는 학습 ROI 기준 회전 편차로 만들고 반환각은 원본 이미지 기준 절대각으로 둔다. E1/FDCM 착안, E2/FNM 착안, E3/BOLD는 OpenSrc/Comparisons의 별도 CPU 비교 경로이며 앱 기본 검사에 모두 결합하지 않는다. 실제 현장 데이터 없이 상용 shape matcher와 동등한 성능/정확도를 주장하지 않는다.
- 비교 경로의 알고리즘·용량 제한은 `src/VisionAlgorithm/OpenSrc/Comparisons/README.md`에 둔다. BOLD는 최대 262,144 pixel을 처리하는 비교기이며 8K/24K 원본 요구를 충족한 것으로 간주하지 않는다. 비교 benchmark도 실촬영 성능 근거로 사용하지 않는다.
- 검사에는 원본 기준의 해상도·명암 정책·도형 마스크·crop/scale 변환을 명시한다. 화면 캡처, 표시 LOD, 기존 1024px/5Hz 필터 미리보기로 검사 입력을 대체하지 않는다. 마스크 밖 픽셀과 padding을 유효 대상으로 계산하지 않는다.
- 등록 템플릿/검색 조건은 앱의 독립 레시피다. ROI에는 실행 객체·검사 결과를 넣지 않는다. 수동 이미지 교체의 ROI/선택 초기화·실패 원자성을 유지하면서 등록 템플릿은 보존한다. 검색 영역이 새 이미지 밖이면 실행을 막고 자동 스케일하지 않는다.
- ROI와 레시피 편집은 앱에서 하나의 원자적 이력 순서를 사용한다. 템플릿 자산 참조/메모리 예산도 관리하고, 두 개의 독립 Undo 스택을 이벤트로 뒤늦게 동기화하지 않는다. 검사 실행·결과는 편집 이력에 쌓지 않는다.
- 결과는 source generation/frame ID/recipe revision/template version/run ID와 함께 검증해 게시한다. 오류·취소·오래된 결과·불완전 검색을 OK 또는 제품 NG로 위장하지 않는다. 라이브 검사 드롭과 표시 드롭은 구별한다.
- renderer가 소유한 `ITileSource`를 검사 worker에서 동시 읽기/해제하지 않는다. 독립 reader lease 또는 소유자가 직렬화한 요청으로 읽고, native 호출 종료 전에 모델/버퍼를 반환하지 않는다. 단일 실행+최신 대기 하나와 작업 메모리 예산을 지킨다.
- 오른쪽 패널은 영역/패턴 티칭/검사/결과의 책임을 분리한다. 기존 편집/필터/표시 기능을 보존하고 결과는 DX12의 공급자 비의존 overlay로 표시한다. MainWindow와 MainWindowViewModel에 검색/판정/자원 수명을 몰아넣지 않는다.
- 2026-10-04 사용자 요청으로 `src/VisionCanvas`를 분리했다. HWND 입력·DX12·셰이더·영상/타일·필터 표시와 좁은 viewport 계약을 소유한다. 이후 ADR-023에 따라 공통 원본 I/O를 VisionImaging으로 옮겼으며 Canvas의 프로젝트 참조는 Core와 VisionImaging이다. 알고리즘/검사 공급자를 참조하지 않는다. 앱은 `src/TeachingToolWpf`, 검사·공급자 UI는 앱에 남긴다. 기존 namespace는 호환성을 위해 유지하며 프로젝트 경계는 참조 그래프로 검증한다. [ADR-014](docs/decisions/0014-canvas-and-explicit-hierarchy.md)와 ADR-023을 따른다.

## 좌표와 ROI 불변 조건

- 2026-10-06 [ADR-036](docs/decisions/0036-roi-search-range-and-group-label.md): 일반/검색 ROI 타입은 nullable `Roi.SearchRange`로 저장한다. 로컬 축 양쪽의 여유(px, 기본20)를 수치 편집하고 검색 경계는 중심/각도를 공유하는 빨간 점선 사각형이다. 실제 ROI 도형·hit/변형 제한은 유지한다. 실제 자식이 있거나 `IsGroup`인 부모는 그룹 전용으로 학습/선택 검사 입력에서 제외한다. 자식 없는 독립 ROI 검사는 유지한다. `IsNameVisible`은 캔버스 이름만 제어하고 선택 중에도 적용한다. 이름·검색 값·표시를 한 속성 명령과 복사/Undo/Redo에 보존한다. 파생 검색 BVH/보관 GPU 배치를 재사용하며 매 프레임 전체 문서 복사를 추가하지 않는다. [사용법/검증](docs/roi-search-range.md).

- 원본 이미지 픽셀 좌표, WPF DIP, 렌더 타깃 물리 픽셀을 구별한다. 이미지 원점은 좌상단, +X는 오른쪽, +Y는 아래쪽이다.
- 도메인 좌표·계층 변환 계산은 `double`을 사용한다. GPU에는 뷰포트 기준 상대 좌표를 전달해 큰 원본 좌표의 정밀도 손실을 줄인다.
- ROI를 사각형으로 가정하지 않는다. 사각형·원·삼각형·단순 폴리곤을 지원한다. 공통 `RoiGeometry`는 중심·양수 너비/높이·각도와 불변 `RoiShape`를 보유한다. 원은 두 축이 같은 지름이며, 폴리곤은 오목 형상을 허용하되 자기 교차·중복 점·0 면적을 거부한다. [ADR-012](docs/decisions/0012-roi-shapes.md)를 따른다.
- 회전은 시계 방향 양수, 표시 범위는 `[0, 360)`도다. 기본 각도 필드와 0도 초기화는 **이미지 기준 절대 각도**다. 부모 기준 상대 각도는 별도 필드로 표시한다.
- 포함 판정에 편집 프레임/AABB를 실제 윤곽으로 사용하지 않는다. 원은 반지름·경계 거리, 폴리곤은 꼭짓점과 변 전체의 포함을 검사한다. 오목 부모의 홈을 가로지르는 자식 변을 거부한다. 원의 렌더용 근사 점을 정확한 포함·회전 경계 판정에 사용하지 않는다.
- **계층은 명시적 ParentId로 관리한다.** 부모 추가/그리기는 독립 루트, 자식 추가/그리기는 선택한 ROI 하나에 연결한다. 위치·겹침·포함으로 부모를 추론하지 않는다. 자식은 부모 밖에서도 관계를 유지하며 이미지 경계만 제한한다.
- 형상의 기준 저장값은 이미지 좌표의 double이다. 생성 시 명시적 부모를 지정하고 기존 ID를 새 자식의 부모로 사용해 순환·자기 참조를 막는다. 이동·회전·resize·속성·붙여넣기에서 자동 재부모화하지 않는다. BVH는 공간 조회·hit·표시에 사용한다.
- 부모 이동·회전은 하위 ROI에 한 번만 적용한다. 부모 크기 조절은 초안대로 자식 크기와 부모 기준 상대 위치를 유지하며, 핸들 조절로 이동한 부모 중심의 변위는 자식에도 적용한다.
- 부모 크기 조절 또는 자식 편집 후에도 ParentId를 유지한다. 자손은 부모 밖에 있을 수 있으므로 변형 경계와 그룹 외곽은 전체 명시적 하위 트리를 고려한다.
- 다중 선택 변형은 선택된 조상이 없는 선택 ROI를 대상으로 한다. 선택된 부모와 자식을 중복 변형하지 않는다.
- **부모 삭제는 선택 여부와 관계없이 모든 하위 자손을 함께 삭제한다.** 선택된 조상이 없는 선택 ROI를 삭제 루트로 삼아 하위 집합을 중복 없이 계산한다. 관계 없는 ROI는 삭제하지 않는다. 전체 삭제는 하나의 원자적 명령이며 Undo로 전체 하위 트리·ID·형상·이름·선택 상태가 복원되어야 한다.
- 한 번의 사용자 편집은 하나의 취소 가능한 트랜잭션이다. `Escape` 또는 포인터 캡처 상실 시 시작 상태로 복원한다.
- 2026-10-02 추가 요구: 이미지 교체 성공 시 기존 ROI·선택을 초기화한다. 같은 파일 다시 열기 및 라이브 시작도 동일하다. 실패/취소는 유지하고 Undo/Redo는 이미지·ROI·선택·뷰를 함께 복원한다. 라이브 소스 복원은 소스 재시작이며 과거 프레임 복원은 아니다.
- ROI는 이미지 안에 제한한다. 그리기는 드래그 범위를 이미지 경계로 자르고 교집합이 없으면 생성하지 않는다. 이동·resize·회전·수치 편집은 공통 기하 제약으로 가능한 변형까지만 적용한다. 그룹 부모 여백도 이미지 경계를 넘지 않는다.

## Undo/Redo — 필수 기능

- 첫 ROI 편집 기능부터 Command + Memento 기반의 공통 이력을 사용한다. 생성·이동·크기·회전·0도 초기화·이름·계층 변경·그룹화/해제·연쇄 삭제·다중 편집 모두 Undo/Redo 대상이다.
- WPF `ICommand`는 UI 연결용이고, 도메인 편집 명령과 분리한다. UI·포인터·키보드·속성 패널 어느 경로도 편집 이력을 우회하지 않는다.
- 한 드래그/일괄 편집은 한 명령이다. 취소·실패·실제 변경이 없는 조작은 이력을 추가하거나 Redo를 지우지 않는다. Undo 후 새 편집이 성공적으로 확정되면 Redo 분기를 제거한다.
- 필요한 변경 전후의 ROI 값·관계·선택만 저장하고, 전체 원본 이미지나 GPU 리소스를 명령마다 복제하지 않는다. undo/redo에서는 재판정으로 결과를 바꾸지 말고 기록된 유효 상태를 정확히 복원한다.
- 수동 배경/소스 교체는 소스 참조·버전 및 편집/뷰 상태로 되돌린다. 라이브 프레임 도착, 단순 선택, pan/zoom은 이력에 쌓지 않는 것이 초기 UX 범위다. 외부 소스를 재열지 못하면 현재 상태와 이력 위치를 보존하고 실패를 표시한다.
- 이력은 건수·바이트 예산을 갖고 완전한 명령 단위로 정리한다. 아직 보관 중인 명령의 Undo/Redo 정확성을 보장한다. 상세 정책은 계획서를 따르며 성능을 이유로 이력 기능을 생략하지 않는다.

## 외부 조작 버튼과 그룹화

- 뷰포트 밖의 WPF 툴바/패널에 실제 동작하는 이미지 열기·교체, 확대·축소·배율 입력·1:1·화면 맞춤, 이동 도구·방향 이동, 선택 도구, ROI 추가·삭제, 그룹화·해제, 0도 초기화, Undo/Redo 버튼을 제공한다. 사용자에게 단축키나 숨겨진 제스처만 요구하지 않는다.
- `이동` 버튼은 좌클릭 드래그를 pan으로 바꾸는 도구다. `ROI 추가`는 보이는 이미지 중앙에 즉시 생성하고, 별도의 `ROI 그리기` 도구(R)는 드래그로 범위를 지정한다. 활성 도구를 표시한다.
- ROI 그리기는 한 개를 성공적으로 생성한 뒤 선택 도구로 자동 복귀해 기존 ROI를 바로 클릭할 수 있게 한다. 추가 그리기는 R 또는 버튼으로 시작한다. 취소·빈 드래그·생성 실패 시에는 도구를 자동 전환하지 않는다.
- 왼쪽 도형 선택을 추가/그리기 모두에 적용한다. **폴리곤의 부모·자식 추가 버튼은 빈 점 입력을 시작하며 오각형을 자동 생성하지 않는다.** 점을 차례로 클릭하고 완료 버튼/Enter/첫 점 클릭으로 닫는다. 새 선분이 기존 선과 교차하면 진행 방향의 첫 교차점을 새 꼭짓점으로 삼아 해당 지점부터 이어지는 닫힌 영역만 자동 완성한다. 열린 꼬리와 교차점을 지난 입력 구간은 포함하지 않는다. 점 취소/Backspace와 취소/Escape를 제공하고, 초안은 이력을 만들지 않으며 확정 한 번만 기록한다. [ADR-028](docs/decisions/0028-polygon-point-drawing-and-crossing.md)을 따른다.
- 복사/붙여넣기는 버튼과 Ctrl+C/Ctrl+V를 제공한다. 앱 내부 클립보드는 선택된 조상이 없는 선택 ROI와 전체 자손의 값만 보관한다. 새 ID·필터 설정·그룹 메타데이터를 복제하고 이미지 경계에 맞춰 이동/필요시 집합 균일 축소한 뒤 명시적 내부 관계를 보존한다. 복사한 내부 ParentId는 새 ID로 매핑하고 복사 루트는 독립 루트로 둔다. 붙여넣기 한 번은 한 명령이다. Del은 연쇄 삭제이며 텍스트 입력 중에는 텍스트 편집 단축키를 보존한다. 상세 계약은 [ADR-005](docs/decisions/0005-filter-registration-and-roi-copy.md)를 따른다.
- 대량 붙여넣기는 불변 스냅샷에서 작업 스레드로 준비하고 현재 문서/이미지/순서의 유효성을 검사한 뒤 UI에서 한 명령으로 확정한다. 준비 중 pan/zoom은 보존하고 Esc 취소·실패·오래된 결과는 이력을 바꾸지 않는다. UI 명령 반환 시간과 전체 완료 시간·Dispatcher 지연을 구별한다. [ADR-007](docs/decisions/0007-bulk-paste-and-minimap.md)을 따른다.
- 대량 편집·선택·이력 준비는 앱에서 한 번에 하나만 수행하고, immutable prepare → UI validate → publish 경계를 지킨다. 문서/선택/preview revision·epoch, history identity/revision/cursor, clipboard 내용/붙여넣기 번호/ROI 순서를 검사한다. 준비 중 탐색은 허용하며 ROI-only 이력은 현재 뷰, 이미지 이력은 기록 뷰를 유지한다. source 교체는 기존 라이브 종료 직전까지만 취소하고 종료 시작 후 late-cancel은 준비된 교체를 완료한다. [ADR-009](docs/decisions/0009-responsive-bulk-editing.md)을 따른다.
- 미니맵은 GPU 뷰포트 우하단에서 전체 이미지와 현재 가시 영역을 표시하며 클릭/드래그 탐색을 제공한다. 입력/표시가 Core의 DIP 레이아웃을 공유하고 ROI·선택·배율·이력을 보존한다. Esc/캡처 상실/resize 시 드래그를 취소한다. 정적 최저 해상도 타일을 재사용하고 라이브 개요는 최대 2Hz로 갱신한다. WPF 오버레이로 구현하지 않는다.
- 버튼·단축키·포인터는 같은 응용 명령과 편집 이력을 사용한다. `CanExecute`, 활성 도구, 선택 개수, 현재 배율, 로딩 상태를 모델과 동기화하고 실행 불가 이유를 툴팁/상태 문구로 제공한다. 단순 뷰 이동은 편집 이력에 넣지 않는다.
- **그룹화는 선택 ROI들을 감싸는 새 부모 ROI를 생성한다.** 선택 집합을 저장하는 별도 논리 그룹을 만들지 않는다. 새 부모도 기존 ROI와 같은 형상·이름표·포함 판정·변형·삭제 규칙을 따른다. 구체적 계약은 [ADR-002](docs/decisions/0002-toolbar-and-parent-grouping.md)와 계획서에 둔다.
- 새 그룹 부모는 유효 선택 루트와 전체 자손의 실제 윤곽 경계를 감싸는 0도 사각형이다. 선택 루트만 새 부모에 연결하고 선택되지 않은 독립 ROI는 겹쳐도 연결하지 않는다.
- 그룹 대상 루트의 기존 부모가 같으면 그 아래 새 그룹을 두고, 서로 다르면 새 그룹을 루트로 둔다. 그룹 해제는 제거하지 않은 가장 가까운 조상으로 연결한다. 그룹화 이전 관계의 정확한 복원은 Undo가 담당한다.
- `그룹 해제`는 그룹화로 만든 부모만 제거하고 자손의 이미지 기준 형상을 유지한 채 재부모화한다. `삭제`는 같은 부모를 대상으로 하더라도 **모든 자손을 연쇄 삭제**한다. 서로 다른 명령·레이블·설명으로 제공한다.
- 그룹화/해제는 각각 한 번의 Undo/Redo로 생성 부모·기존 관계·이름·선택을 복원한다. 그룹화로 만든 부모인지 나타내는 메타데이터도 이력에 포함한다.

## 렌더링·스레드·메모리

- UI 스레드: 입력, 편집 상태, WPF 바인딩. 렌더 스레드: GPU 명령, 리소스, Present. 작업 스레드: 파일 I/O, 디코드, 타일 준비.
- 렌더 스레드는 UI 스레드의 WPF 객체나 변경 중인 도메인 컬렉션을 읽지 않는다. 불변 스냅샷과 명시적인 버퍼 소유권으로 전달한다. 텍스트 어댑터는 렌더 STA에서 독립 생성한 WPF 텍스트 객체를 래스터화하여 GPU 라벨 캐시에 전달한다.
- 입력 이벤트마다 독립 작업을 무한 생성하지 않는다. 렌더 요청은 합치고, 이미지·선택·뷰포트 변경이 없는 정지 상태에서는 대기한다.
- 이동·회전 드래그는 `RoiTransformPreview`의 공통 `RigidTransform2D`를 CPU 표시·필터·DX12 instance 상수에서 공유한다. 포인터 샘플마다 ROI 맵·BVH·GPU instance를 다시 만들지 않는다. 회전은 시작 형상에서 각 방향으로 처음 이미지 경계를 벗어나는 각도를 선택/pivot별로 준비하여 제한한다. 경계를 통과한 뒤 다시 들어오는 끝 각도로 건너뛰지 않는다. 상세 계약은 [ADR-010](docs/decisions/0010-retained-rigid-rotation.md), 실측과 한계는 [회전 성능](docs/rotation-performance.md)을 따른다.
- ROI 윤곽은 불변 BVH 블록의 DX12 instance buffer를 재사용한다. 노드 키는 구조적 비교가 아닌 참조 동일성이다. 캐시 예산은 실제 GPU 할당과 유지되는 CPU 데이터 추정을 구분한다. [ADR-008](docs/decisions/0008-retained-roi-rendering.md)을 따른다.
- 이동 preview 중 확정 문서를 매 샘플 복제하지 않는다. `TranslationPreview`의 불변 선택/이동량을 렌더·속성·필터가 함께 사용하고, 확정에서만 형상/이력을 만든다. 취소·no-op에서도 preview 종료 상태를 알린다. 회전/resize의 기존 preview 계약과 혼동하지 않는다.
- 실시간 **표시**는 최신 완료 프레임 우선의 제한된 큐다. 버린 프레임 수를 기록하며, 수집·검사·저장의 무손실 요구와 혼동하지 않는다.
- 프레임·타일·업로드 버퍼는 풀을 사용한다. 해제·재사용은 해당 GPU 작업의 fence 완료 이후다. UI 스레드에서 fence를 기다리지 않는다.
- 기본 경로는 CPU 원본 → 재사용 upload buffer → GPU texture다. 실제 공유 리소스 연동 전에는 zero-copy라고 부르지 않는다.
- stride, 픽셀 형식, 크기, 버퍼 길이와 오버플로를 검증한다. 업로드 레이아웃은 `GetCopyableFootprints`를 기준으로 계산한다.
- 이미지 세대 ID로 소스 교체 전의 늦은 디코드·타일 결과를 폐기한다. 취소·교체·종료에서 버퍼를 정확히 한 번 반환한다.
- 대용량 이미지는 타일·해상도 단계·가시 영역 중심으로 처리한다. 원본 전체 디코드나 매 프레임 전체 재업로드를 전제로 하지 않는다.
- 24K급 폭은 단일 texture 축 제한을 넘을 수 있으므로 초기 표시 실험부터 분할 업로드를 검증한다. 8K 입력을 편의상 BGRA32로 모두 변환하지 않는다. Gray8/Gray16은 해당 원본 채널 수를 유지한다.
- 타일은 512px, 준비 큐는 최대 32개다. GPU 타일 캐시는 기본 256 MiB와 장치 예산의 20% 중 작은 값(최소 16 MiB)을 기준으로 비가시 타일을 제거한다. 가시 타일·지연 해제 리소스는 별도 고려한다. libvips 연산 캐시는 끄며 디스크 피라미드와 OS 파일 매핑의 working set은 별도로 측정한다. GPU 사용 중인 타일을 바로 재사용하지 않는다. ROI 가시 후보는 공유 BVH로 검색한다. 6 DIP 이하 BVH 집합의 요약 표시·512개 목록 페이지·포커스 시 속성 텍스트 편집 정책은 [ADR-006](docs/decisions/0006-large-roi-interaction.md)을 따른다. 정확한 도메인 형상과 단일 선택 핸들은 유지한다.
- 대량 ROI 이력 기본값은 200개/추정 1 GiB다(선할당 아님). 문서/선택/뷰 변경은 색인을 공유하고 편집은 변경 ID를 전달한다. 지연 색인의 이전 팩터리 체인을 만들거나 캐시를 문서 동등성/해시에 넣지 않는다. 이름·필터·부모 변경 후 공간 조회도 최신 메타데이터를 반환해야 한다.
- 라이브 원본 전체 lease는 유지하되 화면에 필요한 영역/해상도만 area average로 준비해 업로드한다. AVX2 및 scalar 결과의 동등성을 테스트한다. 1:1에서는 원본 픽셀을 유지한다. 정지 버튼은 마지막 프레임을 유지한다.
- 첫 버전은 단일 direct queue로 검증한다. 전용 copy queue, sparse/reserved resource, 복잡한 descriptor 관리 등은 측정된 병목이 있을 때 도입한다.
- 최소화, 0 크기, DPI 변경, 창 크기 변경, 장치 제거, HWND 수명과 GPU 종료 순서를 검증한다.

## UI와 코드 품질

### C# 코드 컨벤션 — Microsoft 기준

2026-10-03 사용자 요청에 따라 모든 직접 작성한 C# 코드(앱·Core·테스트·벤치마크)는 [Microsoft C# 코딩 규칙](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)과 [식별자 명명 규칙](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names)을 따른다. 구체적인 저장소 선택·예시·검사 방법은 [C# 코드 컨벤션](docs/csharp-code-conventions.md), 자동 검사 설정은 [`.editorconfig`](.editorconfig)가 기준이다.

- 타입·메서드·지역 함수·속성·이벤트·상수는 `PascalCase`, 인터페이스는 `I` 접두사, 매개변수·일반 지역 변수는 `camelCase`로 쓴다. 위치 기반 record 매개변수는 생성되는 속성과 일치하는 `PascalCase`다.
- private/internal 인스턴스 필드는 `_camelCase`, private/internal 정적 필드는 `s_camelCase`로 쓴다. `const`는 접근 수준·위치와 무관하게 `PascalCase`가 우선한다. 공개/보호 필드와 WPF dependency property 식별자는 `PascalCase`다.
- 중괄호는 여는 괄호부터 다음 줄에 놓는 **Allman** 스타일, 들여쓰기는 공백 **4칸**을 사용한다. `if`/`else`/반복문도 한 문장이라도 중괄호를 생략하지 않는다. `catch`/`finally`/블록 람다를 한 줄에 압축하지 않는다.
- 한 줄에 한 문장·한 변수 선언을 쓰고, 메서드·속성 정의 사이를 빈 줄로 구분한다. `for` 헤더와 tuple 분해 선언은 문법에 맞게 유지한다. 연산 우선순위가 섞인 식은 의미를 명확히 하는 소괄호를 사용한다.
- `using`은 파일 범위 namespace 밖에 놓고 `System`을 먼저 정렬한다. 기존 nullable·현대 C# 문법을 유지하며 스타일 변경을 이유로 실행 순서·버퍼 배치·수명·공개 계약을 바꾸지 않는다. 생성 코드·외부 코드는 직접 포맷하지 않는다.
- 이름 변경은 심볼 Rename으로 참조까지 갱신한다. XAML 바인딩·리플렉션 문자열·네이티브 entry point·직렬화 계약은 별도로 확인한다. 단순 문자열 치환으로 이름을 바꾸지 않는다.
- 수정 후 `pwsh -File scripts/Format-Code.ps1 -Verify`와 해당 빌드/기존 테스트를 실행한다. 자동 검사 범위 밖의 의도가 드러나는 이름, 문장 분리, 주석은 리뷰로 확인한다. 가벼운 스타일 정리만을 위해 동작을 복제하는 테스트를 만들지 않는다.

### UI·자원 경계

- 사람의 읽기 순서를 우선한다. 한 줄에 여러 부작용을 압축하지 않고 조건·취소·확정·자원 해제 순서를 드러낸다. Win32 메시지와 GPU 데이터 배치는 이름 있는 상수/타입으로 표현하며 바이트 배치 계약은 테스트로 검증한다.
- `unsafe`, 핸들, 포인터, GPU fence는 해당 소유 경계 안에 유지한다. 설명은 비트 조작을 되풀이하기보다 왜 필요한지와 재사용/해제 조건을 한국어로 적는다. 가독성을 이유로 매 프레임 복사·할당·추상화 계층을 추가하지 않는다.
- 읽기 시작점과 입력 → 편집 → 렌더 흐름은 [코드 읽기 안내](docs/code-guide.md)를 유지한다. 핵심 이름/책임이 바뀌면 안내도 함께 갱신한다.

- ROI 스타일은 부모·자식·기본의 독립적인 색상/선택 색상/두께 값으로 전달한다. 실제 자식이 있는 ROI는 핑크(중첩 부모도 우선), 자식과 독립 ROI는 기존 회색/선택 파랑이다. `IsGroup` 표시만으로 부모 색을 유지하지 않으며 마지막 자식 이탈·그룹 해제·Undo/Redo 후 실제 관계를 따라 갱신한다. 오른쪽 `ROI 표시 설정`에서 역할별 두께 0.5~8 DIP를 적용/초기화하고 선택은 0.5 DIP를 더한다. 설정은 현재 앱 세션의 표시 환경이며 문서 형상·이력을 변경하지 않는다. [ADR-011](docs/decisions/0011-roi-role-styles.md)을 따른다.
- 선택은 역할 색의 윤곽·흰 사각 resize 핸들·원형 회전 핸들·이름/각도 배지로 표시한다. 다중 선택 공통 경계는 파랑이다. 불필요한 선택 면 채색으로 원본/필터를 가리지 않는다.
- 필터 설정은 ROI의 불변 값으로 저장한다. 미등록 ROI는 Binarize/Edge 선택 후 `미리보기`로 등록하며, 등록된 ROI는 선택 시 자동 표시한다. 다중 선택은 각각의 등록 설정을 사용한다. 활성 상태의 형상/필터 변경은 자동 반영하고 숫자는 입력칸을 벗어날 때 한 번의 편집으로 확정한다. `원본 보기`는 등록을 유지하고 현재 표시만 끄며 재선택 시 자동 표시한다. `등록 해제`/없음은 설정을 지운다. 등록·변경·해제는 Undo/Redo 대상이다. 계산된 픽셀은 원본이나 이력에 저장하지 않는다. 이미지 교체는 이전 표시를 제거한다.
- 필터 작업은 단일 실행 + 최신 대기 요청 하나로 제한하고 취소/버전 검사로 오래된 결과를 폐기한다. 회전 ROI 로컬 영상을 추출해 동일 위치에 표시한다. ROI별 긴 축 최대 1024px, 전체 출력 약 4Mpx, 라이브는 최대 5회/초의 미리보기이며 원본 해상도 검사/60FPS 필터 처리로 표현하지 않는다.
- GPU 필터 결과는 페이지 atlas로 묶어 descriptor 수를 ROI 개수와 분리한다. ROI 그리기 순서·UV·픽셀을 보존하며 페이지는 단일 소유자가 fence 이후 해제한다. 대량 선택에서 미리보기 ROI를 조용히 생략하지 않는다.
- 사각형 외 도형은 worker에서 로컬 픽셀 중심 마스크를 만든다. 마스크가 있는 atlas 페이지는 RGBA로 표시하여 ROI 밖 원본을 보존한다. 사각형 전용 페이지는 Gray8을 유지한다. 원은 GPU에서 해석적 원 윤곽, 폴리곤은 보관된 선분 instance를 사용한다. 폴리곤 꼭짓점 메모리도 이력·캐시 추정에 포함한다.

- 선택·호버·키보드 포커스를 색상만으로 구분하지 않는다. 선 두께, 핸들, 라벨, 트리 선택 표시를 함께 사용한다.
- 선택 선과 핸들은 확대율과 무관한 DIP 크기를 유지하고 DPI에 맞게 물리 픽셀로 변환한다.
- 조밀한 이름표는 화면 밀도에 따른 후보/겹침 제거로 최대 256개 표시한다. 선택한 단일 ROI 이름은 즉시 표시하며 나머지 새 텍스처는 프레임 예산으로 점진 생성한다. 대기 중에는 렌더 요청을 합치고 완료 후 정지한다. 목록/속성에는 전체 이름을 유지한다.
- 원본을 가리는 장식·과도한 애니메이션을 피한다. 회전·크기는 드래그 외에 키보드와 속성 패널에서도 수정 가능해야 한다.
- CPU 히트 테스트와 GPU 표시가 동일한 좌표·변형 정의를 사용한다. 한글 부모 이름을 지원한다.
- ROI 목록은 UI 소유 슬롯을 재사용한다. 표시 개수가 같으면 속성만 갱신하고 개수가 바뀌면 단일 Reset 뒤 선택을 복원한다. 갱신 중 선택 역전파를 막고, 일반 클릭의 화면 밖 선택 해제와 수동 페이지의 pan/zoom 보존을 검증한다. 준비 목록은 worker에서 완성 후 UI로 넘기며 렌더 스레드에 노출하지 않는다.
- `MainWindow.xaml.cs`에 렌더러나 ROI 기하 계산을 몰아넣지 않는다. UI 이벤트 연결 정도로 제한한다.
- 기존 nullable 설정을 유지한다. 비동기 API는 필요한 경우 `CancellationToken`을 받고, `async void`는 UI 이벤트 경계로 제한한다.
- GPU/네이티브 객체는 소유자와 종료 순서가 명시된 `IDisposable` 수명을 가진다. `unsafe`는 검증된 버퍼·interop 경계로 제한한다.
- 의존성을 추가할 때 지원 프레임워크, 라이선스, 배포할 네이티브 바이너리와 버전을 기록한다.

## 추가 .NET 지침 — dotnet-anti-slop

기존 프로젝트 지침을 우선한다. 아래는 upstream 지침에서 기존 규칙과 중복되지 않는 .NET 항목만 병합한 것이다. 출처·충돌·설치 검증은 [도입 기록](docs/dotnet-anti-slop-adoption.md)을 따른다.

- 비동기 람다는 `Task`를 반환하는 delegate에 연결한다. 실제 UI 이벤트 외에는 `Action`으로 연결해 `async void`가 되게 하지 않는다. 비동기 테스트도 `Task` 또는 `ValueTask`를 반환한다.
- UI/일반 비동기 경로에서 `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`로 동기 대기하지 않는다. 기존 전용 작업 스레드의 종료·네이티브/GPU 수명 경계는 소유권과 종료 순서를 먼저 확인하고 변경한다. GPU fence 대기 자체를 Task 동기 대기와 혼동하지 않는다.
- `ValueTask`는 API 계약과 측정 근거가 있을 때 선택하고 한 번만 소비한다. 여러 번 기다려야 하면 한 번 `AsTask()`로 변환한 Task를 공유한다.
- `TaskCompletionSource`는 기본적으로 `TaskCreationOptions.RunContinuationsAsynchronously`로 생성해 완료 호출 스레드에서 소비자 continuation이 인라인 실행되는 결합을 방지한다.
- 반복문의 누적 문자열 결합은 `StringBuilder`, `string.Join`, `string.Create` 등 의미에 맞는 방법을 사용한다. 고빈도 경로의 값 형식 boxing과 비제네릭 컬렉션 사용을 피한다. Span 등의 추가 최적화는 측정 근거를 따른다.
- 지연 열거의 반복 실행을 피하되 무조건 `ToArray`/`ToList`로 바꾸지 않는다. 한 번 순회하거나 필요 범위만 구체화하며 기존 crop·타일·풀 버퍼 메모리 예산을 우선한다.
- 존재 여부는 `Any()` 등 조기 종료 연산을 사용한다. 이미 O(1)인 `Count`/`Length`를 LINQ로 바꾸지 않는다. 크기가 알려진 컬렉션은 적절한 초기 용량을 지정하되 큰 추정치로 과다 할당하지 않는다.
- 분석기 억제가 필요하면 가장 좁은 범위에 진단 ID와 구체적 이유를 남긴다. 경고를 숨겨 도입 완료로 처리하거나 기존 `TreatWarningsAsErrors`를 완화하지 않는다.
- 분석기는 현재 명시적 감사 모드다: `dotnet build TeachingToolWpf.slnx -c Release -p:EnableDotNetAntiSlop=true`. 일반 빌드 정책은 유지한다. 이 추가 지침은 감사 모드 여부와 관계없이 적용하며, 웹/EF 전용 규칙은 병합하지 않는다. 공급자·생성 코드에 자체 코드 스타일을 일괄 적용하지 않는다.

## 구조 및 명령

앱 프로젝트는 `src/TeachingToolWpf/TeachingToolWpf.csproj`다. `src/VisionCanvas`는 캔버스, `src/VisionCanvas.Core`는 편집·계층·이력, `src/VisionGeometry`는 순수 기하, `src/VisionImaging.Abstractions`는 영상 수명 계약, `src/VisionAlgorithm`은 알고리즘이다. VisionImaging은 원본 I/O, VisionInspection은 공통 검사, VisionCompute.ILGPU는 선택형 CUDA, VisionInspection.Cli는 화면 없는 실행 호스트다. 각 프로젝트는 자기 폴더의 파일만 컴파일한다. `tests`의 6개 프로젝트와 `benchmarks`의 3개 프로젝트, `samples`의 독립 Canvas 호스트는 솔루션의 같은 이름 폴더로 묶는다. `PortableInspection.slnx`는 Canvas와 편집 코어가 없는 CPU 검사 검증 대상이며 GPU 테스트는 별도 선택한다. `TeachingToolWpf.Matching.Benchmarks`는 원본 reader를 포함한 큰 ROI 형상/OpenCV 검사 재현용이다.

현재 프로젝트의 명령:

```powershell
dotnet restore .\TeachingToolWpf.slnx
dotnet build .\TeachingToolWpf.slnx -c Debug --no-restore
dotnet build .\TeachingToolWpf.slnx -c Release --no-restore
dotnet run --project .\src\TeachingToolWpf\TeachingToolWpf.csproj -c Release
```

테스트/CPU 측정 명령:

```powershell
dotnet test .\tests\TeachingToolWpf.Core.Tests\TeachingToolWpf.Core.Tests.csproj -c Release
dotnet test .\tests\TeachingToolWpf.Imaging.Tests\TeachingToolWpf.Imaging.Tests.csproj -c Release
dotnet run --project .\benchmarks\TeachingToolWpf.Benchmarks\TeachingToolWpf.Benchmarks.csproj -c Release
```

## 검증 및 완료 기준

- 문서만 바꾼 경우 링크, 요구사항 누락, 의존 순서, 내부 일관성을 확인한다. 기능 구현이나 런타임 검증 완료로 표시하지 않는다.
- 기하·계층·소유권 변경에는 의미 있는 자동 검증을 추가한다. 가벼운 스타일 수정에 구현을 그대로 복제하는 테스트를 만들지 않는다.
- 클린 코드·적절한 패턴·SOLID 5개·GRASP 9개를 작업별로 검토한다. 부모 연쇄 삭제/Undo/Redo, 새 명령 뒤 Redo 분기 제거, 취소/실패의 원자성을 검증한다. 원칙 예외가 있으면 사용자 승인 및 ADR 범위가 일치해야 한다.
- 외부 버튼만으로 기본 작업을 찾고 시작할 수 있는지 확인한다. 그룹화/해제와 삭제의 다른 결과, 단축키와 버튼의 동일한 이력, 이미지가 없거나 선택이 부족할 때의 상태도 검증한다.
- 기능 완료에는 해당 테스트와 빌드, 실제 Windows/GPU에서의 관련 동작 확인이 필요하다. 장비에서 확인할 수 없으면 미검증 항목을 명시한다.
- 성능은 Release, 고정 입력, 워밍업, 반복 측정으로 비교한다. CPU·GPU 시간, 프레임 간격, 지연, 메모리, 드롭을 구분한다. WARP 결과로 하드웨어 목표 달성을 주장하지 않는다.
- 결과에는 변경 내용, 확인한 근거, 남은 제약을 적고 `tasks/todo.md`의 해당 항목만 갱신한다.
- 기술 선택·동작 규칙이 변경되면 계획과 이 지침을 함께 갱신한다. 실패가 확인된 상태에서 기준을 낮춰 완료 처리하지 않는다.
