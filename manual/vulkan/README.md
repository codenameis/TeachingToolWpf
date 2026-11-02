# VisionCanvas Vulkan 전용 매뉴얼

C# 기초를 배운 고등학생 눈높이의 [코드 교육 HTML](index.html): 공통 ROI·입력·영상부터 Vulkan acquire·render pass·fence·semaphore·SPIR-V까지 실제 코드와 실습으로 읽는다. [교육서 검증·관리](../../docs/renderer-code-guides.md).

**공통 조작:** 방향키1px / Shift+방향키10px 이동, 영상 갱신을 유지하는 `검사 화면 잠금`, 선택을 바꾸지 않는 `ROI 위치 보기 → ROI 맞춤 / 전체 보기`를 제공합니다. [사용법·통합 API·검증](../../docs/canvas-interaction-modes.md).

Windows x64 / .NET 10 / WPF · 2026-10-16

`VisionCanvas.Vulkan`은 기존 비전 Canvas의 입력·문서·장면을 재사용하면서 실제 영상과 ROI를 Vulkan으로 그리는 라이브러리다. 독립 실행 앱은 `VisionCanvas.VulkanSample`이다. 기존 TeachingToolWpf의 기본 DX12 렌더러는 유지한다.

## 1. 실행

저장소 루트에서 다음 명령을 실행한다.

```powershell
dotnet build VisionCanvas.Vulkan.slnx -c Release
dotnet run --project samples/VisionCanvas.VulkanSample -c Release --no-build
```

실행 파일은 `samples/VisionCanvas.VulkanSample/bin/Release/net10.0-windows/VisionCanvas.VulkanSample.exe`다. Visual Studio에서는 `VisionCanvas.Vulkan.slnx`를 열고 `VisionCanvas.VulkanSample`을 시작 프로젝트로 지정한다.

필수 조건은 .NET 10 Windows Desktop Runtime, x64, Vulkan 1.1 이상의 하드웨어 GPU와 드라이버다. 빌드에는 .NET SDK와 NuGet 복원이 필요하다. 파일 코덱은 기존 `VisionImageCodecs.dll`과 MSVC x64 런타임을 사용한다. 코덱이 없는 새 체크아웃은 [코덱 빌드 스크립트](../../experiments/VisionImaging.Codecs/Build-Native.ps1)를 먼저 실행한다. Vulkan SDK는 앱 실행에 필요하지 않다. `vulkan-1.dll`은 GPU 드라이버가 제공하며 임의의 DLL 다운로드로 대체하지 않는다.

현재 로컬 장비는 RTX 3060 Laptop GPU로 실제 실행했다. 앱 하단에 `Vulkan · NVIDIA GeForce RTX 3060 Laptop GPU`와 API 버전·메모리 예산·타일 캐시·FIFO가 표시된다. 이 문구가 `렌더 오류:`로 바뀌면 오류 내용을 확인한다. Vulkan 실패를 DX12나 소프트웨어 렌더링으로 자동 대체하지 않는다.

## 2. 앱 사용

| 작업 | 사용법 |
|---|---|
| 파일 열기 | `파일 열기` 또는 Ctrl+O. 완성된 대용량 이미지 파일을 연다. |
| 예제 | `8K 예제`: 8,192×8,192 합성 타일 영상. 실제 카메라 입력은 아니다. |
| 1억 ROI | `1억 ROI`: 현재 이미지 중심에 10,001×10,001 = 100,020,001개의 반복 ROI를 추가한다. 추가 한 번이 Undo 한 건이다. |
| 화면 맞춤 / 원본 | `화면 맞춤`, `1:1`. 1:1은 이미지 1픽셀을 물리 화면 1픽셀로 표시한다. |
| 확대 / 이동 | 휠 또는 확대·축소 버튼, 가운데 버튼 드래그 또는 `화면 이동`. |
| 도형 선택 | Rectangle(사각형), Circle(원/타원), Triangle(삼각형), Polygon(다각형). |
| ROI 추가 / 그리기 | 추가는 화면 중심, 그리기는 드래그. 다각형은 점을 입력하고 Enter 또는 `그리기 완료`. |
| 부모 추가 / 그리기 | 빈 그룹을 만든다. 실제 그룹 하나 선택 후 ROI 추가/그리기는 그 그룹의 자식을 만든다. |
| 선택 / 변환 | 클릭·드래그 선택, Ctrl+클릭 복수 선택, 더블클릭 중첩 선택, 선택 핸들로 크기·각도 변경. |
| 그룹화 / 해제 | 선택한 일반 ROI를 그룹화하거나 선택 그룹을 해제한다. 공통 계층·경계 규칙을 그대로 적용한다. |
| 복사 / 붙여넣기 | Ctrl+C / Ctrl+V. 붙여넣기 준비는 취소할 수 있고 성공 시 이력 한 건이다. |
| 삭제 / 각도 초기화 | Delete 또는 `삭제`, `0° 초기화`. |
| 이력 | Ctrl+Z, Ctrl+Y 또는 Ctrl+Shift+Z. 이미지 교체도 이력에 포함하며 첫 이미지는 시작 상태다. |
| 취소 | Esc 또는 `취소`. 현재 그리기·편집·파일 로딩·붙여넣기 준비를 취소한다. |

