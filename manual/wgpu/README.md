# VisionCanvas wgpu 매뉴얼

C# 기초를 배운 고등학생 눈높이의 [코드 교육 HTML](index.html): 공통 ROI·입력·영상부터 native WebGPU·DX12 backend·WGSL·제출 슬롯까지 실제 코드와 실습으로 읽는다. [교육서 검증·관리](../../docs/renderer-code-guides.md).

**공통 조작:** 방향키1px / Shift+방향키10px 이동, 영상 갱신을 유지하는 `검사 화면 잠금`, 선택을 바꾸지 않는 `ROI 위치 보기 → ROI 맞춤 / 전체 보기`를 제공합니다. [사용법·통합 API·검증](../../docs/canvas-interaction-modes.md).

Windows x64 / .NET 10 / WPF · 2026-10-16

`VisionCanvas.Wgpu`는 이미지·ROI·텍스트·마스크를 wgpu-native로 그린다. 독립 실행 앱은 `VisionCanvas.WgpuSample`이다. 현재 Windows DX12 backend를 사용하며 기존 DX12/Vulkan/Skia 앱은 유지한다.

## 실행

저장소 루트에서 실행한다.

```powershell
dotnet build VisionCanvas.Wgpu.slnx -c Release
dotnet run --project samples/VisionCanvas.WgpuSample -c Release --no-build
```

실행 파일은 `samples/VisionCanvas.WgpuSample/bin/Release/net10.0-windows/VisionCanvas.WgpuSample.exe`다. Visual Studio에서는 `VisionCanvas.Wgpu.slnx`를 열고 `VisionCanvas.WgpuSample`을 시작 프로젝트로 지정한다.

.NET 10 Windows Desktop Runtime, x64, DX12 하드웨어 GPU와 드라이버가 필요하다. Gray16을 보존하기 위해 wgpu-native의 `TextureFormat16bitNorm` 지원을 요구한다. 소프트웨어 adapter는 지원하지 않는다. Rust toolchain, Vulkan SDK, 별도 WGSL 컴파일러는 실행에 필요하지 않다. 파일 코덱은 기존 `VisionImageCodecs.dll`과 MSVC x64 런타임을 사용한다. 새 체크아웃에서 코덱이 없으면 [코덱 빌드 스크립트](../../experiments/VisionImaging.Codecs/Build-Native.ps1)를 실행한다.

하단 상태에서 `wgpu · NVIDIA GeForce RTX 3060 Laptop GPU · DX12`를 확인할 수 있다. 준비 실패 시 `렌더 오류:`에 원인을 표시한다. 다른 렌더러로 자동 대체하지 않는다.

## 앱 사용

| 작업 | 조작 |
|---|---|
| 이미지 | `파일 열기` / Ctrl+O, `8K 예제`는 8,192×8,192 합성 영상 |
| 배율 | `화면 맞춤`, `1:1`, 확대·축소 버튼 또는 휠 |
| 이동 | 가운데 버튼 드래그 또는 `화면 이동` |
| ROI | Rectangle / Circle / Triangle / Polygon 선택 후 추가·그리기 |
| 1억 ROI | `1억 ROI`: 현재 문서에 10,001×10,001 = 100,020,001개의 반복 ROI 추가 |
| 다각형 완료 | Enter 또는 `그리기 완료` |
| 부모 | `부모 추가` / `부모 그리기`, 그룹 하나 선택 후 ROI 추가는 그 그룹의 자식 생성 |
| 선택·변환 | 클릭, Ctrl+클릭 복수 선택, 더블클릭 중첩 선택, 선택 핸들로 크기·각도 변경 |
| 계층 | `그룹화`, `그룹 해제`, 최대 4단계와 부모 경계는 공통 Core 규칙 적용 |
| 복제·삭제 | Ctrl+C / Ctrl+V, Delete, `0° 초기화` |
| 이력 | Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z 또는 Undo/Redo 버튼 |
| 취소 | Esc / `취소`: 그리기·편집·붙여넣기·로딩 취소 |

