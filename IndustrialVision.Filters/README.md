# IndustrialVision.Filters

.NET 10 / Windows x64 독립 산업용 전처리 엔진의 **첫 수직 구현**이다. 엔진은 TeachingToolWpf·VisionCanvas·패턴매칭을 참조하지 않는다. 후속 사용자 요청으로 VisionCanvas ROI 미리보기에서 이 엔진을 참조하도록 연결했다. [앱 사용법과 실기 검증](../docs/industrial-roi-filters.md). 단계 A 전체, 단계 B/C 및 논문 최적화는 아직 완료하지 않았다. [상태표](docs/filter-status.csv)에서 구현과 검증 범위를 구별한다.

## 실행

이 폴더에서 실행한다. .NET SDK 10이 필요하며 CPU 예제의 기본 빌드에는 NuGet 런타임 패키지가 없다.

```powershell
dotnet build IndustrialVision.Filters.slnx -c Release
dotnet test tests/IndustrialVision.Filters.Tests -c Release

# ILGPU/CUDA 참조 없는 실행 파일
dotnet run --project samples/IndustrialVision.Filters.Console -c Release

# NVIDIA 실제 장치에서 Gray16/Float32 업로드 → 커널 → 다운로드 검증
dotnet run --project samples/IndustrialVision.Filters.Console -c Release -p:EnableGpu=true -- --gpu-smoke

# 같은 합성 영상·recipe를 CPU/CUDA로 실행하고 최종 마스크 비교
dotnet run --project samples/IndustrialVision.Filters.Console -c Release -p:EnableGpu=true

# 디코더는 예제에만 있다. 파일 없이도 위 예제는 실행된다.
dotnet run --project samples/IndustrialVision.Filters.Console -c Release -- --pgm input.pgm
dotnet run --project samples/IndustrialVision.Filters.Console -c Release -- --raw input.raw 1024 1536 14 be left

dotnet run --project benchmarks/IndustrialVision.Filters.Benchmarks -c Release -- Smoke results/local
dotnet run --project benchmarks/IndustrialVision.Filters.Benchmarks -c Release -- Standard results/local
dotnet run --project benchmarks/IndustrialVision.Filters.Benchmarks -c Release -- Large results/local
```

Raw 인자는 파일·너비·높이·유효 비트·`le/be`·`right/left` 순서다. PGM은 P5, Gray16은 big endian이다. Raw Float32와 padded stride는 `FileTileSource` 생성자로 명시한다. 음수 stride는 거부한다. 원본 값의 자동 8비트 축소는 하지 않는다. 예제의 파일 전체 로드는 int 크기 버퍼 범위이며, 그보다 큰 파일은 `FileTileSource`와 `TileExecutor`로 읽는다.

## 구현된 필터

Gray8/Gray16/Float32 입력 모두에서 다음 CPU·실제 CUDA 커널이 동작한다.

- Raw flat reference 보정, reciprocal gain 보정, 각각 선택적 dark map 결합
- 분리 Gaussian
- 사각 회색조 Erosion/Dilation/Opening/Closing/White Top-hat/Black-hat
- 고정 Threshold (`>`), Gray8 전경 255 / 배경 0

보정·Gaussian·형태학 출력은 Float32다. 정수 입력의 0..65535는 Float32에 정확히 표현되며 보정 후 음수·소수는 보존한다. 형태학은 입력 중 extrema를 고르고, hat 연산은 Float32 차분을 수행한다. 모든 경계는 replicate이다. 다른 경계 모드나 원형 구조 요소를 지원한다고 표시하지 않는다.

CPU는 분리 Gaussian에 Vector256과 scalar 경로를 제공한다. 형태학은 행/열별 deque min/max로 커널 반경과 무관한 선형 순회를 사용한다. 작업 분할은 행/열 수준이며 픽셀별 delegate/interface 호출은 없다. CUDA는 분리 convolution/min-max 커널이며 CPU 함수 호출 래퍼가 아니다. 큰 GPU morphology 반경의 vHGW/shared-memory 최적화는 남아 있다.

## API와 수명

```csharp
using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Pipeline;

// data는 호출자 소유. 실행 반환까지 변경/반환하지 않는다.
var descriptor = new ImageDescriptor(width, height, PixelFormat.Gray16,
    strideBytes: stride, validBits: 14, originX: 100, originY: 200,
    exposureKey: "camera-1:10ms");
var input = new ImageView(descriptor, data);
var recipe = new Recipe(1,
    new Gaussian(2, 1.1),
    new Morphology(MorphologyKind.BlackHat, 3),
    new Threshold(500));
var engine = new FilterEngine();
using var plan = engine.Compile(recipe, descriptor, new ExecutionOptions());
using var output = new HostImage(plan.Output);
var report = plan.Execute(input, output); // plan/output 반복 재사용
```

CUDA 호스트만 `IndustrialVision.Filters.Gpu.ILGPU`를 참조하고 `new CudaBackend()`를 `FilterEngine`에 주입한다. backend는 모든 plan보다 오래 살아야 한다. 활성 plan이 있으면 backend Dispose는 거부한다. CPU만 필요한 배포에서는 Core/Cpu/Pipeline만 참조한다. VisionCanvas도 이 세 프로젝트를 참조하고 원본 reader/표시 버퍼 경계에서 `ImageView`를 구성할 수 있다. 이번 변경에는 Canvas 연결이나 8비트 표시 변환을 넣지 않았다.