부모 경계·최대 4단계·기존 그룹 확장·일반 자식의 경계 제한은 `VisionCanvas.Core`의 현재 규칙을 따른다. 복수 선택의 별도 회전 핸들은 제공하지 않으며 단일 ROI와 실제 그룹의 회전은 가능하다. 잘못된 편집과 파일 로딩 오류는 하단 상태에 표시한다. 준비 중에는 문서 변경과 포인터 입력을 막고 취소를 제공한다. 로딩 실패·취소는 기존 문서를 보존한다.

파일 피라미드는 `%LOCALAPPDATA%/VisionCanvas.VulkanSample/ImageCache`에 보관한다. 기존 앱의 캐시와 별도 경로이며 원본 파일을 수정하지 않는다. 최초 파일 준비는 디코딩과 디스크 피라미드 작성이 포함되어 재열기보다 오래 걸릴 수 있다. 표시가 축소되어도 원본 좌표와 원본 타일은 유지한다.

### 1억 ROI 직접 재현

1. `8K 예제`를 연 뒤 `1억 ROI`를 누른다. 하단 ROI/선택 수가 각각 `100,020,001`인지 확인한다. 기존 ROI가 있으면 그 수에 더해진다.
2. 격자 중심을 충분히 확대하고 셀을 더블클릭한다. 선택 수가 1이 되면 드래그로 이동하고 핸들로 크기·각도를 바꾼다. Delete와 Ctrl+Z로 개별 삭제·복원을 확인한다.
3. 영역 드래그로 일부를 선택한 뒤 Ctrl+C/Ctrl+V, Ctrl+Z/Ctrl+Y로 복사·붙여넣기와 이력을 확인한다. 반복 배치를 추가한 직후 Ctrl+Z를 누르면 배치 전체가 취소된다.

가로·세로가 모두 556px 이상인 이미지가 필요하다. 작은 이미지는 오류 문구와 함께 거부하고 문서를 보존한다. 공통 Core의 **압축 반복 격자와 개별 수정값**을 사용한다. 전체 보기에서는 축소 요약으로 표시하며 확대하면 셀을 편집한다. 일반 ROI 객체 1억 개를 각각 할당하거나 전체 세부 윤곽을 동시에 그리는 시험은 아니다. 시간 측정과 검증 코드는 앱이 아닌 별도 테스트 프로젝트에 있다.

이 앱은 Canvas 편집 샘플이다. 메인 앱의 패턴 검사·학습·필터 설정창·검사 레시피 저장·두 카메라 모델 UI를 복제하지 않았다. 실제 카메라 수집이나 라인 조립도 구현하지 않는다. 해당 서비스가 만든 결과·필터 미리보기·레이어는 공통 `RenderScene`과 `Layers` API로 Vulkan에 표시할 수 있다.

## 3. 프로젝트 구조와 책임

| 위치 | 책임 |
|---|---|
| `src/VisionCanvas.Vulkan/Controls/VulkanViewport.cs` | Vulkan 렌더러를 선택하는 WPF 컨트롤 |
| `Rendering/VulkanDevice.cs` | instance, Win32 surface, GPU·queue 선택, 메모리 형식·예산 |
| `Rendering/VulkanSurface.cs` | swapchain, render pass, 프레임 fence, present semaphore, resize·지연 해제 |
| `Rendering/VulkanRenderWorker.cs` | 렌더 스레드, 최신 장면/프레임, 업로드·그리기·표시·캡처 순서 |
| `Rendering/VulkanPipeline.cs` | 그래픽 파이프라인, descriptor, 프레임별 업로드 영역 |
| `Rendering/VulkanSpritePipeline.cs` | 영상·텍스트·레이어 텍스처와 sprite mesh |
| `Rendering/VulkanRoiPipeline.cs` | BVH 가시 영역·축소 요약·유지되는 ROI 인스턴스·선택 역할 |
| `Rendering/VulkanMaskPipeline.cs` | 마스크 union 표시 |
| `Rendering/VulkanRepeatedPipeline.cs` | 압축 반복 격자·부분 셀 표시 |
| `Rendering/VulkanShaderCompiler.cs` | 기존 HLSL의 SPIR-V 컴파일과 UTF-8 native 경계 |
| `Rendering/VulkanReadback.cs` | 명시적 화면 캡처 시에만 GPU→CPU 읽기 |
| `samples/VisionCanvas.VulkanSample/App.xaml.cs` | 서비스 조립·앱 종료 |
| `ViewModels/EditorViewModel.cs` | 화면 상태·명령·취소·오류 문구 |
| `Views/EditorWindow.xaml[.cs]` | 바인딩·키 입력·Dispatcher·컨트롤 수명 |
| `tests/VisionCanvas.Vulkan.Tests` | GPU·픽셀 비교·실제 WPF 창·파일 검증. 샘플에 진단 코드를 넣지 않는다. |