1:1은 원본 1픽셀을 물리 화면 1픽셀로 표시한다. 단일 ROI와 실제 그룹은 회전할 수 있다. 복수 선택 전용 회전 핸들은 없다. 첫 이미지는 시작 상태이며 이후 성공한 이미지 교체도 이력에 포함된다. 로딩 실패·취소 시 이전 문서와 이력을 유지한다.

파일 피라미드는 `%LOCALAPPDATA%/VisionCanvas.WgpuSample/ImageCache`에 저장한다. 원본 파일을 바꾸지 않는다. 첫 열기는 디코딩·피라미드 준비를 포함하며 다시 열기보다 오래 걸릴 수 있다. 확대하면 overview 이후 원본 타일을 비동기로 표시한다.

샘플은 Canvas 편집 앱이다. 검사 알고리즘·카메라 수집·라인 조립·검사 레시피 저장·필터 설정창은 포함하지 않는다. 검사 결과·필터 미리보기·사용자 레이어의 렌더링 자체는 공통 `RenderScene`과 `Layers`를 통해 지원한다.

### 1억 ROI 테스트

1. `8K 예제`를 연 뒤 `1억 ROI`를 누른다. ROI/선택 수에 `100,020,001`이 표시된다. 기존 ROI가 있으면 그 수에 더해진다.
2. 전체 보기에서는 반복 격자의 축소 요약을 그린다. 휠로 충분히 확대하고 셀을 더블클릭하면 개별 ROI를 선택할 수 있다.
3. 선택한 셀을 드래그하거나 크기/회전 핸들을 조작한다. Delete, Ctrl+C/V, Undo/Redo도 기존 편집 명령을 사용한다.
4. Ctrl+Z로 추가 자체를 되돌릴 수 있다. 이전 문서의 ROI는 보존된다.

가로·세로가 모두556px 이상인 이미지가 필요하다. 작은 이미지는 오류를 표시하고 문서·이력·Redo를 유지한다. 데이터는 공통 Core의 **압축 반복 격자와 개별 수정값**으로 관리한다. 일반 ROI 객체1억 개를 각각 할당하거나 모든 세부 윤곽을 동시에 그리는 테스트는 아니다.

자동 테스트는 개별 셀의 이동·크기·20° 회전·삭제,50,005,000개 부분 선택/이동/복붙 후 총150,025,001개 표시와 Undo/Redo 화면 복원까지 확인한다.

```powershell
dotnet test tests/VisionCanvas.Wgpu.Tests -c Release --filter FullyQualifiedName~HundredMillionTests
```

