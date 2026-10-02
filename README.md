# WPF · DX12 Vision Teaching Sample

Windows x64용 비전 티칭 샘플입니다. WPF는 조작 화면을, Direct3D 12는 이미지·ROI·선택 핸들·한글 태그를 그립니다. 8K 합성 영상과 24K급 완성 이미지 파일을 다룹니다.

## 실행

배포 스크립트로 생성하는 실행 파일은 `artifacts/publish/TeachingToolWpf.exe`입니다. 빌드 결과물·복원 패키지·대용량 테스트 이미지는 Git에 포함하지 않으므로 처음 복제한 뒤에는 아래 명령으로 빌드하세요. 실행에는 **.NET 10 Desktop Runtime x64**와 DX12 지원 GPU가 필요합니다. 현재 장비에서는 RTX 3060 Laptop을 선택합니다. 배포 폴더 전체를 함께 사용하세요.

소스에서 실행하려면 .NET 10 SDK를 설치한 뒤:

```powershell
dotnet restore .\TeachingToolWpf.slnx
dotnet build .\TeachingToolWpf.slnx -c Release --no-restore
dotnet run --project .\TeachingToolWpf.csproj -c Release --no-build
```

실행 폴더를 다시 만들 때는 restore 후 `scripts/Publish-Sample.ps1`을 실행합니다. 기존 실측 자료가 있으면 설명서와 함께 복사합니다.

처음에는 8192×8192 검사 패턴이 열립니다. 상단의 **이미지 열기 / 교체**로 PNG/JPEG/WebP/TIFF/BigTIFF를 불러오거나 **24K 샘플**을 눌러 바로 시험할 수 있습니다. 최초 파일 열기는 확대/축소용 디스크 캐시를 생성하므로 시간이 필요합니다. 진행 상태와 읽기 취소 버튼을 제공합니다.

## 조작

| 작업 | 버튼 / 입력 |
|---|---|
| 확대·축소 | 휠(커서 중심), −/+, 배율 입력 후 적용 |
| 화면 맞춤 / 원본 배율 | 화면 맞춤(F), 1:1. 1:1은 원본 1px = 물리 화면 1px |
| 화면 이동 | 이동 도구(H)에서 드래그, 중간 버튼, Space+드래그, 방향 이동 버튼 |
| 미니맵 | 우하단 전체 이미지에서 클릭/드래그로 이동. 파란 사각형은 현재 화면 위치. 배율은 유지하며 휠로 확대·축소 가능 |
| ROI 생성 | ROI 추가 버튼은 즉시 생성. ROI 그리기(R)는 드래그로 한 개를 만든 뒤 자동으로 선택 도구로 복귀. 다시 그리려면 R 또는 그리기 버튼 |
| 선택 | 선택(V), Ctrl+클릭 토글, Shift+클릭 추가, 빈 곳 드래그 범위 선택 |
| 겹친 ROI 선택 | 같은 위치에서 Alt+클릭 반복 |
| 크기 조절 | 단일 선택 ROI의 8개 핸들, 또는 속성 패널의 너비/높이 |
| 이동 / 회전 | ROI 내부 드래그 / 상단 회전 핸들. Shift로 이동 축 또는 회전 15° 고정 |
| 다중 편집 | 여러 ROI 이동·회전·삭제. 부모와 자식을 함께 선택해도 자손 변환은 한 번 |
| 수치 편집 | 오른쪽 이름·중심·크기·절대 각도 입력 후 속성 적용 |
| 각도 초기화 | 0° 초기화(0). 이미지 기준 절대 각도 |
| 그룹화 | 2개 이상의 독립 ROI 선택 → 부모로 그룹화(Ctrl+G) |
| 그룹 해제 | 생성된 그룹 부모 선택 → 그룹 해제(Ctrl+Shift+G). 자식은 보존 |
| 복사 / 붙여넣기 | 왼쪽 복사 / 붙여넣기 버튼, Ctrl+C / Ctrl+V. 선택 ROI와 자손·필터 설정을 함께 복제 |
| 삭제 | 삭제(Delete). 부모를 삭제하면 모든 자손도 삭제 |
| Undo / Redo | 상단 버튼, Ctrl+Z / Ctrl+Y 또는 Ctrl+Shift+Z |
| 편집 취소 | Esc로 진행 중 드래그·대량 붙여넣기 취소. 포인터 캡처 상실 시 드래그 취소 |
| 정밀 이동 | 선택 후 방향키 1 이미지 px, Shift+방향키 10px |