`VisionCanvas.Core`가 도형·좌표·공간 인덱스·계층·입력·Undo/Redo를 담당한다. 기존 `VisionCanvas`의 장면 배치, 텍스트, 타일/라이브 준비, 레이어, 필터 미리보기, 이미지 교체 서비스를 ProjectReference로 재사용한다. 다른 프로젝트의 C# 소스를 링크하거나 도메인 코드를 복제하지 않는다.

현재 `VulkanViewport`는 기존 `Dx12Viewport`의 HWND 입력·바인딩·소스 소유권을 상속하고, `CreateRenderer`만 Vulkan으로 교체한다. 이름에 DX12가 남아 있어도 Vulkan 경로에서 DX12 device를 만들지 않는다. 공통 Canvas 어셈블리에 DX12 구현도 있으므로 Vortice.Direct3D12 등의 패키지는 전이 참조에 남는다. **DX12 의존성이 완전히 제거된 배포물이나 Linux/macOS용 UI는 아니다.** 이를 제거하는 공통 어셈블리 재분할은 이번 포팅과 별도다.

## 4. 다른 WPF 앱에 연결

호스트 프로젝트는 `net10.0-windows`, `UseWPF=true`, `PlatformTarget=x64`로 설정하고 `VisionCanvas.Vulkan.csproj`를 참조한다.

```xml
<ProjectReference Include="../VisionCanvas.Vulkan/VisionCanvas.Vulkan.csproj" />
```

```xml
xmlns:vk="clr-namespace:VisionCanvas.Vulkan.Controls;assembly=VisionCanvas.Vulkan"

<vk:VulkanViewport x:Name="Canvas"
                   Scene="{Binding Scene}"
                   PointerInput="{Binding Input}"
                   LayoutState="{Binding Layout}"
                   KeyAction="{Binding KeyAction}"
                   IsInputEnabled="{Binding IsInputEnabled}" />
```

완전한 조립 예제는 샘플의 App·VM·View를 참고한다. `EditorSession`, `InteractionController`, `ViewportLayout`, `ImageCoordinator`를 사용한다. VM은 `VulkanDevice`나 구체 GPU 파이프라인을 직접 참조하지 않는다. 장면·레이어 변경은 UI 스레드에서 수행하고 GPU API 호출은 렌더 스레드 하나에 한정한다.

| API | 계약 |
|---|---|
| `SetSource(ITileSource)` | 성공 후 소스 해제 책임이 Canvas로 넘어간다. 반환 generation을 Scene에 반영한다. 일반 파일 앱은 ImageCoordinator를 권장한다. |
| `PublishFrame(VideoFrame)` | 프레임 소유권을 전달한다. 전달 뒤 호출자가 Pixels를 수정하거나 Dispose하지 않는다. 소비가 늦으면 최신 프레임으로 합친다. |
| `ClearFrames()` / `RetainFrame()` | 라이브 프레임을 비우거나 별도 참조를 얻는다. RetainFrame 결과는 호출자가 Dispose한다. |
| `Layers` | 이름 있는 Image/Screen 좌표 레이어. ROI 문서·이력과 별도다. |
| `Capture(path)` | 비동기 캡처 요청. 현재 요청 장면의 보이는 타일·라벨 준비 후 PNG를 저장한다. 즉시 파일이 생긴다고 가정하지 않는다. |
| `RestartAsync()` | UI 스레드에서 호출. 동시 요청은 하나로 합치며 GPU를 재생성하고 최신 소스·장면을 다시 표시한다. |
| `Dispose()` | 창 종료 때 호출한다. 렌더 스레드·GPU 작업 완료 뒤 자원을 해제한다. |

