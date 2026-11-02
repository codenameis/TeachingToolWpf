# VisionCanvas wgpu 포팅

사용자 승인(2026-10-16): 별도 wgpu Canvas와 실행 가능한 WPF 앱까지 구현한다. 현재 main에서 로컬 수정·검증만 진행한다.

## 범위와 순서

1. `VisionCanvas.Wgpu`에서 wgpu-native의 Windows HWND surface와 하드웨어 adapter를 연결한다. C# 바인딩/네이티브 버전을 고정하고 장치 생성·종료를 테스트한다.
2. 공통 Core, HWND 입력, RenderScene, 타일/라이브 준비, 라벨/필터 배치를 재사용한다. GPU 버퍼·텍스처·파이프라인·표시·readback만 wgpu로 구현한다. 셰이더는 WGSL로 이식한다.
3. `VisionCanvas.WgpuSample` WPF MVVM 앱과 별도 솔루션을 추가한다. 파일 열기, 8K 예제, ROI 생성·선택·편집·계층·Undo/Redo를 기존 샘플과 같은 방식으로 제공한다. App은 조립/종료, VM은 상태/명령, View는 UI 전달을 맡는다.
4. xUnit의 별도 GPU 테스트에서 Gray8/Gray16/RGBA/BGRA 픽셀, ROI/마스크/반복 격자, resize/재시작/종료, 8K·ken32와 앱 동작을 검증한다. 기존 Canvas/Core 회귀도 실행한다.
5. 매뉴얼, ADR, 배포 의존성/라이선스와 실측·미검증 범위를 기록한다.

## 계약

- 기존 DX12/Vulkan/Skia 기본 동작을 보존한다. 공통 어셈블리의 DX12 전이 참조는 유지하되 wgpu 실행은 별도 렌더러를 사용한다.
- 512px 타일·준비 큐32·Gray16 원본·최신 프레임·세대/취소·GPU 완료 후 자원 재사용을 유지한다. 장치 메모리 예산을 조회할 수 없으면 실제 예산으로 꾸미지 않고 논리 바이트 캐시 퇴출 목표를 명시한다.
- C# Allman/4공백/기존 명명 규칙과 명시적인 IDisposable 소유권을 따른다. unsafe는 GPU interop 안으로 제한한다.
- 진단은 테스트에서 실행한다. 실제 카메라 수집·검사 알고리즘·비 Windows UI·배포/푸시는 범위 밖이다.
- 구현 성공은 실제 하드웨어의 픽셀/기능/수명 검증으로 판단한다. 빌드 성공과 60FPS·지연 목표 달성을 구분한다.

## 명령

```powershell
dotnet build VisionCanvas.Wgpu.slnx -c Release
dotnet test tests/VisionCanvas.Wgpu.Tests/VisionCanvas.Wgpu.Tests.csproj -c Release
dotnet run --project samples/VisionCanvas.WgpuSample/VisionCanvas.WgpuSample.csproj -c Release
pwsh -File scripts/Format-Code.ps1 -Verify
```

## 진행

- [x] 장치/수명: RTX3060·wgpu-native27.0.4.0·DX12, 두 슬롯 재사용·6프레임 픽셀.
- [x] GPU 표시/픽셀/기존 장면: Gray8/16/RGBA/BGRA·WGSL4개·복합 장면 DX12 비교.
- [x] WPF 앱: 독립 EXE 시작/정상 종료·App/VM/View·편집/이력 시험.
- [x] 8K·ken32·회귀·문서: wgpu15건/Core396건/DX12 Canvas245건, [검증과 제약](../docs/wgpu-validation.md), [매뉴얼](../manual/wgpu/README.md).

지속60FPS·장기 메모리·강제 장치 손실 복구·다른 GPU/backend는 NOT_RUN이다. 기존 전체 형식 위반은 보존한다.

## 2026-10-16 1억 ROI 후속

- [x] 샘플 `1억 ROI` 버튼·기존 Core의100,020,001셀 추가·기존 ROI 보존·이미지 최소 크기 검사.
- [x] 별도 테스트3건: 개별 포인터 이동/크기/회전/삭제,50,005,000개 부분 선택/이동/복붙,Undo/Redo 화면 복원·실패의 Redo 보존.
- [x] ken32 포함 wgpu 전체18건 통과. [상세 범위·명령/캡처 시간](../docs/wgpu-validation.md#1억-roi-후속-검증).
- [ ] 지속60FPS·일반1억 객체 할당·전체 GPU 메모리/장기 안정성은 이번 시험 범위가 아니다.
