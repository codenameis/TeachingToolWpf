# wgpu Canvas 구현·검증

2026-10-16, Windows x64 / .NET 10.13 / NVIDIA GeForce RTX 3060 Laptop GPU. [ADR-068](decisions/0068-wgpu-canvas-and-sample.md), [실행 매뉴얼](../manual/wgpu/README.md).

## 결과

| 항목 | 결과 | 실제 범위 |
|---|---|---|
| 별도 솔루션 Release 빌드 | PASS | 경고0/오류0, WPF 앱·renderer·테스트 |
| 기존 TeachingToolWpf Release 빌드 | PASS | 경고0/오류0 |
| 독립 EXE 시작/종료 | PASS | 실제 App 진입점의 창 제목 확인·정상 종료 코드0 |
| win-x64 publish | PASS | 배포 폴더 EXE의 wgpu_native.dll 실제 로드·정상 종료 코드0, 타 PC 실행은 미검증 |
| wgpu 테스트 | PASS 18건 | ken32 환경 변수 지정, skip0. 포팅15건 + 1억 ROI 후속3건 |
| 공통 Core 회귀 | PASS 396건 | 문서·기하·계층·입력·이력 |
| 기존 DX12 Canvas 회귀 | PASS 245건 | 기존 렌더·소스·컨트롤 테스트 |
| Gray8/Gray16/RGBA/BGRA | PASS | 실제 native texture 업로드·채널·좌상단 방향·readback |
| 연속 6프레임 | PASS | 두 슬롯 재사용·같은 Gray16 texture 갱신·모든 제출 뒤에 readback 저장/대조 |
| 복합 장면 | PASS | ROI 도형·한글·마스크·필터·검사 윤곽/내부 윤곽·사용자 레이어, DX12 WARP 캡처와 비교 |
| WPF 편집 앱 | PASS | 실제 창의 바인딩/명령·8K·ROI 그리기·그룹·복붙·연쇄 삭제·Undo/Redo·새 편집의 Redo 제거 |
| 실패·취소 | PASS | 실패/취소 파일 교체의 기존 문서·generation·이력 보존 |
| 8K 정적/라이브 | PASS | 8192² 합성 타일, 7680×4320 Gray8/Gray16 직접 프레임·재시작 보존 |
| resize/최소화/복원 | PASS | 실제 창 크기 변경·최소화/복원·renderer 재시작·문서/이력 보존 |
| 4만 일반 ROI | PASS | fit/확대 표시, 지오메트리 보관·가시 영역 처리; 전수 개별 윤곽 FPS 측정은 아님 |
| 반복 격자 | PASS | 100,020,001셀 추가·포인터 개별 편집·50,005,000셀 부분 복붙 후 총150,025,001개 표시·Undo/Redo |
| ken32 | PASS | 17,280×30,720 RGBA 원본·7단계 피라미드·fit·원본1:1 타일 픽셀 |

네 WGSL 파이프라인을 실제 장치에서 생성하고 렌더링했다. Gray16 테스트는 상·하위 바이트가 다른 값을 포함한다. 연속 프레임 시험은 6개 제출 뒤에 각 readback을 읽어 매 프레임 Save/WaitIdle로 슬롯 문제를 숨기지 않는다. 첫 픽셀은 16비트 정규화 반올림과 단순 상위8비트 절단을 구분하도록 정확히 비교하고, 나머지는 UNORM16→float→UNORM8 표시 양자화 ±1을 허용한다.

DX12 비교 허용치는 RGB 합산 오차12 초과 픽셀1.5% 미만 및 평균 채널 오차1.5 미만이다. 전체 비트 동일성을 주장하지 않는다. 최초 비교에서 배경색 차이를 발견했고 기존 DX12 clear 값으로 수정한 뒤 같은 기준으로 통과했다. 테스트 감지력 확인을 위해 grayscale 조건을 임시 반전했을 때 픽셀/연속 프레임 시험5건이 모두 실패했다. 원본을 복원한 전체15건은 통과했다.

ken32 원본 대조는 서로 다른 타일과 경계를 포함한 화면30위치·RGB 세 채널 ±1이다. 축소 overview가 남은 상태를 원본 타일 검증으로 통과시키지 않는다. 원본 전체 픽셀의 전수 대조나 현장 영상 검출 정확도 시험은 아니다. 샘플 화면과 원본 타일 PNG도 직접 확인했다.

## 1억 ROI 후속 검증

