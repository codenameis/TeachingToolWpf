# CPU 템플릿 매칭 3종 — 독립 구현과 공통 시험

2026-10-16 정확도 우선 후속: Chamfer의 원본 양방향 검증·거리 단위를 보정하고 결과를 보존하는 계산 재사용을 적용했다. [최신 정확도·추가 데이터·할당 실측](cpu-matching-accuracy.md)을 따른다. 아래 성능표는 최초 구현의 이력이다.

2026-10-16 후속: 세 방식을 기존 WPF의 선택·학습·검사에 연결했다. [사용법·Kensa 직접 비교](cpu-matching-wpf.md)를 따른다. 아래 독립 라이브러리 측정은 UI 연결 전의 기록이며 원본 Kensa 부품에서 발견된 Chamfer 추가 오검출과 표시 시간을 대체하지 않는다.

2026-10-15~16. 사용자가 GPU 없는 실용적인 매칭을 목표로 LINE-2D, Sub-NCC, Directional Chamfer를 각각 별도 프로젝트로 구현하고 테스트 데이터를 제공하도록 요청했다. 일반 검사에서는 제한 영역, 보정에서는 넓은 영역을 사용한다. 이번 범위는 독립 C# 라이브러리·실행 도구이며 기존 WPF 검사 모드를 교체하지 않는다. [ADR-054](decisions/0054-independent-cpu-template-matchers.md).

## 시작하기

`CpuMatching.slnx`는 기존 WPF/PoseMatch/ONNX/TorchSharp/CUDA 없이 빌드할 수 있다. 세 알고리즘과 Core에는 외부 NuGet 의존성이 없다. 평가 도구만 기존 OpenCvSharp4/runtime.win.slim 4.13.0.20260627을 PNG I/O·독립 데이터 생성·OpenCV 비교에 사용한다. Windows x64 / .NET10에서 실행했다. Linux 실행은 미검증이다.

```powershell
dotnet build CpuMatching.slnx -c Release
dotnet test tests/CpuMatching.Tests/CpuMatching.Tests.csproj -c Release
dotnet run --project tools/CpuMatching.Evaluation -c Release -- run samples/CpuMatching.Data artifacts/cpu-results.json all 3
# 새 폴더로 같은 입력을 재생성한다. 기존 폴더는 덮어쓰지 않는다.
dotnet run --project tools/CpuMatching.Evaluation -c Release -- generate artifacts/my-cpu-data kensa.png
```

`run`의 `all` 자리에 `local-rotation`, `kensa-18v-original`, `validation-oblique` 등의 case 이름을 넣으면 하나만 실행한다. 마지막 숫자는 첫 실행 이후 반복 수(1~10)다. Ctrl+C는 취소이며 오류로 종료한다. 보고서의 `Passed`와 실행 종료 코드를 구분한다. 종료 코드0은 평가 작업 완료이며 모든 알고리즘 합격을 뜻하지 않는다.

현재 빌드한 실행 파일: `artifacts/cpu-matching/bin/CpuMatching.Evaluation/release/CpuMatching.Evaluation.exe`.

## 프로젝트와 논문 대응

|프로젝트|구현|원문과 차이/미구현|
|---|---|---|
|`CpuMatching.Line2D`|희소 Sobel 경계, 방향별 응답과 coarse 확산, 회전/배율 특징, SIMD 행 누적, 피라미드, 최종 point-to-line 보정|16방향 signed float 응답을 사용한다. 원문의 unsigned bitmask/linear memory 전체 배치와 RGB-D는 구현하지 않았다. 원본 LINEMOD 포팅이라고 표현하지 않는다.|
|`CpuMatching.SubNcc`|회전 유효 원판의 선택 픽셀 ZNCC, 경계+균일 표본, 피라미드/국소 각도 검색, 원본 전체 템플릿 명암 검증|논문의 정확한 stable-pixel 선별 절차와 동일하지 않다. 원본 검증은 문자 양끝을 보존하도록 전체 사각 표본을 사용한다. 역방향 표본화이므로 회전 padding은 점수에서 제외된다.|
|`CpuMatching.DirectionalChamfer`|16방향 공간 거리 변환, 순환 방향 거리 비용, 피라미드/자세 검색, 원본 경계 보정|공간 거리는 8이웃 chamfer 근사다. FDCM의 선분 적분 영상·영역 하한 가지치기를 구현하지 않았으므로 완전한 **Fast** Directional Chamfer 재현이나 논문 속도를 주장하지 않는다.|
|`CpuMatching.Core`|Gray8, 검색 범위, 결과·예산, 공통 자세 검색과 연속 경계 보정|세 소비자가 실제 사용하는 공통 책임만 포함한다. GPU/앱/파일 디코더를 참조하지 않는다.|

