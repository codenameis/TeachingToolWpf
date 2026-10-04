# 코드를 읽기 시작하는 순서

이 앱은 **입력 → 편집 상태 → 화면 그리기**로 읽으면 된다. ROI 규칙을 바꾸기 위해 DX12 포인터나 셰이더부터 이해할 필요는 없다. 편집 상태는 Core가, WPF 연결은 Presentation이, GPU 자원은 Rendering이 소유한다.

식별자와 괄호는 [Microsoft 기준 C# 컨벤션](csharp-code-conventions.md)을 따른다. `_viewport`는 인스턴스 필드, `s_procedure`는 정적 필드, `SizeInBytes`는 상수 또는 속성처럼 이름으로 역할을 구분한다. 지속 지침은 [AGENTS.md](../AGENTS.md), 검사 설정은 [`.editorconfig`](../.editorconfig)에 있다.

## 작업별 시작점

| 하려는 일 | 먼저 읽을 파일 | 알아둘 경계 |
|---|---|---|
| 버튼·단축키 연결 | [EditorViewModel](../src/TeachingToolWpf/Presentation/EditorViewModel.cs) | WPF 요청을 같은 편집 명령으로 전달 |
| ROI 생성·그룹화·삭제·Undo/Redo | [EditorSession](../src/TeachingToolWpf.Core/Editing/EditorSession.cs) | 문서 변경과 한 번의 편집 이력 확정 |
| 사각형·원·삼각형·폴리곤 | [RoiGeometry](../src/TeachingToolWpf.Core/Geometry/RoiGeometry.cs), [RoiShape](../src/TeachingToolWpf.Core/Geometry/RoiShape.cs) | 공통 프레임과 불변 윤곽 전략. FrameCorners는 실제 도형이 아닌 편집 프레임 |
| 드래그 중 이동·회전·취소 | [InteractionController](../src/TeachingToolWpf.Core/Interaction/InteractionController.cs), [Gestures](../src/TeachingToolWpf.Core/Interaction/Gestures.cs) | 이미지 좌표와 도구 상태 |
| 큰 선택 집합의 준비·최신 포인터 | [RoiPreviewPreparation](../src/TeachingToolWpf/Presentation/RoiPreviewPreparation.cs) | 작업 하나만 실행, 이전 드래그 결과는 폐기 |
| 무거운 편집의 준비·확정 | [EditorViewModel.Editing](../src/TeachingToolWpf/Presentation/EditorViewModel.Editing.cs) | 작업 스레드 준비 후 UI에서 유효성 확인·확정 |
| 명시적 부모·자식 연결 | [RoiHierarchy](../src/TeachingToolWpf.Core/Editing/RoiHierarchy.cs) | ParentId와 하위 트리 순회. 겹침으로 연결하지 않으며 부모 밖에서도 관계 유지 |
| 부모 색·선 두께 | [RoiStyles](../src/VisionCanvas/Rendering/RoiStyles.cs), [RoiStyleSettingsViewModel](../src/TeachingToolWpf/Presentation/RoiStyleSettingsViewModel.cs) | 표시 설정은 문서·Undo/Redo와 별도 |
| 마우스가 들어오는 입구 | [Dx12Viewport](../src/VisionCanvas/Controls/Dx12Viewport.cs), [NativeMethods](../src/VisionCanvas/Controls/NativeMethods.cs) | Windows 입력을 앱 좌표·입력 호출로 변환 |
| GPU가 화면을 만드는 순서 | [RenderWorker](../src/VisionCanvas/Rendering/RenderWorker.cs) | GPU 자원을 소유하는 렌더 스레드 |
| 많은 ROI를 GPU에 보내는 방식 | [RoiPipeline](../src/VisionCanvas/Rendering/RoiPipeline.cs) | 가시 블록 재사용, 역할·형상·그리기 값 분리 |
| 셰이더에 보내는 값의 의미 | [RoiDrawConstants](../src/VisionCanvas/Rendering/RoiDrawConstants.cs), [Roi.hlsl](../src/VisionCanvas/Shaders/Roi.hlsl) | 배율·원점·회전·색·두께의 동일한 128바이트 배치 |
| 셰이더 컴파일·오류 확인 | [ShaderCompiler](../src/VisionCanvas/Rendering/ShaderCompiler.cs) | 파일을 바이트로 전달해 한글 주석도 소스가 잘리지 않게 처리 |
| 이미지 열기·교체 | [ImageCoordinator](../src/TeachingToolWpf/Presentation/ImageCoordinator.cs) | 성공 후 문서 교체, 실패/취소 시 유지 |
| ROI 필터 미리보기 | [FilterPreviewViewModel](../src/TeachingToolWpf/Presentation/FilterPreviewViewModel.cs), [Imaging/Filters](../src/VisionCanvas/Imaging/Filters) | OpenCV는 영상 처리 경계 안에서 사용 |
| 형상 모델 학습·검색 계약 | [ShapeTrainer](../src/VisionAlgorithm/OpenSrc/PatternMatching/ShapeTrainer.cs), [ShapeMatcher](../src/VisionAlgorithm/OpenSrc/PatternMatching/ShapeMatcher.cs) | 순수 C# Gray8 입력, 불변 모델, 원본 픽셀 좌표 |
| 오른쪽 패턴 티칭 UI 상태 | [ShapeMatchingViewModel](../src/TeachingToolWpf/Presentation/ShapeMatchingViewModel.cs), [ShapeMatchingPanel](../src/TeachingToolWpf/Views/ShapeMatchingPanel.xaml) | worker 취소, source epoch 검사, ROI 편집 이력과 분리 |
| 원본 타일/프레임 adapter | [ShapeMatchingService](../src/VisionInspection/Matching/ShapeMatchingService.cs), [MatchingImageReader](../src/VisionInspection/Matching/MatchingImageReader.cs) | display preview를 재사용하지 않고 독립 source를 열어 필요한 영역을 읽음 |
| 화면 없는 검사 실행 | [RecipeRunner](../src/VisionInspection.Cli/RecipeRunner.cs), [CLI](../src/VisionInspection.Cli/README.md) | WPF와 같은 검사 서비스를 JSON 레시피로 호출 |
| CUDA 검사 전처리 | [CudaImagePreprocessor](../src/VisionCompute.ILGPU/CudaImagePreprocessor.cs) | 평활화·Sobel만 GPU, 공통 CPU 정규화·검색. 화면 DX12와 분리 |
| 검사 판정·표시 변환 | [InspectionDecision](../src/TeachingToolWpf/Imaging/Matching/InspectionDecision.cs), [InspectionResultPresentation](../src/TeachingToolWpf/Presentation/InspectionResultPresentation.cs) | 완전성·정밀화 진단 → 판정색, 원본 좌표의 검출 경계·특징점 |
| GPU 결과 marker | [MatchMarker / ResultOverlay](../src/VisionCanvas/Rendering/MatchMarker.cs), [ResultOverlayPainter](../src/VisionCanvas/Rendering/ResultOverlayPainter.cs) | algorithm 참조 없이 배너·검색 윤곽·기준점·방향·특징점을 표시 |

## 마우스로 ROI를 회전하면

1. `Dx12Viewport`가 Windows 메시지를 받아 이미지 뷰포트의 입력으로 바꾼다. 메시지 번호와 좌표 비트 해석은 Controls 안에서 끝난다.
2. `InteractionController`와 회전 gesture가 회전 요청을 만든다. 이미지 밖으로 나갈 수 있는 각도는 Core 기하 규칙으로 제한한다.
3. `RoiPreviewPreparation`이 큰 선택 집합을 준비한다. 기다리는 동안 포인터 요청을 계속 쌓지 않고 마지막 요청만 기억한다.
4. `EditorSession`이 시작 상태와 미리보기 변환을 관리한다. 드래그 중 모든 ROI를 새로 복제하지 않고 공통 회전값을 사용한다.
5. `EditorViewModel.Publish`가 불변 `RenderScene`을 전달한다. 렌더 스레드는 변경 중인 WPF 객체나 문서 컬렉션을 읽지 않는다.
6. `RoiPipeline`이 기존 GPU 형상에 공통 회전값을 적용한다. 마우스를 놓으면 편집을 한 번 확정하고, Escape면 시작 상태로 되돌린다.

이 흐름에서 **선택·계층·이력의 정확성은 Core**, **준비 중 UI 상태와 취소 연결은 Presentation**, **메모리 재사용과 그리기는 Rendering**의 책임이다.

## ROI에서 형상 검사까지

1. `EditorViewModel.RefreshMatchingState`가 기존 문서에서 단일 선택 ROI, 이미지 참조와 generation을 가져와 `ShapeMatchingViewModel`에 전달한다. 등록 버튼은 ROI를 새로 만들거나 편집 이력을 수정하지 않는다.
2. `ShapeMatchingService.TrainAsync`는 원본 tile source/정지 프레임에 대한 별도 수명을 만들고, 픽셀 중심 기준 ROI 마스크와 중심·기준각을 `TrainingRegion`으로 바꾼다. 파일 입력은 512px tile을 읽고 최대 8 Mpx 학습 영상에서 `ShapeTrainer`를 실행한다.
3. `ShapeTrainer`는 gradient pyramid와 분산된 oriented feature를 불변 `ShapeModel`로 만든다. 내부 feature는 학습 ROI 기준 방향 좌표, `TrainingOptions` 회전 값은 기준 ROI 대비 편차다.
4. 검색은 응답 map pyramid에서 제한 후보를 만들고 원본 해상도 continuous gradient 및 point-to-line 정밀화를 연결한다. `MatchPose.Angle`은 결과 게시 시 이미지 기준 절대각이다. 점수/support/잔차가 임계값에 미달하거나 후보 예산이 잘린 결과는 진단으로 남는다.
5. ViewModel은 실행 당시 source epoch/token과 model identity가 현재 상태와 같은지 확인한 뒤에만 결과를 게시한다. 입력 변경으로 폐기된 결과는 게시하지 않으며, 사용자 취소·오류는 위치 없는 경고로 표시한다. InspectionDecision이 판정하고 InspectionResultPresentation이 좌표·특징점·색으로 변환한다. `RenderScene.Inspection`의 ResultOverlay를 ResultOverlayPainter가 그리며 ROI 상태·선택·undo/redo에 포함되지 않는다.

`ShapePyramidSearch`는 큰 원본의 검사 피라미드 후보 검색과 원본 crop 정밀화를 연결한다. `MatchingImageReader.ReadReduced`는 원본 level-0만 읽고 학습과 동일하게 축소한다. `TemplateMatchingService`는 별도 OpenCV 기본 템플릿 흐름, `ShapeMatchingViewModel.TemplateMatching`은 선택·명령·게시를 담당한다. `CorrelationMatcher.FindCandidates`는 창별 억제 전 후보를 반환하고 앱에서 전역 `Suppress`를 한 번 수행한다.

`VisionAlgorithm`은 WPF/Core/Canvas/DX12 참조가 없으며 OpenCvSharp Mat은 `OpenSrc/PatternMatching/Imaging`에만 둔다. 논문과의 정확한 차이·새 예산은 [ADR-016](decisions/0016-template-matching-and-pyramid-search.md), 합성 실측은 [8K 성능 기록](matching-performance-2026-10-04.md)에 있다.

`EditorViewModel.PatternSample`은 앱의 `Assets/pattern-matching.png`를 기존 이미지 교체 명령으로 연다. `ShapeMatchingViewModel`은 실행/실패 상태도 범용 overlay로 전달한다. 원본 후보 정밀화는 `PreparedSearchImage.CreateForRefinement`의 gradient 피라미드를 사용하며 전체 응답맵은 재생성하지 않는다. 변경 이유와 수렴 범위 한계는 [ADR-017](decisions/0017-default-chip-inspection.md), 실제 시작 화면 검증은 `Diagnostics/InspectionSmoke.Startup.cs`를 참고한다.

## GPU 코드에서 자주 보이는 말

| 이름 | 이 앱에서의 의미 |
|---|---|
| HWND | WPF 안에 만든 Windows 자식 창의 식별자. 그 창에 DX12 화면을 표시한다. |
| swap chain / back buffer | 번갈아 그려서 화면에 내보내는 버퍼. 현재 구현은 두 슬롯이다. |
| command list | GPU가 실행할 복사·그리기 명령을 기록한 목록. |
| fence | 제출한 GPU 작업이 끝났는지 확인하는 완료 번호. 버퍼 재사용·해제의 기준이다. |
| resource barrier | 같은 메모리를 복사 대상으로 쓸지, 셰이더에서 읽을지 전환하는 명령. |
| upload arena | CPU가 채운 데이터를 GPU로 보내는 재사용 공간. 프레임 슬롯의 fence가 완료된 뒤 다시 사용한다. |
| instance | 사각형/원은 중심·축, 폴리곤은 각 변의 시작점·방향을 전달하는 보관 GPU 데이터. ROI마다 WPF 컨트롤을 만들지 않는다. |
| shader constants | 해당 그리기에 공통으로 적용할 배율·회전·색·두께. ROI 형상 전체를 다시 보내는 데이터가 아니다. |
| BVH | 화면 밖 ROI와 불필요한 포함 후보를 빠르게 건너뛰는 공간 검색 트리. |
| LOD | 화면에서 너무 작은 ROI들을 요약해 표시하는 단계. 문서의 ROI 자체를 삭제하거나 합치지 않는다. |

## 수명을 읽는 규칙

`Dx12Surface.Begin`은 현재 화면 슬롯의 GPU 작업 완료를 기다린다. 기다린 뒤에만 이전 자원을 해제하고 명령 메모리를 초기화한다. `FrameUploadArena.Reset`도 그 뒤에 실행된다. `Retire`는 즉시 해제가 아니라 GPU 완료 후 해제 예약이다.

렌더 순서는 `RenderWorker.Run`에 있다. 최신 요청 확인 → 프레임 자원 준비 → 이미지·필터·라벨 준비 → ROI와 화면 요소 기록 → Present 순서다. 캡처는 요청한 장면 버전과 필요한 개요 영상·라벨이 준비되어야 가져간다. 종료에서는 GPU 완료를 기다린 뒤 자원을 해제한다.

`unsafe`와 포인터는 GPU 버퍼에 값을 복사하는 지점에 남아 있다. 값을 계산하는 코드와 바이트를 외부 API로 보내는 코드를 분리해 읽는다. GPU 전달 구조의 필드를 바꿀 때는 C# 크기·오프셋과 HLSL 배치가 함께 바뀌므로 해당 레이아웃 테스트도 확인해야 한다.

## 수정 후 확인할 것

ROI 규칙은 Core 테스트, 입력·표시 연결과 GPU 전달 계약은 Imaging/WPF 테스트로 확인한다. 메모리 해제·캡처·실제 입력을 건드리면 RTX smoke도 실행한다. 대량 ROI 경로를 바꾸면 같은 입력에서 회전 콜백 비용·프레임 간격·업로드 양을 각각 비교한다. 작은 콜백 시간을 전체 작업의 무지연으로 해석하지 않는다.

실제 실행 명령은 [AGENTS.md](../AGENTS.md), ROI 동작 기준은 [구현 계획](../tasks/plan.md), 매칭 설계·상태는 [VisionAlgorithm 계획](../tasks/pattern-matching-plan.md)과 [매칭 검증 기록](shape-matching-validation.md), 성능상 남은 ROI 비용은 [회전 성능 기록](rotation-performance.md), 가독성 정리 범위와 검증은 [가독성 정리 검증](readability-validation.md)에 있다.