`8K 예제` → `1억 ROI`로 기존 문서에 공통 Core의10,001×10,001 압축 배치를 추가한다. 샘플 VM의 명령과 XAML 버튼만 연결했으며 Core/renderer 알고리즘은 변경하지 않았다. MVVM 조립·편집 명령·자원 소유권은 기존 책임을 유지하고 성능 진단은 앱에 넣지 않았다.

`HundredMillionTests`3건과 실제 샘플 창의 버튼 바인딩을 검증했다. 먼저 버튼이 없는 상태에서 `Assert.Contains` 실패를 확인한 뒤 연결했고, 관련4건과 ken32 포함 전체18건을 통과했다. 표의 기존 Core396/DX12 Canvas245건은 포팅 당시 실행 결과이며 이번 후속에서 다시 실행한 수치가 아니다.

- 100,020,001개 추가·전체 보기. 실제 셀 하나를 더블클릭해 선택한 뒤 포인터로 이동·크기 변경·20° 회전. 이웃 셀 불변, 삭제/Undo,3단계 Undo/Redo의 기하와 캡처 복원.
- 50,005,000개 부분 선택(10,001개 구간)·선택한 셀만 이동·복붙 후 총150,025,001개 표시. 새 배치 ID·개수·선택과 두 단계 Undo/Redo의 원래 배치/캡처 복원.
- 기존 일반 ROI 보존·추가 취소/복원.555px 폭 이미지의 추가 거부 시 문서·논리 이력 바이트·Redo 분기 보존. 이미지가 없으면 명령 비활성.

RTX3060, Release,8K 예제/GPU 준비 후 두 테스트 실행에서 관찰한 값이다. 반복 성능 벤치마크나 백분위가 아니다.

| 항목 | 집중 실행 | 전체 회귀 실행 |
|---|---:|---:|
| 배치 추가 CPU 명령 | 0.224ms | 0.400ms |
| 추가 시작→캡처 저장 확인 | 36.998ms | 44.251ms |
| 50,005,000개 부분 선택 준비/확정 | 37.344ms | 60.585ms |
| 부분 복사+붙여넣기 명령 완료 | 8.524ms | 10.791ms |

캡처에는 타일 준비·GPU 대기·readback·PNG 저장·20ms polling이 포함된다. 다른 행은 CPU 명령/준비 완료 시간이다. 배치의 `EstimatedBytes`1,416바이트와 편집 후 `HistoryBytes`44,720/568,144바이트는 **공통 자료구조의 논리 추정량**이며 프로세스/GPU 전체 메모리 실측이 아니다.

일반 객체1억 개의 개별 할당, 전체 세부 윤곽의 동시 표시, 지속60FPS·물리 표시 지연·장시간 안정성으로 해석하지 않는다. 실제 화면은 전체 보기의 요약 격자·확대 편집 셀·부분 복붙 장면으로 나누어 캡처하고 확인했다.

```powershell
dotnet test tests/VisionCanvas.Wgpu.Tests -c Release --filter FullyQualifiedName~HundredMillionTests
```

증거는 `artifacts/wgpu-port-validation`의 `results/wgpu-hundred-million.trx`, `results/wgpu-hundred-million-all.trx`, `hundred-million-*.json/png`, `hundred-million-red.log`와 [후속 압축본](data/wgpu-hundred-million-validation.zip)에 있다. 원본 image/cache는 압축하지 않는다.

후속 샘플/테스트 프로젝트의 공백·명명·스타일 검사 PASS. 전체 형식 검사는 기존 위반으로 FAIL이며 wgpu 진단은0건이다. `artifacts/wgpu-publish`를 갱신하고 배포 EXE의 native DLL 로드·정상 종료 코드0을 확인했다. 관련 로그와 `hundred-million-app-launch.json`도 압축본에 포함한다. 로컬 수정·검증만 수행했으며 커밋·푸시는 하지 않았다.

## 장치·의존성·시간

- `Alimer.Bindings.WebGPU 1.6.0`, `Alimer.WebGPU.Native 1.0.4`, 실제 `wgpuGetVersion()` = **27.0.4.0**.
- 실제 adapter: **NVIDIA GeForce RTX 3060 Laptop GPU**, DX12 backend, R16Unorm 지원, maxTextureDimension2D = 16,384.
- native adapter description은 빈 문자열을 반환했다. 별도 `nvidia-smi` 조회는 드라이버 **581.83**, 총 VRAM **6144MiB**다. 현재 사용 가능한 GPU 메모리 예산으로 해석하지 않는다.
- 타일256MiB는 논리 픽셀 바이트 퇴출 목표다. 활성/지연 해제 자원, 라이브·라벨·필터·surface·wgpu staging은 별도이며 전체 메모리 상한이 아니다.

