# TorchSharp PoseMatch 사용법과 검증

2026-10-15, Windows x64, Ryzen5 5600H, RTX3060 Laptop6GB. **C# 학습·추론 기능과 ONNX 비교는 구현했지만, Kensa/8K 정밀 정확도와125/80ms는 아직 FAIL이다.** 전체 PoseMatch 검색과 점수0.5를 유지했다. [ADR-053](decisions/0053-torchsharp-training-and-inference.md).

## 앱에서 사용

1. PoseMatch-TDCM을 선택하고 ROI를 학습한다. 이 버튼은 템플릿 등록이며 신경망 가중치를 바꾸지 않는다.
2. 추론 엔진 `TorchSharp`(새 기본값) 또는 `OnnxRuntime`, 장치 `Cpu`/`Cuda`를 선택한다.
3. TorchSharp 가중치 경로가 빈칸이면 공식 모델이다. 아래 C# 학습 도구에서 만든 `.tdcm` 경로를 입력하면 해당 모델로 검사한다. ONNX에서 이 파일을 선택하면 오류로 안내한다.
4. 검사 실행은 같은 원본 reader·면적 축소·겹치는 창 전체 검색·후처리·결과 표시 경로를 사용한다. 다른 전통적 탐색으로 후보를 미리 찾지 않는다.

현재 검증 실행 파일은 `artifacts/tdcm-torch/bin/TeachingToolWpf/release/TeachingToolWpf.exe`다. 실행 중인 기존 사용자 앱은 종료하거나 교체하지 않았다. CPU/CUDA 전환과 사용자 가중치 변경은 명시적인 세션 교체로 처리한다.

ONNX CUDA의 지연 로딩 DLL 탐색을 위해 `scripts/Start-TdcmCuda.ps1 <실행파일>`로 실행한다. 스크립트는 새 배포의 `runtimes/win-x64/native`를 자식 프로세스 PATH에 추가한다. 시스템 PATH/드라이버는 바꾸지 않는다. 두 엔진 비교도 같은 배포 DLL 경로를 사용했다. TorchSharp 학습·추론에는 Python 설치가 필요 없다. CUDA 네이티브 배포가 크며 첫 로드 비용도 측정에 포함한다.

## C# 가중치 학습

프로젝트: `tools/PoseMatch.Tdcm.Training`. 다음은 **동작 확인용 합성 데이터** 예제다. Kensa/8K 정확도를 검증하는 데이터셋이 아니다.

```powershell
dotnet run --project tools/PoseMatch.Tdcm.Training -c Release -- --make-example artifacts/my-tdcm-data
dotnet run --project tools/PoseMatch.Tdcm.Training -c Release -- artifacts/my-tdcm-data/dataset.json artifacts/my-tdcm-data/model.tdcm --cuda --epochs=20 --learning-rate=0.00001
```

CPU는 `--cuda`를 빼면 된다. `--threads=4`, `--positive-weight=1`, `--input=previous.tdcm`도 지원한다. 마지막 옵션은 기존 가중치에서 **새 optimizer로** 미세학습하는 것이며 학습 진행 상태의 완전한 재개가 아니다. 이미 있는 출력 가중치/보고서는 덮어쓰지 않는다. Ctrl+C로 취소하며 취소된 실행을 성공 모델로 자동 저장하지 않는다.

라이브러리에서는 `TdcmTrainer.Train(samples, options, progress, token)`과 `SaveCheckpoint(path)`를 사용한다. `TorchTdcmMatcher(checkpoint: path)`로 불러와 추론한다. 각 인스턴스는 호출자가 생성·해제하며 학습은 단일 실행 소유자를 전제로 한다.

데이터셋 JSON에는 `Description`, `LabelsVerified`, `Training`, `Validation`을 둔다. 각 항목은 `SourceId`, `Image`, `Template`, `Targets`다. 픽셀 항목은 `Path`, `Width`, `Height`, `Stride`, `Channels`(Gray8=1/BGR24=3)이며 헤더 없는 픽셀 파일을 읽는다. 정답은 `X`, `Y`, `Width`, `Height`, `AngleDegrees`(영상 시계 방향), `Score`다. 좌표는 **해당 학습 창의 픽셀 인덱스 좌표**, 폭·높이는 검색 창에서의18~72px다. 큰 원본 ROI 크기를 그대로 기입하지 않는다. 입력 창은36~1024,4의 배수이며 템플릿은36×36으로 정규화한다.

