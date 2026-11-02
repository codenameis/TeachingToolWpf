# 코드를 읽기 시작하는 순서

C# 기초부터 실제 구현을 연결하는 교육 HTML: [WPF](../manual/wpf/index.html) · [DX12](../manual/dx12/index.html) · [Vulkan](../manual/vulkan/index.html) · [wgpu](../manual/wgpu/index.html) · [Skia](../manual/skia/index.html). 각 편에 공통 코드·전용 렌더 흐름·계산·실습·풀이를 모았다. [검증·관리](renderer-code-guides.md).

보정은 [사용 예제](fiducial-alignment.md) → `RoiAlignmentMap.TryAlign`(전체/모듈/ROI 그룹과 실제 ParentId) → `FiducialAlignment.TryCreate`(마크 검증·중심·회전·합성·잔차) → `Map`(최종 부모 ID를 보존한 검사 ROI) 순서로 읽는다. 보정 실행은 원래 문서/Undo를 변경하지 않는다.

wgpu 경로는 [별도 앱](../samples/VisionCanvas.WgpuSample/README.md)의 App → EditorViewModel → WgpuViewport → WgpuRenderWorker 순서로 읽는다. WgpuDevice/Surface/FrameResources가 native 자원·제출 완료 대기·업로드 영역을 소유하고, 네 WGSL이 sprite/ROI/mask/반복 격자를 그린다. 공통 Core·장면·타일은 재사용한다. [매뉴얼](../manual/wgpu/README.md), [ADR-068](decisions/0068-wgpu-canvas-and-sample.md).

Skia 경로는 [별도 샘플](../samples/VisionCanvas.SkiaSample/README.md)의 App → EditorViewModel → SkiaViewport → SkiaRenderWorker 순서로 읽는다. 공통 Core/Presentation/ScenePainter를 재사용하고 새 Rendering 파일은 Skia 픽셀·형상·출력 수명만 맡는다. [경계와 결정](decisions/0067-skia-canvas-and-sample.md).