새 wgpu 테스트 전용 애플리케이션 캐시 준비를 포함한 최초 ken32 로딩은 **7,357.963ms**, fit 캡처 완료까지 **7,438.686ms**였다. 같은 캐시를 재사용한 회귀의 한 번은 **74.677ms / 464.960ms**였다. 첫 값에는 PNG 디코딩·피라미드 작성이, capture 값에는 GPU readback·PNG 저장·20ms polling이 포함된다. OS 캐시는 초기화하지 않았다. 캐시 조건이 다른 두 수치를 renderer 속도 개선이나 다른 backend 대비 우위로 비교하지 않는다.

`FrameMetrics`는 CPU 측 처리·대기와 논리 texture 바이트다. GPU timestamp 측정은 구현하지 않았다. Present/PNG 완료는 물리 모니터 scan-out과 다르다.

## 미검증과 제약

- **NOT_RUN:** 지속60FPS·입력→물리 표시 지연·장시간 메모리 안정성·혼합 DPI 이동·실제 카메라·다른 GPU·wgpu의 Vulkan/Metal/browser backend.
- wgpu validation을 켠 실행은 통과했지만 D3D12 debug layer/GPU validation을 별도 설치하고 검사한 결과는 아니다.
- 고정 native 구현의 일부 queue submit 실패는 fatal 처리되어 프로세스가 종료될 수 있다. 강제 장치 손실·복구 시험은 하지 않았으며 RestartAsync 성공을 이 경우의 복구 보장으로 표현하지 않는다.
- 기능 포팅이며 지속 성능을 보장하는 최종 생산 검증은 아니다. ROI cache 생성의 기존 DX12 시간 분할 준비와 완전히 동일한 최적화까지 보장하지 않는다.
- WPF/Win32 호스트와 공통 Canvas의 DX12 전이 의존성을 유지한다. 현재 native backend도 DX12다. 비 Windows UI나 DX12 의존성이 제거된 배포가 아니다.

## 재현과 증거

```powershell
dotnet build VisionCanvas.Wgpu.slnx -c Release
$env:VISIONCANVAS_WGPU_KEN32 = (Resolve-Path 'ken32.png').Path
dotnet test tests/VisionCanvas.Wgpu.Tests -c Release
dotnet test tests/TeachingToolWpf.Core.Tests -c Release
dotnet test tests/VisionCanvas.Tests -c Release
dotnet run --project samples/VisionCanvas.WgpuSample -c Release
```

ken32 환경 변수가 없으면 해당1건은 명시적으로 skip한다. 원본과 대용량 raw cache는 Git에 넣지 않는다. `artifacts/wgpu-port-validation`에 다음 증거가 있다.

- `results/wgpu-final.trx`, `core-regression.trx`, `canvas-regression.trx`.
- `device.json`, `app-launch.json`, `published-app-launch.json`, `ken32/wgpu-result-*.json`, `sample-*.png`, `ken32/original-tile-*.png`.
- `mutation.log`, `build-wgpu.log`, `build-existing-app.log`, `publish.log`, 각 신규 프로젝트 이름의 `-whitespace.log`/`-style.log`, `repository-format.log`.

원본 TRX/JSON·빌드/형식 로그는 [증거 압축본](data/wgpu-validation.zip)에도 보관한다. 대용량 이미지와 cache는 압축본에 포함하지 않는다.

## 코드 규칙·리뷰

App/VM/View와 Core를 분리하고 native 자원은 명시적인 소유자에서 해제한다. 별도 리뷰로 두 제출 슬롯, 지연 해제, 종료 순서, 콜백 수명, WGSL 상수 배치를 확인했다. 업로드·sampler 생성 실패 시 핸들 누수 두 곳을 고쳤다. Gray16/다중 프레임 시험 보완과 메모리 회계 설명도 반영했다.

신규3개 프로젝트를 대상으로 Allman/4칸·명명·괄호·using·공백 검사 PASS. 최초 솔루션의 include 경로 필터가 파일을 누락하여 프로젝트를 각각 직접 지정하는 검사로 변경했다. 최종 저장소 전체 `pwsh -File scripts/Format-Code.ps1 -Verify`는 기존 파일의 줄바꿈/공백 위반으로 FAIL이며 wgpu 파일 진단은0건이다. 예: `tests/VisionInspection.Tests/DiskTileRegionTests.cs`. 무관한 파일은 일괄 수정하지 않았다. 로컬 수정·검증만 수행하며 커밋·푸시는 하지 않았다.