선택은 얇은 파란 윤곽·흰색 사각 핸들·원형 회전 핸들·각도 배지로 표시합니다. 확대율과 무관한 DIP 크기를 사용합니다. 왼쪽 목록에서도 다중 선택할 수 있습니다. 완전히 포함된 ROI만 자식이 되고 가장 작은 유효 부모를 찾습니다. 부모 이름표에 직접 자식 개수가 표시됩니다. 겹치는 태그는 일부 생략하지만 전체 이름은 목록에 남습니다.

복사본은 새 ID를 가지며 붙여넣기마다 화면 기준 20 DIP씩 옮깁니다. 이미지 경계에서는 이동을 제한하고, 더 작은 이미지로 붙여넣으면 전체 복사 집합을 균일 축소합니다. 부모 관계는 기존 완전 포함 규칙으로 다시 계산하므로 원본 ROI와 겹치면 기존 ROI도 부모 후보가 됩니다. 앱 내부 클립보드를 사용하며 앱 종료 시 비워집니다. 이름·숫자 입력칸에서는 Ctrl+C/Ctrl+V/Del이 텍스트 편집으로 동작합니다.

대량 붙여넣기는 작업 스레드에서 준비하며 상태 표시줄에 진행 상태를 표시합니다. **Esc로 취소**할 수 있고, 성공한 붙여넣기는 한 번의 Undo로 되돌립니다. 300개부터 전체 선택·복제로 19,200개까지 늘리는 시험과 7,000개 필터 ROI 복제 결과는 [검증 기록](docs/clipboard-minimap-validation.md)을 참고하세요. 전체 선택과 결과 확정에는 짧은 UI 지연이 남아 있습니다.

미니맵은 정적 8K/24K와 라이브 영상에 연동됩니다. 전체 이미지와 현재 가시 영역을 보여주며 이동은 편집 이력을 추가하지 않습니다. 미니맵 드래그 중 Esc를 누르면 이전 위치로 돌아갑니다. 라이브 개요 영상은 최대 초당 2회 갱신합니다. 아주 작은 뷰포트에서는 미니맵을 숨깁니다.

**이미지 교체에 성공하면 ROI와 선택이 초기화됩니다.** Undo로 이전 이미지·ROI·선택·뷰를 함께 복원할 수 있습니다. ROI 그리기가 이미지 밖으로 나가면 이미지와 겹치는 크기만 생성합니다. 이동·크기·회전·수치 편집도 이미지 경계에서 제한하며, 여러 ROI는 함께 가능한 범위까지만 움직입니다.

0도 초기화 시 경계를 넘게 된다면 선택/자손을 함께 이동하고 필요하면 균일 축소해 정확한 0도와 이미지 포함을 유지합니다. Undo로 원래 형상을 복원할 수 있습니다.

## ROI 필터 미리보기

1. ROI를 하나 이상 선택합니다.
2. 오른쪽 **ROI 필터**에서 **Binarize · 이진화** 또는 **Edge · Canny**를 선택하고 임계값을 입력합니다.
3. **미리보기**를 누르면 선택 ROI에 설정을 등록하고 해당 영역에만 결과를 표시합니다. 회전한 ROI도 해당 영역에 맞춰 표시합니다.
4. **등록된 ROI는 다시 선택하면 자동으로 필터를 표시합니다.** 미등록 ROI는 원본을 표시합니다. 다중 선택은 ROI마다 저장된 설정을 사용하며, 서로 다른 설정이면 안내 문구를 표시합니다.
5. 미리보기 중 이동/회전/크기·필터 변경을 자동 반영합니다. 임계값은 입력칸을 벗어나면 확정하며 한 번의 Undo로 되돌립니다. 다중 선택에서 설정을 수정하면 선택 전체에 같은 설정을 등록합니다.
6. **원본 보기**는 설정을 유지한 채 현재 표시만 끕니다. 다른 ROI를 선택했다가 돌아오면 다시 표시합니다. **등록 해제**는 선택 전체의 설정을 지웁니다. **없음 · 원본**도 설정을 해제합니다.

필터 등록·변경·해제는 Undo/Redo 대상이며 복사·붙여넣기와 삭제/이미지 교체 Undo에서도 복원됩니다. 계산된 미리보기 픽셀은 이력에 저장하지 않으며 원본 파일을 변경하지 않습니다. 설정은 현재 문서 안에서 유지되며 파일로 레시피를 저장하는 기능은 아직 없습니다.