Skia의 `1억 ROI 추가`는 VM의 `AddRepeatedGrid`에서 기존 Core 명령을 호출한다. 반복 윤곽은 `DrawGrid`의 범위 → 가시 셀 → 경로 → 그리기 순서로 읽고, `PasteAsync`·`UpdateRetainedFrame`에서 취소/해제 순서를 확인한다. [코드 읽기 순서](../samples/VisionCanvas.SkiaSample/README.md#코드-읽는-순서), [튜닝 실측과 제외한 실험](skia-canvas-performance.md).

최소 WPF 소비 예제는 [VisionCanvas.MvvmSample](../samples/VisionCanvas.MvvmSample/README.md)이다. App → EditorViewModel → XAML 바인딩 순서로 읽는다. 이미지 교체/이력 서비스 ImageCoordinator는 두 앱이 공유하는 `VisionCanvas.Presentation`에 있다.

C# 문법을 막 배웠다면 [신입용 HTML 실습서](../manual/index.html)의 픽셀 → 좌표 → 필터 → 설계 → 편집 → 표시 순서로 시작한다. [직접 구축 실습](../manual/09-build.html)은 실행 가능한 작은 호스트를 제공한다. 이력은 [DocumentEdit](../src/VisionCanvas.Core/Editing/DocumentEdit.cs)(편집 차이), [HistoryStep](../src/VisionCanvas.Core/Editing/HistoryStep.cs)(준비 요청), [PreparedHistoryStep](../src/VisionCanvas.Core/Editing/PreparedHistoryStep.cs)(준비 결과), [EditHistory](../src/VisionCanvas.Core/Editing/EditHistory.cs)(목록·커서·예산)의 순서로 나누어 읽는다. [정리 범위와 실행 명령 비교](beginner-manual-validation.md).

이 앱은 **입력 → 편집 상태 → 화면 그리기**로 읽으면 된다. ROI 규칙을 바꾸기 위해 DX12 포인터나 셰이더부터 이해할 필요는 없다. 순수 기하는 `VisionGeometry`, 편집 상태는 `VisionCanvas.Core`, 영상 수명 계약은 `VisionImaging.Abstractions`가 소유한다. WPF 화면은 앱의 Views, 바인딩 상태·명령은 ViewModels, GPU 자원은 `VisionCanvas`의 Rendering이 소유한다.

식별자와 괄호는 [Microsoft 기준 C# 컨벤션](csharp-code-conventions.md)을 따른다. `_viewport`는 인스턴스 필드, `s_procedure`는 정적 필드, `SizeInBytes`는 상수 또는 속성처럼 이름으로 역할을 구분한다. 지속 지침은 [AGENTS.md](../AGENTS.md), 검사 설정은 [`.editorconfig`](../.editorconfig)에 있다.

## 작업별 시작점

앱 프로젝트는 조립(`TeachingToolWpf`), 화면·ViewModel(`TeachingToolWpf.Presentation`), 실행 진단(`TeachingToolWpf.Diagnostics`)으로 나뉘며 다음 MVVM 폴더 구성을 사용한다. Model을 만들기 위해 공통 Core의 ROI를 앱으로 복제하지 않는다.

```text
TeachingToolWpf/
  App.xaml                     공통 화면 리소스
  App.xaml.cs                  창/서비스 조립·시작·해제 순서
  Themes/                      화면 리소스
TeachingToolWpf.Presentation/
  Models/                      검사 설정·판정·목록 데이터 (WPF 비의존)
  Views/                       MainWindow.xaml·설정창·패널과 화면 수명
  ViewModels/                  MainWindowViewModel·각 패널의 상태/명령
  Commands/                    RelayCommand·AsyncCommand
  Services/                    이미지 교체·라이브 프레임·편집 준비 작업
  Infrastructure/              ObservableObject
  Controls/                    재사용 WPF 컨트롤·화면 연결
TeachingToolWpf.Diagnostics/
  DiagnosticStartup.cs         실제 앱 검증 시나리오
```

왼쪽 패널의 `ROI 추가/그리기`는 그룹 하나를 선택했으면 그 그룹에 연결된 ROI를, 일반 ROI·무선택·다중선택이면 초록색 독립 ROI를 만든다. 별도 자식 버튼은 이 명령으로 통합했다. `부모 추가/그리기`는 자식이 없어도 부모 역할을 갖는 그룹을 만든다. 생성은 한 번의 이력으로 기록하며 기본 도형과 폴리곤에 동일하게 적용한다.

메인 창의 ViewModel은 [MainWindowViewModel.cs](../src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.cs)다. [App.xaml.cs](../src/TeachingToolWpf/App.xaml.cs)에서 구현 서비스를 주입해 만들고 [MainWindow.xaml.cs](../src/TeachingToolWpf.Presentation/Views/MainWindow.xaml.cs)의 `Bind(model)`이 DataContext와 실제 컨트롤을 연결한다. App이 창을 직접 표시하므로 StartupUri는 사용하지 않는다. 일반 초기화/오류·성능 표시 문구는 VM, 실행 진단 선택은 [DiagnosticStartup](../src/TeachingToolWpf.Diagnostics/DiagnosticStartup.cs)이 맡는다. 색상 설정의 역할별 값은 [RoiStyles.cs](../src/TeachingToolWpf.Presentation/ViewModels/RoiStyles.cs)가 보관하고, `RoiStyleSettingsViewModel`이 검증 후 한 번에 적용한다. 독립 ROI와 부모에 연결된 리프는 같은 초록색 스타일을 쓴다. [모든 View의 코드 비하인드 검토와 남긴 이유](views-mvvm-and-group-bounds.md).

Viewport 연결은 [MainWindow.xaml](../src/TeachingToolWpf.Presentation/Views/MainWindow.xaml)의 Scene/Input/Layout 바인딩 → [의존 속성](../src/VisionCanvas/Controls/Dx12Viewport.Properties.cs) → 기존 Submit 순서로 읽는다. VM은 장면 데이터와 명령을 게시하고 이미지 소유권 이전은 IImageDisplay에 위임한다. [MVVM 경계와 남긴 View 코드의 이유](decisions/0046-parent-reuse-and-mvvm-viewport.md).

| 하려는 일 | 먼저 읽을 파일 | 알아둘 경계 |
|---|---|---|
| 버튼·단축키 연결 | [MainWindowViewModel](../src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.cs)(조립) → [MainWindowViewModel.Commands](../src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.Commands.cs)(명령 40개) | WPF 요청을 같은 편집 명령으로 전달. 생성자는 객체 조립과 이벤트 구독만 한다 |
| ROI 생성·그룹화·삭제·Undo/Redo | [EditorSession](../src/VisionCanvas.Core/Editing/EditorSession.cs) | 문서 변경과 한 번의 편집 이력 확정 |
| ROI 4단계·부모 편입 | [RoiGroupEditor](../src/VisionCanvas.Core/Editing/RoiGroupEditor.cs), [RoiHierarchy](../src/VisionCanvas.Core/Editing/RoiHierarchy.cs) | RootGroup→ModuleGroup→RoiGroup→Roi, 최대4단계. [변경된 규칙·사용법](roi-hierarchy-validation.md) |
| ROI 마스크 그리기·목록·검사 제외 | [RoiMaskViewModel](../src/TeachingToolWpf.Presentation/ViewModels/RoiMaskViewModel.cs), [MaskDrawGesture](../src/VisionCanvas.Core/Interaction/MaskDrawGesture.cs), [MaskRegion](../src/VisionGeometry/MaskRegion.cs) | 정규화 불변 값과 통합 이력. [MaskPipeline](../src/VisionCanvas/Rendering/MaskPipeline.cs)은 표시만, 중립 RoiExclusion은 원본 검사 연결. [적용 규칙·검증](roi-masking.md) |
| 검색 ROI·그룹 이름 표시 | [RoiSearchRange](../src/VisionCanvas.Core/Editing/RoiSearchRange.cs), [검색 색인](../src/VisionCanvas.Core/Editing/RoiSnapshot.Search.cs), [속성 VM](../src/TeachingToolWpf.Presentation/ViewModels/RoiPropertiesViewModel.cs) | 타입은 즉시 저장, 수치 여유·이름은 변경 적용으로 저장. [최신 사용법](roi-hierarchy-validation.md) |
| 사각형·원·삼각형·폴리곤 | [RoiGeometry](../src/VisionGeometry/RoiGeometry.cs), [RoiShape](../src/VisionGeometry/RoiShape.cs) | 공통 프레임과 불변 윤곽 전략. FrameCorners는 실제 도형이 아닌 편집 프레임 |
| 폴리곤 점 입력·교차점 닫힘 | [RoiCreation](../src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.RoiCreation.cs), [PolygonClosure](../src/VisionCanvas.Core/Geometry/PolygonClosure.cs) | 추가 버튼은 빈 초안 시작. 첫 교차점의 닫힌 경로만 한 번 확정 |
| 드래그 중 이동·회전·취소 | [InteractionController](../src/VisionCanvas.Core/Interaction/InteractionController.cs), [EditGesture](../src/VisionCanvas.Core/Interaction/EditGesture.cs), [MoveGesture](../src/VisionCanvas.Core/Interaction/MoveGesture.cs), [RotateGesture](../src/VisionCanvas.Core/Interaction/RotateGesture.cs) | 입력 선택과 개별 제스처를 나눠 읽기. `InitialViewport`는 드래그 시작 시점의 변환 |
| 마우스 클릭이 무엇을 잡는지 | [RoiHitTester](../src/VisionCanvas.Core/Interaction/RoiHitTester.cs) | 문서와 화면 좌표만 안다. 3 DIP 허용 반경, 겹치면 ①면적 작은 것 ②나중에 만든 것 ③반복 셀 인덱스 큰 것. 미니맵 위에서는 잡지 않는다 |
| 메인 편집기의 1억 ROI | [RepeatedRoiBatch](../src/VisionCanvas.Core/Editing/RepeatedRoiBatch.cs), [RoiCellSet](../src/VisionCanvas.Core/Editing/RoiCellSet.cs), [EditorSession.Repeated](../src/VisionCanvas.Core/Editing/EditorSession.Repeated.cs), [RepeatedRoiPipeline](../src/VisionCanvas/Rendering/RepeatedRoiPipeline.cs) | 배치/셀 ID, 부분 선택·복붙·변환, 개별 수정값, 공통 문서 이력. [사용법·실측·한계](main-editor-repeated-roi-validation.md) |
| 큰 선택 집합의 준비·최신 포인터 | [RoiPreviewPreparation](../src/TeachingToolWpf.Presentation/Services/RoiPreviewPreparation.cs) | 작업 하나만 실행, 이전 드래그 결과는 폐기 |
| 무거운 편집의 준비·확정 | [MainWindowViewModel.Editing](../src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.Editing.cs) | 작업 스레드 준비 후 UI에서 유효성 확인·확정 |
| 명시적 부모·자식 연결 | [RoiHierarchy](../src/VisionCanvas.Core/Editing/RoiHierarchy.cs), [RoiGroupBounds](../src/VisionCanvas.Core/Editing/RoiGroupBounds.cs) | 부모 안의 일반 ROI/그룹은 상위 도형 안에서 편집. 밖의 리프 편입은 허용하고 내부 이동 확정 후 제한. 그룹 해제로 분리 |
| 빠른 이동·회전과 중첩 선택 | [RoiMotionBounds](../src/VisionGeometry/RoiMotionBounds.cs), [InteractionController](../src/VisionCanvas.Core/Interaction/InteractionController.cs) | 첫 경계에서 멈춤. 더블클릭으로 겹친 계층 순환, 고른 그룹 내부 드래그. [사용법·검증](roi-boundary-motion-validation.md) |
| 부모 색·선 두께 | [RoiStyles](../src/VisionCanvas/Rendering/RoiStyles.cs), [RoiStyleSettingsViewModel](../src/TeachingToolWpf.Presentation/ViewModels/RoiStyleSettingsViewModel.cs) | 표시 설정은 문서·Undo/Redo와 별도 |
| 앱 표시 설정·크로스헤어 | [ConfigPage](../src/TeachingToolWpf.Presentation/Views/ConfigPage.xaml), [CrosshairLayerLink](../src/TeachingToolWpf.Presentation/Controls/CrosshairLayerLink.cs) | 별도 설정 창과 불변 레이어 게시. [사용 가이드](configuration-guide.md) |
| 마우스가 들어오는 입구 | [Dx12Viewport](../src/VisionCanvas/Controls/Dx12Viewport.cs), [NativeMethods](../src/VisionCanvas/Controls/NativeMethods.cs) | Windows 입력을 앱 좌표·입력 호출로 변환 |
| GPU가 화면을 만드는 순서 | [RenderWorker](../src/VisionCanvas/Rendering/RenderWorker.cs) | GPU 자원을 소유하는 렌더 스레드 |
| 많은 ROI를 GPU에 보내는 방식 | [RoiPipeline](../src/VisionCanvas/Rendering/RoiPipeline.cs) | 가시 블록 재사용, 역할·형상·그리기 값 분리 |
| 작은 ROI 버퍼의 할당·해제 | [RoiBufferArena](../src/VisionCanvas/Rendering/RoiBufferArena.cs) | GPU 페이지 안의 정렬된 구간을 대여하고 fence 이후 마지막 lease 반환 시 페이지 해제 |
| 셰이더에 보내는 값의 의미 | [RoiDrawConstants](../src/VisionCanvas/Rendering/RoiDrawConstants.cs), [Roi.hlsl](../src/VisionCanvas/Shaders/Roi.hlsl) | 배율·원점·회전·색·두께의 동일한 176바이트 배치 |
| 셰이더 컴파일·오류 확인 | [ShaderCompiler](../src/VisionCanvas/Rendering/ShaderCompiler.cs) | 파일을 바이트로 전달해 한글 주석도 소스가 잘리지 않게 처리 |
| 이미지 열기·교체 | [ImageCoordinator](../src/VisionCanvas/Presentation/ImageCoordinator.cs) | 성공 후 문서 교체, 실패/취소 시 유지 |
| ROI 필터 컨트롤/미리보기 | [VisionCanvas.Filters.Wpf](../src/VisionCanvas.Filters.Wpf/README.md), [Imaging/Filters](../src/VisionCanvas/Imaging/Filters) | MVVM 파라미터·토글·연속 갱신과 CPU/CUDA 계산 경계 분리 |
| 형상 모델 학습·검색 계약 | [ShapeTrainer](../src/VisionAlgorithm/OpenSrc/PatternMatching/ShapeTrainer.cs), [ShapeMatcher](../src/VisionAlgorithm/OpenSrc/PatternMatching/ShapeMatcher.cs) | 순수 C# Gray8 입력, 불변 모델, 원본 픽셀 좌표 |
| 오른쪽 패턴 티칭 UI 상태 | [ShapeMatchingViewModel](../src/TeachingToolWpf.Presentation/ViewModels/ShapeMatchingViewModel.cs), [ShapeMatchingPanel](../src/TeachingToolWpf.Presentation/Views/ShapeMatchingPanel.xaml) | worker 취소, source epoch 검사, ROI 편집 이력과 분리 |
| 검사 패널이 문서를 따라가는 방법 | [InspectionSelection](../src/VisionCanvas.Core/Editing/InspectionSelection.cs)(선택 해석) → [InspectionUpdate](../src/TeachingToolWpf.Presentation/ViewModels/InspectionUpdate.cs)(갱신 요청) → 각 패널의 `Update` | 라이브 프레임마다 호출된다. 검색 여유·마스크가 없으면 할당이 없고, 패널별 무효화 조건은 서로 다르다 |
| 원본 타일/프레임 adapter | [ShapeMatchingService](../src/VisionInspection/Matching/ShapeMatchingService.cs), [MatchingImageReader](../src/VisionInspection/Matching/MatchingImageReader.cs) | display preview를 재사용하지 않고 독립 source를 열어 필요한 영역을 읽음 |
| 화면 없는 검사 실행 | [RecipeRunner](../src/VisionInspection.Cli/RecipeRunner.cs), [CLI](../src/VisionInspection.Cli/README.md) | WPF와 같은 검사 서비스를 JSON 레시피로 호출 |
| CUDA 검사 후보 점수·장치 수명 | [CudaImagePreprocessor](../src/VisionCompute.ILGPU/CudaImagePreprocessor.cs), [CudaInspectionSession](../src/VisionCompute.ILGPU/CudaInspectionSession.cs) | 앱/CLI는 방향 응답·정수 후보 점수를 GPU에서 계산. 전처리·연속 검증·원본 판정은 CPU, 화면 DX12와 분리 |
| 검사 판정·표시 변환 | [InspectionDecision](../src/TeachingToolWpf.Presentation/Models/InspectionDecision.cs), [InspectionResultPresentation](../src/TeachingToolWpf.Presentation/ViewModels/InspectionResultPresentation.cs) | 완전성·정밀화 진단 → 판정색, 원본 좌표의 검출 경계·특징점 |
| GPU 결과 marker | [MatchMarker / ResultOverlay](../src/VisionCanvas/Rendering/MatchMarker.cs), [ResultOverlayPainter](../src/VisionCanvas/Rendering/ResultOverlayPainter.cs) | algorithm 참조 없이 배너·검색 윤곽·기준점·방향·특징점을 표시 |
| 실행 진단 플래그 추가 | [DiagnosticStartup](../src/TeachingToolWpf.Diagnostics/DiagnosticStartup.cs) | 진단 표(`s_runs`)에 한 줄만 추가한다. 인자 이름이 겹치지 않아 표 순서는 의미가 없다. 진단은 시작할 때 한 번만 도는 코드 |

## 마우스로 ROI를 회전하면

1. `Dx12Viewport`가 Windows 메시지를 받아 이미지 뷰포트의 입력으로 바꾼다. 메시지 번호와 좌표 비트 해석은 Controls 안에서 끝난다.
2. `RoiHitTester`가 클릭 지점에서 잡을 ROI·반복 셀을 고르고, `InteractionController`와 회전 gesture가 회전 요청을 만든다. 이미지 밖으로 나갈 수 있는 각도는 Core 기하 규칙으로 제한한다.
3. `RoiPreviewPreparation`이 큰 선택 집합을 준비한다. 기다리는 동안 포인터 요청을 계속 쌓지 않고 마지막 요청만 기억한다.
4. `EditorSession`이 시작 상태와 미리보기 변환을 관리한다. 드래그 중 모든 ROI를 새로 복제하지 않고 공통 회전값을 사용한다.
5. `MainWindowViewModel.Publish`가 불변 `RenderScene`을 전달한다. 렌더 스레드는 변경 중인 WPF 객체나 문서 컬렉션을 읽지 않는다.
6. `RoiPipeline`이 기존 GPU 형상에 공통 회전값을 적용한다. 마우스를 놓으면 편집을 한 번 확정하고, Escape면 시작 상태로 되돌린다.

이 흐름에서 **선택·계층·이력의 정확성은 Core**, **준비 중 UI 상태와 취소 연결은 Presentation**, **메모리 재사용과 그리기는 Rendering**의 책임이다.

## ROI에서 형상 검사까지

1. `MainWindowViewModel.RefreshMatchingState`가 기존 문서에서 단일 선택 ROI, 이미지 참조와 generation을 가져와 `ShapeMatchingViewModel`에 전달한다. 등록 버튼은 ROI를 새로 만들거나 편집 이력을 수정하지 않는다.
2. `ShapeMatchingService.TrainAsync`는 원본 tile source/정지 프레임에 대한 별도 수명을 만들고, 픽셀 중심 기준 ROI 마스크와 중심·기준각을 `TrainingRegion`으로 바꾼다. 파일 입력은 512px tile을 읽고 최대 8 Mpx 학습 영상에서 `ShapeTrainer`를 실행한다.
3. `ShapeTrainer`는 gradient pyramid와 분산된 oriented feature를 불변 `ShapeModel`로 만든다. 내부 feature는 학습 ROI 기준 방향 좌표, `TrainingOptions` 회전 값은 기준 ROI 대비 편차다.
4. 검색은 응답 map pyramid에서 제한 후보를 만들고 원본 해상도 continuous gradient 및 point-to-line 정밀화를 연결한다. `MatchPose.Angle`은 결과 게시 시 이미지 기준 절대각이다. 점수/support/잔차가 임계값에 미달하거나 후보 예산이 잘린 결과는 진단으로 남는다.
5. ViewModel은 실행 당시 source epoch/token과 model identity가 현재 상태와 같은지 확인한 뒤에만 결과를 게시한다. 입력 변경으로 폐기된 결과는 게시하지 않으며, 사용자 취소·오류는 위치 없는 경고로 표시한다. InspectionDecision이 판정하고 InspectionResultPresentation이 좌표·특징점·색으로 변환한다. `RenderScene.Inspection`의 ResultOverlay를 ResultOverlayPainter가 그리며 ROI 상태·선택·undo/redo에 포함되지 않는다.

`ShapePyramidSearch`는 큰 원본의 검사 피라미드 후보 검색과 원본 crop 정밀화를 연결한다. `MatchingImageReader.ReadReduced`는 원본 level-0만 읽고 학습과 동일하게 축소한다. `TemplateMatchingService`는 별도 OpenCV 기본 템플릿 흐름, `ShapeMatchingViewModel.TemplateMatching`은 선택·명령·게시를 담당한다. `CorrelationMatcher.FindCandidates`는 창별 억제 전 후보를 반환하고 앱에서 전역 `Suppress`를 한 번 수행한다.

`VisionAlgorithm`은 WPF/Core/Canvas/DX12 참조가 없으며 OpenCvSharp Mat은 `OpenSrc/PatternMatching/Imaging`에만 둔다. 논문과의 정확한 차이·새 예산은 [ADR-016](decisions/0016-template-matching-and-pyramid-search.md), 합성 실측은 [8K 성능 기록](matching-performance-2026-10-04.md)에 있다.

앱/CLI의 CUDA 혼합 경로는 `CudaInspectionSession`에서 한 검사 lease를 빌려 후보 점수를 계산한다. `CudaImagePreprocessor`에는 별도의 Gaussian/Sobel 전처리 전용 API도 남아 있지만, 현재 앱/CLI의 전처리는 CPU에서 수행한다. 두 호출 경로와 메모리·취소 계약은 [CUDA 구현 안내](../src/VisionCompute.ILGPU/README.md)를 따른다.

`MainWindowViewModel.PatternSample`은 앱의 `Assets/pattern-matching.png`를 기존 이미지 교체 명령으로 연다. `ShapeMatchingViewModel`은 실행/실패 상태도 범용 overlay로 전달한다. 원본 후보 정밀화는 `PreparedSearchImage.CreateForRefinement`의 gradient 피라미드를 사용하며 전체 응답맵은 재생성하지 않는다. 변경 이유와 수렴 범위 한계는 [ADR-017](decisions/0017-default-chip-inspection.md), 실제 시작 화면 검증은 `Diagnostics/InspectionSmoke.Startup.cs`를 참고한다.

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

ROI 규칙은 `TeachingToolWpf.Core.Tests`, 캔버스·GPU 전달 계약은 `VisionCanvas.Tests`, 앱 입력·표시 연결은 `TeachingToolWpf.Imaging.Tests`로 확인한다. 메모리 해제·캡처·실제 입력을 건드리면 RTX smoke도 실행한다. 대량 ROI 경로를 바꾸면 같은 입력에서 회전 콜백 비용·프레임 간격·업로드 양을 각각 비교한다. 작은 콜백 시간을 전체 작업의 무지연으로 해석하지 않는다.

현재 대량 ROI 작업 전체 구간의 2D 60FPS 수용은 FAIL이며 최초 준비·일부 GC·게시·이력 비용이 남아 있다. [그룹화·회전·Esc 후속 검증](group-freeze-validation.md)과 [프레임 예산 검증](frame-budget-validation.md)을 먼저 확인한다. 별도 검사 목표인 [논문 형상 CPU 125ms / CUDA 80ms 미만](paper-shape-125ms-validation.md)도 FAIL이다. 기존 낮은 콜백 시간이나 500ms 검사 실측으로 두 목표를 통과한 것으로 설명하지 않는다.

실제 실행 명령은 [AGENTS.md](../AGENTS.md), ROI 동작 기준은 [구현 계획](../tasks/plan.md), 매칭 설계·상태는 [VisionAlgorithm 계획](../tasks/pattern-matching-plan.md)과 [매칭 검증 기록](shape-matching-validation.md), 성능상 남은 ROI 비용은 [회전 성능 기록](rotation-performance.md), 가독성 정리 범위와 검증은 [가독성 정리 검증](readability-validation.md)에 있다.

## 원칙이 코드에 나타난 곳

"원칙을 지켰다"는 말은 파일을 가리킬 수 있을 때만 의미가 있다. 아래 표는 원칙마다 읽을 곳 하나와, 그 파일에서 무엇을 보면 되는지 적은 것이다. 설계를 배우는 사람은 이 표를 따라가며 "왜 이렇게 나눴는지"를 확인하면 된다.

| 원칙 | 읽을 곳 | 무엇을 보면 되는가 |
|---|---|---|
| SRP (단일 책임) | [RoiHitTester](../src/VisionCanvas.Core/Interaction/RoiHitTester.cs) | 문서와 화면 좌표만 받아 "무엇을 잡았나"만 계산한다. 제스처·도구 상태·미니맵 패널은 모른다 |
| OCP (개방-폐쇄) | [RoiShape](../src/VisionGeometry/RoiShape.cs), [DiagnosticStartup](../src/TeachingToolWpf.Diagnostics/DiagnosticStartup.cs) | 새 도형은 파생 record 추가, 새 진단은 표 한 줄. 기존 분기를 고치지 않는다 |
| DIP (의존 역전) | [VisionCanvas.Core.csproj](../src/VisionCanvas.Core/VisionCanvas.Core.csproj) | Core는 WPF·DX12를 참조하지 않는다. 화면은 `IImageDisplay` 같은 인터페이스로만 연결된다 |
| Information Expert | [RoiIdSet.TryGetOnly](../src/VisionCanvas.Core/Editing/RoiIdSet.cs), [RoiSearchRange.Expand](../src/VisionCanvas.Core/Editing/RoiSearchRange.cs) | 데이터를 가진 쪽이 그 데이터에 대한 계산을 갖는다 |
| Creator | [EditorSession.Add](../src/VisionCanvas.Core/Editing/EditorSession.cs) | ROI를 만드는 데 필요한 상태(문서·이력·번호)를 가진 쪽이 만든다 |
| Controller | [InteractionController](../src/VisionCanvas.Core/Interaction/InteractionController.cs) | 입력 이벤트를 편집 명령으로 바꾸는 첫 지점. UI 이벤트를 직접 처리하지 않는다 |
| Low Coupling | [InspectionUpdate](../src/TeachingToolWpf.Presentation/ViewModels/InspectionUpdate.cs), [InspectionSelection](../src/VisionCanvas.Core/Editing/InspectionSelection.cs) | 패널은 문서 전체가 아니라 필요한 값만 받는다 |
| High Cohesion | [MainWindowViewModel.Commands](../src/TeachingToolWpf.Presentation/ViewModels/MainWindowViewModel.Commands.cs) | 같은 목적의 배선을 한 파일 5묶음으로 모은다. 생성자는 조립만 한다 |
| Polymorphism | [IGesture / NoGesture](../src/VisionCanvas.Core/Interaction/Gestures.cs) | 종류별 if 대신 객체로 나눈다. "제스처 없음"도 객체(Null Object)다 |
| Pure Fabrication | [RoiPreviewPreparation](../src/TeachingToolWpf.Presentation/Services/RoiPreviewPreparation.cs) | 도메인에 없는 "준비 작업"을 서비스로 만들어 Core를 오염시키지 않는다 |
| Parameter Object | [InspectionUpdate](../src/TeachingToolWpf.Presentation/ViewModels/InspectionUpdate.cs) | 플래그 3개 나열(boolean blindness) 대신 이름 있는 값 묶음 |
| Composition Root | [App.xaml.cs](../src/TeachingToolWpf/App.xaml.cs) | 구현 선택과 수명은 한 곳에서만. 나머지는 받은 것만 쓴다 |
| Table-driven | [DiagnosticStartup](../src/TeachingToolWpf.Diagnostics/DiagnosticStartup.cs) | if/else 사슬 대신 표. 분기 추가가 표 한 줄이 된다 |

일부러 지키지 않은 곳도 적어 둔다. `RoiGeometry`는 사각형 판정을 `Shape.Kind`로 네 번 분기한다. 도형마다 다른 동작은 이미 `RoiShape` 파생이 맡고, 남은 분기는 "사각형만 예외"인 프레임·검증 규칙이라 파생 타입으로 옮기면 오히려 읽기 어려워진다. 회전 제한([RoiSpatialIndex.RotationLimits](../src/VisionCanvas.Core/Geometry/RoiSpatialIndex.cs))도 BVH 안에 남겼다. 반올림·이분법에 민감해 구조를 바꾸면 정확도가 걸리기 때문이다. 대신 두 곳 모두 왜 그런지 주석으로 남겼다.