장면의 모든 대상에 라벨을 붙여야 한다. 대상 없는 장면은 `Targets: []`로 추가한다. 같은 원본의 crop/증강은 같은 SourceId로 관리하고 학습/검증에 나눠 넣지 않는다. 도구는 SourceId 중복과 동일 영상 파일 해시를 거부하지만, 다른 파일로 다시 만든 같은 장면까지 자동 판별하지는 못한다. 원본 분리와 라벨 정확성은 데이터 제공자가 확인해야 한다. JSON4MiB/입력 픽셀 합계256MiB 예산을 둔다.

`output.tdcm.json`에는 입력 목록·가중치 SHA256, 학습 설정, step loss, 검증 전후 누락·오검출·중복·위치/각도 오차를 기록한다. 종료 코드0은 작업 완료이며 정확도 PASS와 다르다. 콘솔/보고서의 검증 판정을 확인한다. 정답은 loss/검증에만 사용하며 검색 위치를 제한하지 않는다.

## 학습 동작 검증

공식 가중치에서 CPU/CUDA 각각400회 갱신했다. 학습20장·검증10장, 검증에는 양성8장·음성2장을 사용했다. 위치/장면이 분리되어 있고 검증 회전은 학습의 직각 회전에서15° 벗어난다. 같은 합성 템플릿을 사용하는 검증이며 미지 물체 일반화 시험이 아니다.

초기에는 중심 픽셀이 적어 단순 평균 BCE로 학습하면 검출을 모두 배경으로 만들면서 loss만 낮아졌다. 이를 양성/음성별 정규화로 수정했다. 문턱은0.5로 고정했다. 최종 양성 검출은0/8→8/8, 음성 오검출0, 최대 위치 오차0.752px였다. **최대 각도 오차44.931°로 정밀 정확도는 FAIL**이다. 이 가중치는 앱 기본 모델로 적용하지 않았다. Kensa/8K용 가중치를 학습했다는 주장도 하지 않는다.

학습 루프·세션 생성·가중치 저장 시간은 CPU26.21초, CUDA20.00초였다. 검증 추론은 이 시간과 별도다. CPU/GPU 학습이 가능한지 확인한 수치이며 학습 처리량의 통제된 성능 시험은 아니다.

## ONNX와 비교하는 조건

공식 가중치·Gray8 전처리·템플릿·512창·점수0.5·스레드4·자세 보정OFF를 고정한다. CUDA는 양쪽 TF32 OFF다. 각 입력/엔진/장치에서 첫 실행1회와 반복4회를 기록한다. GPU 실험은 RTX3060에서 실행하며 실패 시 CPU로 대체하지 않는다.

Kensa 부품 ROI는(200.5,184),89×52이고 근사 정답48개다. Kensa는 정밀 라벨 미확정이므로 `GroundTruthVerified=false`다. 8K ROI는(759.518,546.178),105.827×129.459이며 생성기에서 계산한 고정0° 정답256개를 사용한다. 예측 각도로 정답 위치를 바꾸지 않는다. 18V19×12는 기존 최소 변18px 제약 때문에 이번 부품 시험에 포함되지 않는다.

출력 텐서 대조와 실제 앱 검사는 서로 구별한다. CPU/CUDA의 FP32 연산 순서가 달라 비트 동등성을 주장하지 않는다. 정확도 판정은 위치1px/각도1°이며 대응 반경은 Kensa25px/8K60px다. 대응 반경을 합격 오차로 사용하지 않는다.

측정 시간은 명령 진입→원본 읽기→추론→후처리→결과 게시→필수 라벨·Present/fence→후속 WPF 렌더 관찰까지다. 최초 JIT/라이브러리 준비를 빼지 않는다. 물리 마우스 입력 메시지 대기/모니터 scan-out은 별도 미측정이며, 이미 측정 경계에서도 목표에 미달한다.

## 측정 결과

각 셀의 시간은 반복4회의 중앙값이며 서비스 계산만이 아니라 위의 표시 관찰 경계를 포함한다. 첫 실행은 원본 JSON에 별도 보존한다.

|입력|엔진|CPU 반복 중앙값|CUDA 반복 중앙값|검출/정답|최대 위치/각도 오차|
|---|---|---:|---:|---:|---|
|Kensa|ONNX|1191.19ms|136.75ms|28/48|6.727px /41.244°*|
|Kensa|TorchSharp|1425.67ms|183.66ms|28/48|6.727px /41.244°*|
|8K|ONNX|10216.82ms|893.12ms|110/256|17.252px /75.410°|
|8K|TorchSharp|11652.55ms|1276.84ms|110/256|17.252px /75.410°|

*Kensa는 앞서 설명한 근사 정답 대비 수치다. 두 엔진 모두 Kensa20개·8K146개 누락이며1px/1° 기준에 실패한다. 점수 문턱을 낮추지 않았다. 이번 구현/측정에서 TorchSharp의 속도 이점은 없었다. CUDA가 실제 사용되었지만125/80ms 목표는 실패다.

