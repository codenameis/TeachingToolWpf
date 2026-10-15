# VisionCanvas · WPF MVVM 샘플

진단 실행 코드 없이 라이브러리를 사용하는 독립 WPF 앱이다. `TeachingToolWpf`나 기존 `VisionCanvas.Sample` 프로젝트를 참조하지 않는다.

## 처음 코드를 읽는 순서

각 파일에 역할·실행 순서·처음 등장하는 문법을 설명하는 한국어 주석이 있다. 아래 순서로 읽으면 객체를 만드는 곳에서 실제 버튼 동작까지 이어진다.

1. [App.xaml.cs](App.xaml.cs): 창·문서·서비스를 만들고 연결한다. `DataContext`가 무엇을 연결하는지 확인한다.
2. [InspectionModelWindow.xaml](Views/InspectionModelWindow.xaml): 두 카메라 배치를 확인한다. [EditorView.xaml](Views/EditorView.xaml)은 두 카메라가 공유하는 편집 화면이며 [코드 비하인드](Views/EditorView.xaml.cs)는 UI 입력을 전달한다.
3. [EditorViewModel.cs](ViewModels/EditorViewModel.cs): 버튼이 할 일, 실행 가능 조건, 화면에 보여 줄 값을 준비한다.
4. [EditorCommand.cs](Commands/EditorCommand.cs): 클릭을 받아 실행하고, 중복 클릭과 오류를 처리한다.
5. [ImageFilePicker.cs](Services/ImageFilePicker.cs)와 [ImageCoordinator.cs](../../src/VisionCanvas/Presentation/ImageCoordinator.cs): 파일 선택과 실제 이미지 읽기·교체·해제의 책임을 구별한다.

예를 들어 **8K 예제**를 누르면 `EditorCommand.ExecuteAsync`가 VM의 `SampleCommand`에 연결한 함수를 실행한다. 그 함수가 `ImageCoordinator.ReplaceAsync`로 이미지를 준비하면, 변경 알림을 받은 VM의 `Refresh`가 새 `Scene`을 만든다. WPF 바인딩이 이를 Canvas에 전달하여 표시한다. 이 흐름을 먼저 따라간 뒤 ROI 그리기를 읽으면 된다.

## 실행

저장소 루트에서 Windows x64, .NET 10 Desktop Runtime/SDK 환경으로 실행한다.

```powershell
dotnet run --project samples/VisionCanvas.MvvmSample/VisionCanvas.MvvmSample.csproj -c Release
```

Visual Studio에서는 이 프로젝트를 시작 프로젝트로 지정한다. 처음에는 두 카메라가 비어 있다. 상단 **2카메라 모델 불러오기**로 예제를 함께 불러오거나, 각 카메라의 **8K 예제 / 파일 열기**로 개별 이미지를 연다. 이미지 파일 로딩에는 저장소의 `VisionImaging.Codecs` 네이티브 런타임이 필요하며 기존 프로젝트와 같은 빌드 검증을 적용한다. 준비 방법과 지원 형식은 [코덱 실행 안내](../../experiments/VisionImaging.Codecs/README.md)를 따른다.

## 두 카메라 모델

[SampleInspectionModel.cs](Models/SampleInspectionModel.cs)에 내장 예제 데이터를 정의한다. 외부 모델 파일을 읽는 기능은 이번 범위에 포함하지 않는다.

- 카메라 1은 상면의 녹색 합성 이미지, 카메라 2는 하면의 청색 합성 이미지이며 각각 8192×8192다. 실제 카메라 SDK나 수집은 사용하지 않는다.
- 카메라마다 보드 그룹 → 커넥터 그룹 → ROI 계층과 독립 ROI를 둔다. 그룹 2개, 사각형·원·다각형 리프 ROI 5개다.
- 각 리프 ROI에는 평균 밝기, 이진화 면적, 패턴 매칭, 에지 개수, 원 검출 중 하나의 **예제 이름·파라미터**를 연결한다. 알고리즘 실행·검출 결과·합격 판정은 구현하지 않는다.
- 아래 목록의 행을 누르면 해당 ROI가 Canvas에서도 선택되고 파라미터가 표시된다. Canvas에서 단일 ROI를 선택해도 목록에 반영된다. 복수 선택 때는 단일 행을 표시하지 않는다.
- 편집/선택/확대·축소/이미지/Undo/Redo는 카메라마다 독립적이다. 첫 모델 로딩은 시작 상태이며, 모델 재로딩은 카메라별 Undo 한 건이다.
- 불러온 ROI의 ID로 알고리즘 설정을 찾으므로 삭제 후 Undo·이미지 교체 후 Undo에서도 연결을 유지한다. 새로 그리거나 붙여넣은 ROI는 **알고리즘 미지정**이다. 설정 편집·자동 알고리즘 복제는 하지 않는다.
- 두 카메라는 각각 로딩한다. 한쪽에서 실패·취소하면 그쪽의 이전 문서를 보존하고 해당 Status에 알린다. 두 카메라 전체를 묶는 트랜잭션이나 통합 Undo는 아니다.

같은 예제 정의를 앱 수명 동안 재사용한다. 모델 내용이나 알고리즘 예시를 바꾸려면 `SampleInspectionModel.CreateCamera`를 수정하면 된다. 도형·계층은 Core의 `Roi`를 사용하며 앱 모델에는 카메라와 알고리즘 연결만 추가한다.

## 사용