OpenCV 4.13 / OpenCvSharp로 처리합니다. 임계값은 Gray8 기준 0–255이며 Gray16은 0–65535를 0–255로 매핑합니다. 컬러는 회색조 변환 후 처리합니다. 대용량에서는 필요한 타일만 읽고, ROI당 긴 축 최대 **1,024px**, 선택 전체 약 **4Mpx** 범위의 미리보기로 계산합니다. 축소된 Edge 결과는 원본 해상도 검사 결과와 다를 수 있습니다. 라이브 필터는 최신 프레임으로 최대 **초당 5회** 갱신하며 처리 중 요청을 쌓지 않습니다. 라이브 영상 자체의 표시 주기와는 별개입니다.

부모 이동·회전은 자손에게 적용합니다. 부모 resize는 자식의 크기와 상대 위치를 유지하고, 부모 중심이 이동한 만큼 자식도 이동합니다. 편집 확정 후 포함에서 벗어나면 유효 부모 또는 루트로 재배치합니다. 그룹화는 실제 새 부모 사각형을 만들며, 선택하지 않은 ROI까지 포함되면 기존 포함 규칙이 적용됩니다.

각 편집/드래그는 하나의 이력입니다. 이름, 그룹, 연쇄 삭제, 이미지 교체까지 복원합니다. 이력은 최대 200개·추정 1 GiB이며 ROI 변경 값과 이미지 참조만 보관합니다. 이미지 Undo 시 원본 파일이 삭제되거나 변경되었으면 현재 문서와 이력을 유지하고 오류를 표시합니다. 단순 선택·zoom/pan·라이브 프레임 도착은 이력에 넣지 않습니다.

## 8K 실시간과 대용량 이미지

**8K 실시간**은 7680×4320, 목표 60 FPS의 합성 CPU 프레임입니다. Gray8/Gray16/RGBA32/BGRA32를 고를 수 있습니다. 정지는 마지막 프레임을 유지하므로 확대해서 확인할 수 있습니다. 파일/샘플을 열면 라이브 미리보기를 닫습니다. 실제 카메라 SDK 연결·검사·녹화는 포함하지 않았습니다.

라이브 시작도 소스 교체이므로 ROI를 초기화하고 7680×4320 경계를 적용합니다. 라이브 소스로 Undo/Redo하면 해당 소스를 다시 시작하며 과거 시점의 프레임을 재생하지 않습니다.

전체 원본 프레임은 유지하고, 화면에 필요한 영역과 해상도만 area average로 준비해 DX12로 업로드합니다. 작은 화면에서 전체 8K RGBA를 매번 복사하는 비용을 줄였으며, 1:1에서는 해당 영역의 원본 픽셀을 사용합니다. 표시가 늦으면 대기 중인 예전 프레임을 버리고 드롭 수를 표시합니다. 이 정책은 카메라 수집/검사 데이터의 무손실 보장을 뜻하지 않습니다.

정적 대용량 파일은 libvips로 디스크 피라미드를 만든 후 512px 타일로 읽습니다. Gray8/Gray16은 단일 채널을 유지합니다. Gray16 표시 범위는 0–65535이며 별도 window/level 조절은 아직 없습니다. 컬러는 RGBA8로 정규화합니다. **24576×131072 Gray8/Gray16 BigTIFF(디코드 후 3/6 GiB)**를 실제 생성·열기·마지막 타일 검증했습니다. 초기 로드는 파일 압축과 저장장치에 따라 달라집니다.

캐시 위치는 `%LOCALAPPDATA%\TeachingToolWpf\ImageCache`입니다. 디코드 크기의 약 1.5배 이상 여유를 확인합니다. 캐시의 자동 디스크 용량 정리는 아직 없으므로 앱을 닫은 뒤 이 전용 캐시 폴더를 비워 재생성할 수 있습니다. OS 파일 매핑 때문에 프로세스 working set은 관리 버퍼 크기보다 커질 수 있습니다.