계측과 캡처는 테스트 프로젝트에만 둔다. [실행 결과와 시간 해석](../../docs/wgpu-validation.md#1억-roi-후속-검증)을 참고한다.

## WPF 통합과 책임

`net10.0-windows`, `UseWPF=true`, `PlatformTarget=x64` 호스트에서 `VisionCanvas.Wgpu.csproj`를 참조한다.

```xml
xmlns:wgpu="clr-namespace:VisionCanvas.Wgpu.Controls;assembly=VisionCanvas.Wgpu"

<wgpu:WgpuViewport x:Name="Canvas"
                   Scene="{Binding Scene}"
                   PointerInput="{Binding Input}"
                   LayoutState="{Binding Layout}"
                   KeyAction="{Binding KeyAction}"
                   IsInputEnabled="{Binding IsInputEnabled}" />
```

완전한 조립 코드는 [App](../../samples/VisionCanvas.WgpuSample/App.xaml.cs), [VM](../../samples/VisionCanvas.WgpuSample/ViewModels/EditorViewModel.cs), [View](../../samples/VisionCanvas.WgpuSample/Views/EditorWindow.xaml)를 따른다. App이 EditorSession·InteractionController·ViewportLayout·ImageCoordinator·파일 선택기를 조립/종료하고, VM이 상태·명령·취소·오류를, View가 바인딩·키 입력·Dispatcher·창 수명을 맡는다. 진단은 별도 테스트 프로젝트에 둔다.

`WgpuViewport`는 기존 `Dx12Viewport`의 HWND·입력·소스 소유권을 상속하고 `CreateRenderer`를 교체한다. 공통 `VisionCanvas` 어셈블리의 Vortice/DX12 전이 참조는 남는다. 실행 시 C#의 기존 `Dx12Device`를 생성하지 않으며 **wgpu-native 내부에서 DX12 장치를 생성한다.** 현재 구현은 Windows WPF 전용이다. wgpu의 다른 backend나 브라우저 WebGPU UI를 지원한다고 해석하면 안 된다.

| API | 소유권·호출 계약 |
|---|---|
| `SetSource(ITileSource)` | 성공 후 소스는 Canvas가 소유한다. 반환 generation을 Scene에 반영한다. 일반 파일 교체는 ImageCoordinator를 사용한다. |
| `PublishFrame(VideoFrame)` | 소유권을 전달하므로 호출 후 Pixels 수정·Dispose를 하지 않는다. 최신 프레임으로 병합한다. |
| `RetainFrame()` | 반환 참조는 호출자가 Dispose한다. |
| `Layers` | Image/Screen 좌표의 사용자 표시, 문서 이력과 별도다. |
| `Capture(path)` | 보이는 타일·라벨 준비 후 PNG를 저장하는 비동기 요청이다. 완료 전에 파일을 읽지 않는다. |
| `RestartAsync()` | UI 스레드에서 호출한다. 렌더러를 재생성하고 최신 소스·장면을 복원한다. |
| `Dispose()` | 창 종료에서 호출한다. 렌더 스레드/GPU 완료를 기다린 다음 HWND를 해제한다. |

라이브 입력은 `Scene.IsLive=true`와 일치하는 `ImageInfo`를 설정한다. 직접 7,680×4,320 Gray8/Gray16 프레임과 재시작 보존을 검증했지만 카메라 수집이나 지속 FPS 검증은 아니다. `DevicePreference.Software`는 거부한다.

## 렌더링과 수명

`WgpuDevice`는 instance·HWND surface·adapter·device·queue를 소유한다. `WgpuSurface`는 FIFO 표시와 resize, 두 제출 슬롯을 관리한다. 슬롯 재사용 전에 해당 submission 완료를 기다리고 bind group·버퍼 영역·지연 해제 목록을 반환한다. 두 슬롯은 CPU/GPU 작업 자원 수이며 swapchain 이미지 수를 뜻하지 않는다. resize와 종료는 GPU 완료를 기다린다.

`WgpuRenderWorker`의 전용 STA 스레드만 GPU를 사용한다. 공통 ScenePainter·타일·라벨·필터·최신 프레임 처리를 재사용한다. 텍스처·버퍼·파이프라인·WGSL·readback만 새 구현에 둔다. 콜백은 오류를 큐에 보관하고 unmanaged 경계 밖으로 예외를 던지지 않는다. 버퍼/텍스처 교체는 마지막 사용이 끝난 뒤 해제한다.

Gray8/RGBA/BGRA는 R8/RGBA8/BGRA8 UNORM으로, Gray16은 native R16Unorm으로 업로드한다. Gray16 원본을 CPU에서 8비트로 줄이지 않는다. 최종 화면은 8비트 UNORM이므로 표시 양자화는 존재한다. 네 WGSL 셰이더는 기존 HLSL의 좌표·상수·mask/반복 격자 계산을 옮겼으며 실행 시 wgpu가 검증·컴파일한다. 일반 표시에는 GPU→CPU 복사가 없고 명시적인 Capture에서만 readback한다.

타일 퇴출 목표는 **논리 픽셀 기준 256MiB**이며 실제 GPU 메모리 예산 조회값이 아니다. 가시 타일과 아직 GPU에서 사용하는 자원 때문에 일시적으로 초과할 수 있다. ROI 보관 목표는 128MiB이며 활성 블록은 유지한다. 프레임별 업로드 페이지·라이브 영상·라벨·필터·surface·wgpu 내부 staging도 별도 메모리다. 총 프로세스/GPU 메모리 상한으로 표현하지 않는다. 512px 타일·준비 큐32개와 기존 취소·세대 계약을 유지한다.

공통 metrics는 CPU 측 처리·대기와 논리 텍스처 바이트를 보고한다. GPU timestamp나 모니터 scan-out 측정이 아니다. wgpu validation을 켰지만 D3D12 debug layer를 별도로 설치·검증한 것은 아니다.

## 검증·배포·제약

```powershell
dotnet test tests/VisionCanvas.Wgpu.Tests -c Release
$env:VISIONCANVAS_WGPU_KEN32 = (Resolve-Path 'ken32.png').Path
dotnet test tests/VisionCanvas.Wgpu.Tests -c Release
```

환경 변수가 없으면 ken32 시험 한 건만 skip한다. [검증 기록](../../docs/wgpu-validation.md)에 실제 픽셀·8K·ken32·기존 회귀와 미검증 범위를 구분했다. 기능 통과를 DX12/Vulkan 대비 성능 우위나 엄격한 60FPS 달성으로 표현하지 않는다.

```powershell
dotnet publish samples/VisionCanvas.WgpuSample -c Release -r win-x64 --self-contained false -o artifacts/wgpu-publish
```

배포 시 출력 폴더 전체를 복사한다. EXE/관리 DLL, `wgpu_native.dll`, `Shaders/*.wgsl`, 영상 코덱 DLL, `licenses`, `THIRD-PARTY-NOTICES.md`, `WGPU-MANUAL.md`가 필요하다. .NET Windows Desktop Runtime과 GPU 드라이버는 별도다. publish 명령은 다른 PC에서의 배포 검증을 대신하지 않는다.

고정 의존성은 `Alimer.Bindings.WebGPU 1.6.0`과 `Alimer.WebGPU.Native 1.0.4`이며 실제 native 버전 조회값은 `27.0.4.0`이다. [제3자 고지](../../THIRD-PARTY-NOTICES.md)에 MIT/Apache 전문과 출처를 기록했다. 네이티브 바이너리는 변경하지 않았다.

| 증상/제약 | 확인 |
|---|---|
| DLL·셰이더 없음 | 출력 폴더 전체와 x64 실행 확인 |
| adapter/Gray16 미지원 | DX12 드라이버와 R16Unorm 기능 지원 확인 |
| 파일 열기 실패 | 원본 경로·권한·코덱·캐시 디스크 공간 확인. 이전 문서 유지 |
| 드라이버 정보 공란 | native adapter 메타데이터가 빈 문자열을 반환할 수 있음. 드라이버 버전·메모리 예산을 추정하지 않음 |
| 장치 손실 | 강제 제거·복구 미검증. 고정 native 구현은 일부 queue submit 실패 시 프로세스를 중단할 수 있어 항상 복구 가능하다고 보장하지 않음 |

지속 60FPS, 장시간 메모리 안정성, 혼합 DPI 모니터 이동, 실제 카메라와 타 GPU/다른 backend는 NOT_RUN이다. 설계 근거는 [ADR-068](../../docs/decisions/0068-wgpu-canvas-and-sample.md)를 따른다.

첫 읽기에서는 앞부분의 일곱 단어와 1~7장의 상황·작은 계산·조작 모형을 따라간다. 8~12장은 실제 구현과 자원 관리의 심화 읽기다. 비유 다음에 실제 클래스와 메서드를 연결하며 코드·수식·검증 한계는 유지한다.