- **사각형/원 그리기**: 이미지 위에서 드래그한다. **다각형 그리기**는 점 클릭 후 Enter 또는 **그리기 완료**, Esc로 취소한다.
- **부모 그리기**: 빈 그룹을 만든다. 그룹 하나를 선택한 상태에서 ROI 그리기를 시작하면 해당 부모 안에 자식을 만든다.
- **선택**: 클릭·Ctrl+클릭·빈 곳 드래그로 선택한다. 선택 도형을 드래그해 이동하고 핸들로 회전·크기를 바꾼다. 계층과 경계 규칙은 Core의 기존 규칙을 그대로 사용한다.
- **그룹화/그룹 해제/삭제**: 그룹 해제는 부모 껍질만 풀고 자식 형상을 유지한다. 삭제는 선택 그룹의 자손도 삭제한다.
- 휠로 확대·축소하고 가운데 버튼으로 화면을 이동한다. **화면 맞춤**으로 돌아온다.
- Ctrl+A/C/V, Delete, Ctrl+Z/Y, Ctrl+Shift+Z, Ctrl+O를 지원한다. 버튼과 단축키는 동일한 명령을 실행한다.
- 이미지 교체도 Undo/Redo 대상이다. 원본을 다시 열 수 없으면 기존 이미지와 이력 위치를 보존하고 오류를 표시한다.

## 구조와 재사용

| 파일 | 책임 |
|---|---|
| `App.xaml.cs` | EditorSession/Input/Layout/이미지 서비스/VM/창 조립과 종료 |
| `Views/InspectionModelWindow.xaml` | 두 EditorView와 모델 로딩 버튼 배치 |
| `Views/EditorView.xaml(.cs)` | 도구 버튼, ROI/알고리즘 목록, Canvas 바인딩, UI 입력 전달과 수명 |
| `Views/EditorWindow.xaml(.cs)` | 단일 Canvas 호스트. 같은 EditorView 재사용 |
| `ViewModels/InspectionModelViewModel.cs` | 두 카메라의 로딩 명령과 VM 수명 |
| `ViewModels/EditorViewModel.cs` | 화면 상태, 명령 사용 가능 여부, Scene 생성 |
| `Models/SampleInspectionModel.cs` | 카메라별 이미지·ROI·그룹·예제 알고리즘 연결 |
| `Services/DemoImageLoader.cs` | 두 합성 이미지 식별자 처리, 실제 파일은 기존 로더에 위임 |
| `Commands/EditorCommand.cs` | ICommand의 재진입·오류 경계, await 가능한 실행 |
| `Services/IImageFilePicker.cs`, `ImageFilePicker.cs` | 파일 선택 계약과 Windows 대화상자 구현 |

ROI Model은 별도 복제하지 않고 `VisionCanvas.Core`의 불변 DocumentState/Roi와 EditorSession을 사용한다. 카메라·알고리즘 연결 데이터만 샘플 Model에 둔다. ViewModel은 구체 Viewport, Window, Dispatcher, ImageLoader를 생성하거나 참조하지 않는다. 파일 선택만 작은 계약으로 분리하며 나머지는 이미 있는 라이브러리 계약을 쓴다. DI 컨테이너·서비스 로케이터·범용 이벤트 버스는 필요하지 않다.

핵심 연결은 다음 바인딩이다.

```xml
<canvas:Dx12Viewport Scene="{Binding Scene}"
                     PointerInput="{Binding Input}"
                     LayoutState="{Binding Layout}"
                     KeyAction="{Binding KeyAction}"
                     IsInputEnabled="{Binding IsInputEnabled}" />
```

원본 이미지 교체와 이력 복원은 이제 `VisionCanvas.Presentation.ImageCoordinator`를 재사용한다. IImageLoader는 준비된 소스의 소유권을 호출자에게 전달하고, ImageCoordinator는 성공한 소스를 IImageDisplay로 넘기거나 실패·취소 시 해제한다. 라이브러리는 디코더 구현을 선택하지 않는다.

이미지와 ROI 모델은 `await images.LoadDocumentAsync(camera.Image, camera.Rois)`로 함께 불러온다. Core의 `PrepareDocumentAsync`가 이미지 경계·ID/순서·부모/깊이·그룹 포함을 먼저 검증하고, 실제 원본 크기가 모델과 일치할 때만 문서를 게시한다. 반복 격자는 이 일반 ROI 문서 입력에 포함되지 않는다.

직접 프로젝트 참조는 **VisionCanvas + VisionImaging 두 개**다. 새 NuGet 패키지는 없다. 다만 기존 VisionCanvas가 필터 파이프라인과 OpenCvSharp를 전이 참조하므로 바이너리까지 렌더러만의 최소 구성이라고 부르지는 않는다. 그 패키지 분리는 이번 작은 샘플 범위 밖이다.

## 범위와 검증

샘플에 검사 실행·필터 UI·라이브 카메라·대량 ROI 생성·벤치마크·진단 CLI는 없다. Canvas 내부의 장치 관리와 오류 보고는 그대로 사용한다. 8K 예제는 정적 타일 이미지다. 문서 파일 저장 기능이나 기존 티칭 앱의 모든 편집 패널을 대체하지 않는다.

테스트는 `tests/VisionCanvas.MvvmSample.Tests`에만 있으며 샘플 배포물에 포함되지 않는다.

```powershell
dotnet test tests/VisionCanvas.MvvmSample.Tests/VisionCanvas.MvvmSample.Tests.csproj -c Release
```

기능 확인과 설계 원칙별 근거는 [검증 기록](../../docs/visioncanvas-mvvm-sample.md)에 있다. 이 앱의 기능 시험은 엄격한60FPS 성능 보장이 아니다.