반복 범위도 보존한다. TorchSharp Kensa CUDA175.54~1143.51ms로 한 차례 큰 지연이 있었고 중앙값183.66ms만으로 지연 상한을 주장하지 않는다. TorchSharp8K CPU10131.19~13038.62ms, CUDA1230.38~1481.31ms였다. 사용자가 열어 둔 기존 앱은 종료하지 않았으며 별도 숨김 진단 창으로 측정했다. 전용 무부하 실험실의 지연 보장 수치가 아니다.

증거: [요약](posematch-torchsharp-comparison-summary.json), [TorchSharp Kensa](posematch-torch-kensa-comparison.json), [ONNX Kensa](posematch-onnx-kensa-comparison.json), [TorchSharp8K](posematch-torch-8k-comparison.json), [ONNX8K](posematch-onnx-8k-comparison.json).

네이티브224/256/512/1024창의 TorchSharp CPU 반복 중앙값은48.58/77.45/291.44/1318.76ms, CUDA는9.99/11.54/38.35/132.33ms다. CPU 기준 ONNX 출력 지도와의 최대 차이는 CPU1024에서0.001135, CUDA1024에서0.010768까지 있어 전체 크기에0.0002 동등성을 주장하지 않는다. 공식152×156 fixture의 CPU5개 지도는0.0002 미만 테스트를 통과했다. CUDA 동일 프로세스의 ONNX→TorchSharp→ONNX→TorchSharp 전환은 해당 fixture 최대2.10e-5 차이로 통과했다. [CPU 창](posematch-torchsharp-native-cpu.json), [CUDA 창](posematch-torchsharp-native-cuda.json), [엔진 전환](posematch-torchsharp-backend-switch.json).

가중치 경로를 실제 앱에 연결한 CPU/CUDA 외부 공식 파일 검사는 각각28개로 내장 모델과 같았다. [앱 파일 로딩](posematch-torchsharp-checkpoint-app.json). 학습 결과의 재로딩 동등성은 별도 자동 테스트로 확인했다. [CPU 학습](posematch-torchsharp-training-cpu.json), [CUDA 학습](posematch-torchsharp-training-cuda.json).

회귀 검증: TorchSharp5개, 기존 PoseMatch/서비스11개, 앱 모드/정확도35개, 총51개 PASS. Release 앱·학습 도구·벤치마크 빌드0경고/0오류. 전체 `scripts/Format-Code.ps1 -Verify`는 기존/병행 수정 파일의 줄 끝·공백 문제를 포함해 실패했으며 그 파일을 일괄 수정하지 않았다. 새 TorchSharp·학습 CLI·TorchSharp 테스트 프로젝트의 별도 whitespace 포맷 검증은 PASS다. 학습/검증 SourceId 중복 입력도 파일 저장 없이 거부하는 것을 확인했다. 현장 정확도/18V/수정 후 실물 임의 회전은 기능 테스트 PASS와 구별한다.

## 원칙 검토와 제약

SOLID: SRP는 C# 네트워크/가중치 파일/학습/검색/진단 분리, OCP는 기존 matcher 계약으로 두 공급자 연결, LSP는 동일 입력·결과·오류·수명 및 지도 대조(취소 관찰 시점 차이 명시), ISP는 소비자가 쓰는 Predict/Find/Dispose만 노출, DIP는 VisionInspection에 TorchSharp 구체 의존성을 넣지 않고 호스트가 주입한다.

GRASP: Information Expert는 네트워크가 tensor/gradient, 서비스가 원본 좌표 담당; Creator는 matcher/trainer가 모델 소유; Controller는 기존 VM과 학습 CLI; Low Coupling은 중립 픽셀·pose 계약; High Cohesion은 학습과 UI 검사 분리; Polymorphism은 두 matcher 공급자; Pure Fabrication은 가중치 직렬화와 검증; Indirection은 App 생성 함수; Protected Variations는 LibTorch/ONNX 격리다. 원칙 예외 없음. ROI 편집·이력·표시 렌더러 구조는 변경하지 않았다.

LibTorch의 스레드 수·TF32 설정은 프로세스 전역이므로 같은 프로세스에서 서로 다른 설정의 trainer/matcher를 동시에 실행하지 않는다. 학습 CLI와 앱은 별도 프로세스로 운영한다. 네이티브 호출 중 취소는 호출이 돌아온 뒤 관찰한다. 플랫폼 배포는 Windows x64에서만 검증했다. 잘못된 가중치나 CUDA 실패에서 공식 모델/CPU로 조용히 대체하지 않는다.