출처: [Gradient Response Maps / LINE-2D](https://raw.githubusercontent.com/meiqua/shape_based_matching/master/Gradient%20Response%20Maps%20for%20Real-TimeDetection%20of%20Textureless%20Objects.pdf), [Sub-NCC, 2022](https://www.aimspress.com/aimspress-data/mbe/2022/9/PDF/mbe-19-09-442.pdf), [FDCM, CVPR2010 / MERL](https://www.merl.com/publications/docs/TR2010-045.pdf). 논문 개념의 독립 구현이며 외부 matcher 코드를 복사하지 않았다. 세 방법을 별도로 검증하기 위한 초기 구현이지 논문 전체 재현 인증이 아니다.

## C# 호출

소비 프로젝트가 사용할 알고리즘 csproj를 ProjectReference로 연결한다. 예를 들어:

```csharp
using CpuMatching.Core;
using CpuMatching.Line2D;

var template = new GrayImage(templateWidth, templateHeight, templateGray8);
var image = new GrayImage(imageWidth, imageHeight, imageGray8);
ITemplateMatcher matcher = new Line2DMatcher(template);
var result = matcher.Find(image, new SearchOptions
{
    Region = new SearchRegion(100, 200, 256, 256),
    MinimumAngle = -30,
    MaximumAngle = 30,
    MinimumScore = 0.75,
    MaximumMatches = 16
}, cancellationToken);
```

다른 두 방법은 `new SubNccMatcher(template)` / `new DirectionalChamferMatcher(template)`로 바꾼다. 생성자는 템플릿 등록이며 신경망 학습이 아니다. 입력 GrayImage는 생성 시 복사한다. 좌표는 픽셀 인덱스의 템플릿 중심, 각도는 영상 시계 방향 도, scale은 템플릿에 대한 균일 배율이다. 결과는 검색 crop 내부가 아닌 입력 영상 원본 좌표다. 검색 Region 안에서 필요한 특징/명암 표본이 들어오는 위치만 평가하며 마스크/가림 전용 API는 없다.

|파라미터|기본값/제한|
|---|---|
|회전|기본 -180~180°, coarse 10°. 검사에서는 실제 허용 범위를 지정|
|배율|기본1, 선택 범위0.5~2, coarse 간격0.1. 가로/세로 독립 배율 없음|
|피라미드|기본 최대3단계, 작은 템플릿은 자동으로 적은 단계 사용|
|점수|0.75. 방법 간 수치 의미가 달라 동일 품질 확률로 해석하지 않음|
|후보/출력|후보2048, 출력64. 초과는 별도 flag이며 완전 검색으로 주장하지 않음|
|중복 억제|중심 거리 기본8px. 붙어 있는 여러 대상은 실제 간격에 맞게 설정|
|메모리 보호|입력80Mpx 이하, 검색 사각 영역2,097,152px 이하, 템플릿 각 변512px 이하|

이 문서의 초기 구현은8K 원본 안의 작은 Region만 지원하고 전체 검색을 거부했다. **현재8K 전체 검색/방식별 옵션/병렬 실행은 [후속 구현과 실측](cpu-matching-performance.md)을 따른다.** 아래 초기 수치는 당시1024² 이하 조건이다. 경계·원판 특징 선택, coarse 점수 및 후보 한도는 완전성의 수학적 보장이 아니며 실데이터 검증이 필요하다. 매 호출의 crop·피라미드·특징/응답 준비를 모두 측정에 포함한다. 원본을 미리 검색하거나 정답으로 탐색 위치를 제한하지 않는다.

## 테스트 데이터

[`samples/CpuMatching.Data`](../samples/CpuMatching.Data/README.md)에 템플릿 PNG, 검사 PNG, `*-truth.png`, `cases.json`, `results.json`이 있다. [ZIP](data/cpu-matching-test-data.zip)으로도 제공한다.

- 개발 합성12장: 256² 이동/17°/179°/잡음/가림/배율1.1, 음성,512² 다중 대상,1024² 보정 영역, 문자, Kensa 부품/19×12 문자 crop의 알려진 합성 회전.
- 별도 검증3장: 개발 후 추가한 다른73×61 모양과 다른 난수 seed의 -24.4°/다중6.8°·-21.6°/음성. 이 세 장을 보고 알고리즘을 재조정하지 않았다.
- Kensa 원본120×100 crop1장: 학습한19×12 문자의 원래 위치를 확인한다. 전체 대상 라벨을 검수한 장면이 아니므로 `GroundTruthComplete=false`, 알려진 대상 검증만 기록한다.

작은 합성 데이터로 개발한 결과이므로 현장 검출률을 의미하지 않는다. 합성 렌더러는 OpenCV WarpAffine, matcher는 C# 수식으로 분리했다. 회전19×12 문자는 합성 테스트이고 실제 생산 조건의 전체 Kensa 검출률 시험이 아니다.

## 실측과 판정

Ryzen5 5600H / Windows x64 / .NET10, Release. 세 라이브러리는 단일 작업 스레드(SIMD 자동 사용), OpenCV도1스레드로 고정했다. 첫 회+반복3회, 아래는 반복 중앙값이다. **Find의 영상 준비·검색·보정 포함, PNG 읽기와 UI 표시는 제외**다. 따라서 검사 버튼125ms 달성 증거가 아니다. 다른 사용자 앱은 종료하지 않았다.

|장면|LINE-2D|Sub-NCC|Directional Chamfer|OpenCV 회전 bank|
|---|---:|---:|---:|---:|
|256² 회전17°|15.9ms|21.8ms|71.7ms|18.2ms|
|256² 별도 검증 -24.4°|15.1ms|27.7ms|89.0ms|28.6ms|
|512² 별도 검증2개|54.6ms|138.3ms|346.5ms|92.4ms|
|1024² 보정용1개|141.8ms|239.1ms|1178.8ms|164.7ms|

세 구현 모두 정답이 완전한 합성15장(개발12+별도3)에서 누락/오검출0, 위치1px·각도1°·배율0.03 기준을 통과했다. 같은 장면과 파라미터를 사용했고 최종 점수0.75는 변경하지 않았다. 원본 Kensa는 세 방법 모두 알려진 위치의 한 개를 찾았으나 현장 precision/recall 합격으로 집계하지 않는다.

OpenCV 고정 방향 CCOEFF_NORMED는15장 중3장 합격, 회전 bank+0.5° 국소 보정은11장 합격이다. 전자는 회전 기능이 없어 회전 장면의 실패가 예상되며, 고정 방향의 속도는 대체로 더 빠르다. 후자는 회전한 사각 템플릿의 padding·정수 위치에 영향을 받고 다중 스케일 coarse bank를 사용한다. 최적화된 피라미드 NCC/상용 매칭과 동등한 비교 구현이 아니다. 이 숫자로 일반적인 OpenCV 우위/열위를 선언하지 않는다. **현재 LINE-2D가 후보 중 유리하지만 목표인 OpenCV 대비 압도적 성능은 검증되지 않았다.**

`results.json`에는 첫 회/모든 반복 시간, 입력/템플릿 SHA256, 설정, 정답과 예측 pose, 누락·추가 결과·오차·잘림이 있다. 참값/예측의 대응 반경과 합격 위치 오차1px를 구분한다. 위치/각도는 찾은 대상만 평가하므로 누락과 함께 확인해야 한다. 점수별 PR 곡선은 미측정이다.

원인 수정: 초기 방향/Chamfer 각도2~4° 오차→원본 subpixel 경계 point-to-line 정밀화; 좁은 NCC 원판이 작은 문자의 양끝을 버린 오검출→최종 전체 템플릿 검증; 작은 문자에 과대한 Chamfer 거리 허용→특징 크기에 따른 거리 단위 적용. 방향 거리 맵은 모든 bin 쌍 비교를 같은 비용의 순환 L1 변환으로 줄였다. 원본 초기 실패 보고서는 `artifacts/cpu-matching/initial.json`에 보존했다.

## 검증과 설계 검토

자동 회귀7개: 세 방식별 이동/검색 ROI 원본 좌표/음성/취소, 홀수 크기·90° 다중 검출/출력 초과/잘못된 입력, 픽셀 소유권/상수 템플릿 거부. 별도 솔루션 Release 빌드0경고/0오류. 기존 앱 제품 코드와 패키지 참조는 변경하지 않았다.

SOLID: SRP는 데이터/탐색/점수/경계 보정/평가 도구 분리, OCP는 ITemplateMatcher·IScoreLevel, LSP는 동일 좌표/취소/실패/예산 테스트, ISP는 필수 Find와 선택형 IRowScore 분리, DIP는 공통 탐색이 구체 공급자 대신 점수 계약을 사용한다.

GRASP: Information Expert는 각 점수 모델이 응답/거리/명암 소유; Creator는 matcher가 레벨 점수 생성; Controller는 평가 CLI; Low Coupling은 원본 Gray8/pose 경계; High Cohesion은 세 알고리즘 독립; Polymorphism은 matcher/score 전략; Pure Fabrication은 공통 피라미드 검색; Indirection은 중립 계약; Protected Variations는 도구 내부 OpenCV와 알고리즘 분리다. 원칙 예외 없음.

남은 범위: 실제 생산 이미지·대량 반복 무늬·심한 가림·마스크·비균일 배율·최대 크기 부하·UI 연결·다중 스레드 최적화·현장 장시간 운용. 검사125ms와8K 전체 성능 완료를 주장하지 않는다.

추가 검증: 새 CpuMatching.slnx의 whitespace/명명/중괄호·수식 스타일 검증 PASS. 전체 scripts/Format-Code.ps1 -Verify는 기존/병행 파일(예: tests/VisionInspection.Tests/DiskTileRegionTests.cs)의 줄 끝 규칙으로 FAIL이며 해당 범위를 일괄 수정하지 않았다. 로그: artifacts/cpu-matching/global-format.log. ZIP 41개 파일을 원본 SHA256과 각각 대조했고 모두 일치했다(1,677,830bytes).
