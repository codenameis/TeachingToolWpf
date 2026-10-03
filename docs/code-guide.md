# 코드를 읽기 시작하는 순서

이 앱은 **입력 → 편집 상태 → 화면 그리기**로 읽으면 된다. ROI 규칙을 바꾸기 위해 DX12 포인터나 셰이더부터 이해할 필요는 없다. 편집 상태는 Core가, WPF 연결은 Presentation이, GPU 자원은 Rendering이 소유한다.

## 작업별 시작점

| 하려는 일 | 먼저 읽을 파일 | 알아둘 경계 |
|---|---|---|
| 버튼·단축키 연결 | [EditorViewModel](../Presentation/EditorViewModel.cs) | WPF 요청을 같은 편집 명령으로 전달 |
| ROI 생성·그룹화·삭제·Undo/Redo | [EditorSession](../src/TeachingToolWpf.Core/Editing/EditorSession.cs) | 문서 변경과 한 번의 편집 이력 확정 |
| 사각형·원·삼각형·폴리곤 | [RoiGeometry](../src/TeachingToolWpf.Core/Geometry/RoiGeometry.cs), [RoiShape](../src/TeachingToolWpf.Core/Geometry/RoiShape.cs) | 공통 프레임과 불변 윤곽 전략. FrameCorners는 실제 도형이 아닌 편집 프레임 |
| 드래그 중 이동·회전·취소 | [InteractionController](../src/TeachingToolWpf.Core/Interaction/InteractionController.cs), [Gestures](../src/TeachingToolWpf.Core/Interaction/Gestures.cs) | 이미지 좌표와 도구 상태 |
| 큰 선택 집합의 준비·최신 포인터 | [RoiPreviewPreparation](../Presentation/RoiPreviewPreparation.cs) | 작업 하나만 실행, 이전 드래그 결과는 폐기 |
| 무거운 편집의 준비·확정 | [EditorViewModel.Editing](../Presentation/EditorViewModel.Editing.cs) | 작업 스레드 준비 후 UI에서 유효성 확인·확정 |
| 부모·자식 포함 판정 | [RoiHierarchy](../src/TeachingToolWpf.Core/Editing/RoiHierarchy.cs) | 반지름·실제 윤곽의 완전 포함으로 판정, 오목 홈을 가로지르는 변 거부 |
| 부모 색·선 두께 | [RoiStyles](../Rendering/RoiStyles.cs), [RoiStyleSettingsViewModel](../Presentation/RoiStyleSettingsViewModel.cs) | 표시 설정은 문서·Undo/Redo와 별도 |
| 마우스가 들어오는 입구 | [Dx12Viewport](../Controls/Dx12Viewport.cs), [NativeMethods](../Controls/NativeMethods.cs) | Windows 입력을 앱 좌표·입력 호출로 변환 |
| GPU가 화면을 만드는 순서 | [RenderWorker](../Rendering/RenderWorker.cs) | GPU 자원을 소유하는 렌더 스레드 |
| 많은 ROI를 GPU에 보내는 방식 | [RoiPipeline](../Rendering/RoiPipeline.cs) | 가시 블록 재사용, 역할·형상·그리기 값 분리 |
| 셰이더에 보내는 값의 의미 | [RoiDrawConstants](../Rendering/RoiDrawConstants.cs), [Roi.hlsl](../Shaders/Roi.hlsl) | 배율·원점·회전·색·두께의 동일한 128바이트 배치 |
| 셰이더 컴파일·오류 확인 | [ShaderCompiler](../Rendering/ShaderCompiler.cs) | 파일을 바이트로 전달해 한글 주석도 소스가 잘리지 않게 처리 |
| 이미지 열기·교체 | [ImageCoordinator](../Presentation/ImageCoordinator.cs) | 성공 후 문서 교체, 실패/취소 시 유지 |
| ROI 필터 미리보기 | [FilterPreviewViewModel](../Presentation/FilterPreviewViewModel.cs), [Imaging/Filters](../Imaging/Filters) | OpenCV는 영상 처리 경계 안에서 사용 |

## 마우스로 ROI를 회전하면

1. `Dx12Viewport`가 Windows 메시지를 받아 이미지 뷰포트의 입력으로 바꾼다. 메시지 번호와 좌표 비트 해석은 Controls 안에서 끝난다.
2. `InteractionController`와 회전 gesture가 회전 요청을 만든다. 이미지 밖으로 나갈 수 있는 각도는 Core 기하 규칙으로 제한한다.
3. `RoiPreviewPreparation`이 큰 선택 집합을 준비한다. 기다리는 동안 포인터 요청을 계속 쌓지 않고 마지막 요청만 기억한다.
4. `EditorSession`이 시작 상태와 미리보기 변환을 관리한다. 드래그 중 모든 ROI를 새로 복제하지 않고 공통 회전값을 사용한다.
5. `EditorViewModel.Publish`가 불변 `RenderScene`을 전달한다. 렌더 스레드는 변경 중인 WPF 객체나 문서 컬렉션을 읽지 않는다.
6. `RoiPipeline`이 기존 GPU 형상에 공통 회전값을 적용한다. 마우스를 놓으면 편집을 한 번 확정하고, Escape면 시작 상태로 되돌린다.

이 흐름에서 **선택·계층·이력의 정확성은 Core**, **준비 중 UI 상태와 취소 연결은 Presentation**, **메모리 재사용과 그리기는 Rendering**의 책임이다.

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

실제 실행 명령은 [AGENTS.md](../AGENTS.md), 동작 기준은 [구현 계획](../tasks/plan.md), 성능상 남은 비용은 [회전 성능 기록](rotation-performance.md), 이번 정리 범위와 검증은 [가독성 정리 검증](readability-validation.md)에 있다.