형식별 크기 제한도 적용됩니다. 24K 폭은 PNG/JPEG/TIFF로 검증했고 WebP는 8K로 검증했습니다. WebP의 최대 축은 16,383px이므로 24K 파일 용도로 사용할 수 없습니다. [WebP 공식 FAQ](https://developers.google.com/speed/webp/faq)

## 구조와 확장 지점

| 위치 | 책임 |
|---|---|
| `src/TeachingToolWpf.Core` | double 기하, 포함/BVH, 불변 문서, 편집/이력, 제스처, 프레임/타일 계약 |
| `Presentation` | MVVM 명령·상태, 이미지 교체 트랜잭션, 라이브 제어 |
| `Controls` | HwndHost 입력/DPI, 목록 선택, 텍스트 래스터 어댑터 |
| `Imaging` | libvips 디스크 피라미드, 타일 작업자, 합성 소스, 가시 프레임/AVX2 축소 |
| `Rendering` | DX12 장치/표시/fence, 업로드, 이미지 캐시, 배치 도형/라벨 |
| `Diagnostics`, `benchmarks` | 실제 앱 smoke, GPU/CPU CSV, 대용량 파일 시험, Core 측정 |

카메라 연결은 `IFrameSource.Frames(CancellationToken)`을 구현하고 `MainWindow` 조립 지점의 팩터리를 교체합니다. `VideoFrame` 게시 시 소유권을 넘기고 소비자가 해제하기 전에 버퍼를 재사용하지 마세요. 현재 `PixelBuffer`는 tightly packed CPU 버퍼이며 카메라 stride padding은 어댑터에서 행 복사로 정규화합니다. GPU 공유 메모리/zero-copy는 구현하지 않았습니다. 파일 소스는 `ITileSource`, 표시 준비는 `IFramePreparer`로 확장합니다.

## 검증과 재현

대량 ROI에서는 왼쪽 목록은 4,096개까지 한 가상화 목록으로, 그 이상은 512개씩 표시합니다. 캔버스에서 하나를 새로 선택하면 해당 페이지로 이동합니다. 목록 페이지 이동은 전체 선택을 보존하며, 일반 클릭으로 선택을 교체하고 Ctrl로 추가/해제합니다. 축소 화면의 매우 작은 ROI 집합은 요약 표시되고 확대하면 개별 윤곽이 나타납니다. 이름·숫자 필드는 클릭/Tab으로 그대로 편집할 수 있습니다. 실제 100만·200만 ROI 조건과 초기 구축 비용은 [대량 ROI 성능](docs/roi-scalability.md)을 참고하세요.

```powershell
dotnet test .\tests\TeachingToolWpf.Core.Tests -c Release --no-restore
dotnet test .\tests\TeachingToolWpf.Imaging.Tests -c Release --no-restore
.\artifacts\publish\TeachingToolWpf.exe --smoke --gpu-debug
.\artifacts\publish\TeachingToolWpf.exe --benchmark --format Bgra32 --rois 500 --seconds 70 --metrics artifacts\benchmark.csv
.\artifacts\publish\TeachingToolWpf.exe --large-image --gpu-debug
.\artifacts\publish\TeachingToolWpf.exe --roi-benchmark --roi-counts 3550 --roi-samples 50 --roi-distributed --roi-report artifacts\roi.json --metrics artifacts\roi-frames.csv
.\artifacts\publish\TeachingToolWpf.exe --roi-benchmark --roi-counts 2000000 --roi-samples 30 --roi-distributed --roi-single-batch --roi-detail --roi-report artifacts\roi-detail.json
.\artifacts\publish\TeachingToolWpf.exe --memory-check --gpu-debug --memory-cycles 300 --memory-report artifacts\memory-replacement.json --metrics artifacts\memory-replacement-frames.csv
dotnet run --project .\benchmarks\TeachingToolWpf.Benchmarks -c Release -- artifacts\core.json
```

진단 실행은 화면 밖 테스트 창을 사용하고 종료 후 `artifacts`에 결과를 남깁니다. `--gpu-debug`는 Windows Graphics Tools가 설치되어 있으면 DX12 debug layer를 켭니다. 벤치마크는 10초 워밍업 후 60초 구간을 비교합니다. `--large-image`는 디스크 캐시를 약 12 GiB 생성하므로 여유 공간을 확보하세요. 성능 목표를 측정 결과와 혼동하지 않도록 [실측 보고서](docs/performance-baseline.md)와 [검증 범위](docs/runtime-validation.md)를 함께 보세요.

`--memory-check`는 8K RGBA PNG와 24K Gray16 TIFF를 번갈아 열고 매번 ROI 필터를 처리합니다. 300주기는 이미지 600회 교체이며, 이번 측정 약 219초 동안 지속 증가하는 누수 징후를 발견하지 못했습니다. 조건·수치·미검증 범위는 [이미지 교체 메모리 검증](docs/memory-replacement-validation.md)을 참고하세요.

미검증/후속 항목은 혼합 DPI 모니터 이동, 실제 장치 제거, 실제 카메라·촬영 파일, 8192² 실시간, 장시간 부하, 분수 배율 타일 gutter입니다. ROI 저장/불러오기, 타원/다각형, 다중 ROI 일괄 resize는 이번 샘플에 포함하지 않았습니다. [품질 검토](docs/quality-review.md), [설계 결정](docs/decisions/0003-implementation-and-measured-optimization.md), [의존성 고지](THIRD-PARTY-NOTICES.md)를 참고하세요.