`GetCapabilities()`와 `plan.Stages`에 장치·이유가 있다. 기본 CpuGpu 배치는 단일 host 단계는 CPU, 지원되는 연속 다단계는 CUDA다. 이는 보수적인 구조 규칙이며 측정으로 학습한 최적 선택기는 아니다. 벤치마크는 단독 CUDA도 비교하기 위해 `GpuPlacement.PreferGpu`를 명시한다. 메모리 부족/장치 부재는 AllowAndReport 또는 Throw를 따른다. 잘못된 입력과 커널 오류는 숨기지 않는다. 런타임 CUDA 오류 후에는 해당 plan/backend를 재생성한다.

GPU 상주 실행은 `UploadHostInput` → `ExecuteDevice` → 필요할 때 `CopyOutputToHost`다. 반복 `ExecuteDevice`는 같은 원본 device 입력을 보존한다. 현재 전송 형식은 Float32여서 Gray16 입력도 픽셀당 4바이트를 전송하며 최종 마스크도 4바이트/픽셀을 내려받아 host에서 Gray8로 만든다. 중간 필터 사이 전송은 없다. 개선 여지가 있는 비용을 측정에서 제외하지 않는다.

모든 API는 동기 완료이다. 제출한 GPU 작업은 반환/취소/Dispose 전 동기화한다. `CompiledPlan`은 단일 호출자 컨텍스트이며 동시 호출을 거부한다. 여러 device 메서드로 구성한 세션은 호출자가 독점해야 한다. 결과 출력과 입력 저장소 alias/in-place는 거부한다. 취소·실패 시 출력 저장소의 내용은 사용하면 안 되며 device 결과 취득은 차단된다. `HostImage.Dispose()` 이후 이전에 얻은 borrowed view를 사용하지 않는 것은 호출자 책임이다.

## 정확도·유효성·예산

유효 Float32 입력의 NaN/Infinity와 정수 ValidBits 범위 밖 값은 거부한다. 입력 validity=0인 픽셀은 내부 NaN으로 표현하고 모든 이웃 필터에서 보수적으로 전파한다. 이는 결손 픽셀을 제외한 평균/통계가 아니다. 유효성을 잃은 최종 Float32는 NaN+mask0, Gray8은 0+mask0이다. 마스크를 버리면 invalid와 정상 배경 0을 구별할 수 없다.

Raw flat은 `(input-dark)/(flat-dark)*scale`, reciprocal은 `(input-dark)*gain*scale`이다. raw 분모는 **유한하고 minimumDenominator보다 커야** 하며, reciprocal gain은 양수여야 한다. 위반은 invalid이다. dark/flat의 크기·원본 좌표·물리 픽셀 크기·노출 키를 확인한다. 양수 map version을 보관하고 맵 값을 복사하여 불변화한다. 노출 키의 실제 의미와 올바른 calibration 선택은 호스트가 책임진다. recipe/맵을 바꾸면 새 버전으로 재컴파일한다.

Float32 저장 시 IEEE Float32 반올림을 적용한다. 정수 `HostImage.WriteFloat` 변환은 ties-to-even 후 ValidBits 상한에서 saturation한다. 필터 중간에 정수 clamp는 하지 않는다. threshold 비교는 `>`이며 invalid는 전경이 아니다. 검사 원본과 출력은 별도 버퍼다. Window/Level/표시 LUT는 아직 미구현이다.

예산은 plan이 보유하는 실제 pool 배열 payload, calibration, 출력 1장과 GPU device 배열을 계상한다. 빌려온 원본·CLR/JIT·드라이버·ArrayPool에 반환된 잔류 메모리·객체 헤더는 프로세스 메모리 상한에 포함하지 않는다. 여러 plan의 예산은 각자이며 앱 전체 예산은 호스트가 합산해야 한다. 초기 calibration 생성과 출력 생성은 호출자가 소유한다. 반복 resident plan 실행은 이미지 크기에 비례하는 새 managed 배열을 만들지 않는다.

`TileExecutor`는 `IImageTileSource`/`IImageTileSink`로 전체 입력을 상주시키지 않고 처리한다. Gaussian radius와 두 번 형태학 radius를 역방향 누적한다. ROI SourceContext/Isolated를 구별하고 중앙 출력만 게시한다. sink는 Complete 전에 부분 출력을 완료본으로 게시하면 안 된다. 예산에 맞춰 타일 크기를 줄이며 최소 halo도 못 넣으면 실패한다. 현재 타일마다 작은 plan/출력/보정 crop을 만들고, 원본 calibration map은 전체 상주한다. 따라서 tiled 경로의 할당/준비 비용 최적화와 calibration의 스트리밍 소스는 남은 작업이다. 전역 CLAHE/Otsu/FFT/Canny를 독립 타일로 가장하는 API는 없다.

## 검증과 다음 단계

[검증 기록](docs/validation.md), [알고리즘·라이선스](docs/sources.md), [설계 검토](docs/design-review.md), [필터별 상태 CSV](docs/filter-status.csv)를 따른다. `results/final`이 최종 측정이고 `results` 바로 아래 파일은 개발 중 예비 측정이다.

독립 테스트 참조는 이중정밀도 직접 2D convolution/전수 extrema이며 제품의 분리 convolution/deque/GPU 코드를 재사용하지 않는다. 정수 extrema/hat/threshold는 exact, Float32 복합 경로는 실행 전에 정한 `abs <= 0.06 + 1e-6*abs(reference)`를 사용한다. 이는 Gaussian/보정의 수치 오차 허용이며 근사 알고리즘 허용이 아니다. 실제 결함 인증이나 현장 검출률은 측정하지 않았다.

다음 단계는 A의 픽셀 연산·독립 dark·불량 픽셀·Box·정확한 Median·배경/CLAHE·나머지 threshold다. 이후 B, C 순서로 진행한다. R1/R2/R3 최적화는 독립 후보로 비교 후 채택하며 아직 구현/재현 완료가 아니다. 외부 OpenCV/HALCON 비교와 비CUDA GPU/Linux 검증도 미실행이다.