라이브 입력은 Scene의 `IsLive=true`와 일치하는 `ImageInfo`를 설정한다. 7,680×4,320 Gray8/Gray16 프레임 표시와 재시작 보존을 테스트했다. 이 사실은 카메라 수집 또는 지속 60FPS 달성을 뜻하지 않는다. `DevicePreference.Software`는 Vulkan에서 지원하지 않으며 표시 전에 설정하면 명시적으로 거부한다.

## 5. 렌더링과 메모리

Vulkan 1.1 graphics와 present가 같은 queue family에서 가능한 GPU를 선택하며 discrete GPU를 우선한다. 화면은 BGRA8 UNORM, FIFO present를 사용한다. swapchain 이미지 수는 surface가 허용하는 최소값 이상이며 최소 2개를 요청한다. CPU/GPU 작업용 프레임 슬롯은 2개다.

각 슬롯의 fence가 끝난 뒤 command pool·descriptor pool·업로드 영역을 재사용한다. present 완료용 semaphore는 프레임 슬롯 대신 **swapchain 이미지별**로 둔다. swapchain 재생성·종료 때 device idle을 기다린다. 타일·라벨·ROI 버퍼 교체는 즉시 해제하지 않고 마지막 사용 프레임의 fence 뒤에 반환한다. 캡처 버퍼도 같은 지연 해제 목록에 넣어 제출 후 Present 오류가 발생해도 수명을 보존한다. GPU→CPU 캡처에는 TransferWrite→HostRead 배리어와 완료 대기를 모두 적용한다. [Khronos 동기화 예제](https://docs.vulkan.org/guide/latest/synchronization_examples.html), [present semaphore 재사용](https://docs.vulkan.org/guide/latest/swapchain_semaphore_reuse.html).

원본 Gray8/Gray16은 R8/R16 UNORM 텍스처로 업로드하며 BGRA로 확장하지 않는다. 색 영상은 원래 RGBA/BGRA 채널 순서를 따른다. 파일은 기존 512px 타일·32개 준비 큐·원본 디스크 피라미드를 사용한다. GPU 타일 예산은 시작 시 최대 256MiB 또는 보고된 local memory budget의 20%이며 기존 하한 16MiB를 적용한다. `VK_EXT_memory_budget`이 없으면 heap 크기를 대체 기준으로 쓰고 상태에는 `Budget unavailable`을 표시한다. 이 값은 실행 중 동적 예산 감시가 아니다.

타일 캐시 상한은 전체 프로세스/GPU 메모리 상한이 아니다. swapchain·라벨·필터·현재 프레임·전송·지연 해제 자원이 추가된다. ROI 캐시는 128MiB를 기준으로 비활성 블록을 퇴출하지만 현재 보이는 블록은 누락을 막기 위해 유지한다. 프레임별 업로드 페이지와 descriptor pool은 필요한 만큼 늘고 fence 완료 후 재사용한다. 따라서 매우 많은 동시 표시 도형의 실제 상한·장기 메모리는 별도 측정이 필요하다.

셰이더는 기존 `Shaders/Sprite.hlsl`, `Roi.hlsl`, `Mask.hlsl`, `RepeatedRoi.hlsl`을 그대로 사용한다. shaderc가 최초 렌더러 준비 시 SPIR-V로 컴파일한다. `t0/t1/b0/s0/s1`을 descriptor 0/1/2/3/4로 매핑하고 vertex Y를 반전한다. ROI의 176바이트 상수는 Vulkan 최소 push constant 보장 128바이트를 넘으므로 uniform buffer를 사용한다. 한글 주석이 있는 HLSL도 정확히 넘기도록 UTF-8 **바이트 길이**를 전달하는 작은 native 경계를 둔다.

## 6. 검증과 성능 해석

명령과 상세 결과는 [Vulkan 검증 기록](../../docs/vulkan-validation.md)에 있다.

```powershell
dotnet test tests/VisionCanvas.Vulkan.Tests -c Release
dotnet test tests/VisionCanvas.Tests -c Release

# 1억 ROI의 Vulkan 표시·개별 편집·대량 복붙·이력만 실행
dotnet test tests/VisionCanvas.Vulkan.Tests -c Release --filter FullyQualifiedName~HundredMillionTests

# 대용량 로컬 파일 검증을 포함할 때
$env:VISIONCANVAS_VULKAN_KEN32 = (Resolve-Path 'ken32.png').Path
dotnet test tests/VisionCanvas.Vulkan.Tests -c Release
```

ken32 환경 변수가 없으면 그 파일 시험만 명시적으로 건너뛴다. 파일은 Git에 추가하지 않는다. 실제 파일 검증은 원본 17,280×30,720 로딩·축소 표시·원본 배율 타일을 확인하며 전체 픽셀의 GPU 출력 전수 비교나 검사 알고리즘 정확도 검증은 아니다.

현재 검증에는 4가지 픽셀 형식과 방향/색 채널, DX12 대비 ROI·한글 라벨·마스크·필터·결과/내부 윤곽·레이어 화면 비교, 1억 반복 격자 중 개별 셀의 포인터 선택·이동·크기·회전·삭제, 50,005,000개 부분 선택·이동·복붙 후 총150,025,001개 표시와 Undo/Redo, 8K 정적/라이브, resize·최소화·재시작, 앱 명령·이력 등이 포함된다. ken32 원본 배율은 타일 경계를 포함한30위치의 RGB를 원본 타일과 직접 비교했다. DX12 비교는 래스터 경계의 미세 차이를 허용하는 테스트이며 비트 단위 동일성을 주장하지 않는다.

`MetricsChanged`의 CPU 처리·대기 값과 GPU 완료 통지는 물리 모니터 scan-out이 아니다. GPU timestamp 측정은 아직 연결하지 않았으며 값이 없을 때 0ms로 바꾸지 않는다. 지속 8K 60FPS·DX12 대비 속도 우위·30만 일반 ROI 최악 지연은 이번 작업에서 검증하지 않았다. 기존 DX12의 시간 분할 ROI 준비와 동일한 성능 특성을 약속하지 않는다. 현재 장비에는 Khronos validation layer가 없으므로 validation 메시지 0건을 합격 근거로 쓰지 않는다.

## 7. 배포와 문제 확인

```powershell
dotnet publish samples/VisionCanvas.VulkanSample -c Release -r win-x64 --self-contained false -o artifacts/publish/VisionCanvas.VulkanSample
```

publish 폴더 전체를 함께 사용한다. 특히 `shaderc_shared.dll`, 코덱 DLL, 관리 DLL, `Shaders` 폴더, `licenses`, `THIRD-PARTY-NOTICES.md`, `VULKAN-MANUAL.md`가 필요하다. 실행 파일 하나만 복사하거나 HLSL을 제거하지 않는다. GPU 드라이버와 .NET Windows Desktop Runtime은 별도로 설치한다. 이 명령은 다른 PC에서의 배포 검증을 대신하지 않는다.

새 의존성은 Vortice.Vulkan 3.2.3, Vortice.ShaderCompiler 1.9.0, 전이 native 패키지 Silk.NET.Shaderc.Native 2.23.0이다. 라이선스 및 포함 shaderc/glslang/SPIR-V 고지는 [제3자 고지](../../THIRD-PARTY-NOTICES.md)를 따른다. 네이티브 바이너리는 수정하지 않았다.

| 증상 | 확인할 내용 |
|---|---|
| Vulkan loader/장치를 찾지 못함 | x64 실행·GPU 드라이버·Vulkan 1.1 지원. 원격 환경과 다른 GPU는 별도 검증 대상. |
| shaderc DLL 없음 / 셰이더 오류 | publish 전체 파일, Shaders 폴더, native DLL을 확인한다. 오류에 파일·entry point가 나온다. |
| 파일 로딩 실패 | 경로·읽기 권한·캐시 공간·코덱 런타임을 확인한다. 기존 문서는 유지된다. |
| NU1900 | NuGet 취약성 피드 접근 실패다. 패키지 빌드 성공과 취약성 조회 성공을 구분한다. |
| 확대 후 선명해지는 데 시간이 걸림 | 낮은 해상도 overview 뒤에 원본 타일을 비동기로 읽는다. 전체 원본을 매 프레임 GPU에 올리지 않는다. |
| 장치 오류 / surface lost | 오류를 보존한다. 호스트는 원인을 해결한 뒤 RestartAsync를 호출할 수 있다. 장치 제거 강제 주입 시험은 미실행이다. |

설계 근거: [ADR-066](../../docs/decisions/0066-vulkan-canvas-and-sample.md). 모든 변경은 로컬이며 커밋·푸시는 수행하지 않았다.

첫 읽기에서는 앞부분의 일곱 단어와 1~7장의 상황·작은 계산·조작 모형을 따라간다. 8~12장은 실제 구현과 자원 관리의 심화 읽기다. 비유 다음에 실제 클래스와 메서드를 연결하며 코드·수식·검증 한계는 유지한다.
