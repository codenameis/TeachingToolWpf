# PoseMatch-TDCM CPU/CUDA 전체 탐색 검증

2026-10-15. 사용자는 **PoseMatch 전체 탐색 유지**, 로컬 수정·검증만 확정했다. 전통적 형상 후보 검색으로 교체하지 않는다. 목표는 검사 버튼 입력부터 결과 표시까지 CPU 125ms 이하, CUDA 80ms 미만이며 **아직 FAIL**이다. CUDA 실행 성공, 후보 개수 일치, 정밀 검출 정확도, 지연 시간 합격은 서로 다르다.

## 구현

- ONNX Runtime GPU 1.24.4에서 CPU/CUDA를 명시적으로 선택한다. CUDA 장치 0, FP32, TF32 비활성, HEURISTIC convolution 계획을 사용한다. CUDA DLL/장치 오류를 CPU 성공으로 바꾸지 않는다.
- 앱의 PoseMatch 설정에 장치 선택을 추가했다. 서비스가 장치/스레드 설정별 현재 세션 하나를 재사용하며 변경 시 교체한다. App이 소유·해제하고, 실행 중 Dispose는 네이티브 호출 종료 후 해제한다. 영상 준비/검색 결과는 검사 간 캐시하지 않는다.
- 다섯 출력 head의 FP32 convolution을 결합했다. 가중치·출력 채널·활성화·전체 검색 범위를 보존하고 공식 PyTorch 출력과 재대조했다. 변환 스크립트와 `Models/conversion.json`에 원본/최적화 모델 해시가 남는다.
- 같은 크기 영상 정규화는 bilinear 대신 동일한 byte→float 계산표를 사용한다. 원본 Gray8 직접 공급 계약이 있는 reader는 중간 BGRA 변환을 생략한다.
- 최근 20회 명령 진입·서비스·결과 게시·명령 완료·라벨 준비/Present/fence 완료를 별도로 기록하고 JSON 저장한다. 표시 통지가 없으면 시간이 비어 있으며 성공값으로 채우지 않는다.

## 재현

장비: Ryzen 5 5600H, RTX 3060 Laptop 6GB, Windows x64, NVIDIA 581.83. `nvidia-smi`의 CUDA 13.0은 드라이버 지원 정보이며 앱 런타임은 CUDA 12다.

개발용 NVIDIA wheel의 DLL만 사용한다. C# 앱 실행에 Python은 필요 없다. 시스템 PATH/드라이버는 수정하지 않았다. 로컬 개발 환경 설치에 사용한 명령:

```powershell
artifacts/posematch-export-env/Scripts/python.exe -m pip install --target artifacts/tdcm-cuda-runtime nvidia-cuda-runtime-cu12==12.8.90 nvidia-cublas-cu12==12.8.4.1 nvidia-cudnn-cu12==9.10.2.21
artifacts/posematch-export-env/Scripts/python.exe -m pip install --target artifacts/tdcm-cuda-extra nvidia-cufft-cu12==11.3.3.83 nvidia-nvjitlink-cu12==12.9.86
dotnet build src/TeachingToolWpf/TeachingToolWpf.csproj -c Release --artifacts-path artifacts/tdcm-grid --packages .packages
scripts/Start-TdcmCuda.ps1 artifacts/tdcm-grid/bin/TeachingToolWpf/release/TeachingToolWpf.exe
```

실행 후 패턴 방식 `PoseMatch-TDCM · ONNX`와 장치 `Cuda`를 선택한다. CPU는 위 NVIDIA DLL 없이 실행할 수 있다. 배포 시 NVIDIA DLL의 해당 버전 라이선스·재배포 조건과 고지를 함께 유지해야 한다. 개발 runtime 폴더는 Git 추적/일반 배포 산출물에 포함하지 않았다.

동일 실행기에 `--tdcm-latency`를 넘기면 실제 8192² 샘플의 CPU/CUDA 최초·반복 명령을 측정한다. `--tdcm-accuracy-grid`는 창256/512와 신뢰도0.1/0.2/0.3/0.5를 비교한다. `--tdcm-refined-pins`는 신뢰도0.2와 명암 자세 보정 ON을 별도로 측정한다. `--tdcm-kensa`는 실제 `kensa.png`의89×52 부품 ROI를 사용한다. 이 ROI는 18V 텍스트 ROI가 아니다.

독립 벤치마크는 `PoseMatch.Tdcm.Benchmarks.exe <JSON경로> --cuda`로 원본 ROI36~2048 크기 계열을 측정한다. `--native-only`는 창224/256/512/1024만, `--accuracy`는 공식 예제의90° 단위 회전을 검사한다. 서비스/창 수치는 앱 표시 시간을 포함하지 않는다.

## 실험 기록

초기 CUDA 프로파일의 공급자별 연산 이벤트3160개가 모두 `CUDAExecutionProvider`였다. 단순 GPU 선택 표시만으로 CUDA 실행을 판단하지 않았다. FP32 CPU/CUDA의 다섯 지도 최대 절대 차이는 창224에서0.000074,1024에서0.01078이며 비트 단위 동등성을 주장하지 않는다. 공식 회전 예제에서는 두 장치의 검출 수/중심은 같았다.

초기 FP32 서비스 벤치마크(원본 읽기 포함, WPF 제외):

| 학습 크기 계열 |8K 창 수|CUDA ms|최인접 위치 오차 px|
|---:|---:|---:|---:|
|36|484|13320.6|1.19|
|72|121|3541.3|3.71|
|128|36|1169.1|6.44|
|256|9|493.0|10.44|
|512|1|246.7|20.13|
|1024|1|268.2|50.06|
|2048|1|407.0|63.56|

이는 공식 예제 하나를 확대해 만든 합성 입력이다. 현장 검출률이나 8K 전 영역의 다양한 부품 정확도가 아니다. 큰 ROI 계열은 읽기만209~312ms였으며 작은 ROI는 신경망 창 반복이 지배했다.

8K 앱 초기 검사: 신뢰도0.5/창512에서 CPU 표시8.0~8.6초, CUDA1.15~1.75초, 출력23개로 기대256개에 미달했다. 신뢰도0.2에서256개가 실제 핀 근처에 대응했지만, 기준 위치 오차 평균13.38px/최대19.83px이고 각도도 잘못 추정했다. 이 수치는 **후보 재현**, 정밀 매칭 PASS가 아니다. 사용자의 신뢰도 기본값을 조용히 낮추지 않았다.

명암 자세 보정 ON도 위치를 정밀화하지 못하고 일부 각도 오차가 남았다. 보정 ON의 초기 전체 표시 시간은 CPU11.8~13.0초, CUDA5.50~5.53초였다. 신뢰도를 품질 점수로 사용하거나 자동 제품 OK/NG를 부여하지 않는다.

| 실험 | 결과 | 처리 |
|---|---|---|
|명암 목적함수의 위치 이웃 탐색|공식 예제 각도 개선 대신 중심 오차0.52→3.02px|되돌림|
|FP16 일괄 변환|BatchNorm 분산71727이 표현 범위를 넘어 잘림, 지도/검출 변화|미적용|
|정규화 FP32 유지 혼합 정밀도|오차·커널 준비 비용 증가, 안정적인 충분한 이득 없음|미적용|
|FP32 다섯 head 결합|512창 CPU 순수 추론 중앙값 약262→198ms, 최대 지도 차이 약1.3e-6|채택, 공식 PyTorch 대조 재실행|
|CUDA 세션 재사용|반복 초기화 비용 제거, 최초 준비는 계속 명령 시간에 포함|채택|

최초 CUDA 실행은 세션/커널 준비만으로 목표를 초과한다. 준비 비용을 학습/앱 시작으로 옮겨 최초 검사 목표 달성처럼 표현하지 않았다. 전체 탐색을 유지하면서 현장 정확도와125/80ms를 함께 만족한 구현은 아직 없다.

## 최종 검증 결과

관련7개 테스트 어셈블리 합계 **1,253개 PASS**: Core371, 앱280, Algorithm194, Canvas235, Compute52, Inspection113, PoseMatch8. 전체 실행 중 Kensa 진단의 누락 namespace로 앱 컴파일이 실패했고 이를 수정한 뒤 앱280개를 다시 통과했다. 이후 시간 로그 정리 변경도 PoseMatch8/앱280을 재실행했다. 기능 테스트 통과는 현장 정확도나 지연 목표 통과를 뜻하지 않는다.

최종8K 실행의 신뢰도는0.5, 창512, 스레드4, 자세 보정OFF다. 각 장치의 첫 실행과 같은 프로세스의 반복을 구별한다.

| 장치/회차 | 서비스 ms |명령→Present/fence ms|WPF 렌더 관찰 포함 ms|출력/기대|
|---|---:|---:|---:|---:|
|CPU 첫 실행|8841.79|8886.33|9019.71|23/256|
|CPU 반복|7998.03|8004.78|8084.13|23/256|
|CUDA 첫 실행|1461.84|1477.19|1624.12|23/256|
|CUDA 반복|818.27|822.07|1004.76|23/256|

표시 시간에는 원본 읽기·세션/커널 준비·추론·출력 지도 복사·후처리·reader 정리·UI 게시·필수 라벨·GPU fence와 후속 WPF 렌더 관찰을 포함한다. 진단의 표시 통지 polling/Dispatcher 대기도 포함된 보수적 측정이다. 물리 모니터 scan-out과 실제 마우스 메시지의 명령 진입 전 대기는 측정하지 못했으므로 완전한 물리 클릭→광자 지연으로 표현하지 않는다. 이미 이 경계에서 목표가 FAIL이므로 제외 비용으로 합격을 만들지 않는다.

Kensa89×52 부품 ROI에서는 출력1/기대48, 반복 표시CPU994.44ms/CUDA123.53ms로 정확도·성능 모두 FAIL이다. 18V 텍스트의19×12 ROI는 현재 라이브러리의18px 최소 변 제한에도 맞지 않으며, 이 검사로18V 문제 해결을 주장하지 않는다.

최종 창512 반복 CUDA 중앙값24.59ms는 초기34.57ms보다 줄었으나, 전체8K 목표와 구별한다. 전체 CPU 지연의 초기/최종 차이는 실행 편차가 있어 큰 개선을 주장하지 않는다. 최종 창1024 CUDA도 반복 약80ms이며 단일 창만으로 전체 검사 예산을 거의 소비한다.

증거: [최종 앱8K](posematch-tdcm-app-latency.json), [Kensa](posematch-tdcm-kensa.json), [최종 CPU 창](posematch-tdcm-native-cpu.json), [최종 CUDA 창](posematch-tdcm-native-cuda.json), [초기 크기별 CUDA 서비스](posematch-tdcm-cuda-benchmark.json), [8K 문턱/창 정확도 실험](posematch-tdcm-accuracy-grid.json), [명암 보정 실험](posematch-tdcm-refined-pins.json), [CPU 회전](posematch-tdcm-rotation-cpu.json), [CUDA 회전](posematch-tdcm-rotation-cuda.json).

## 원칙 검토

SOLID: SRP는 추론/원본 reader/VM/진단 분리, OCP는 기존 두 방식 보존, LSP는 CPU/CUDA의 입력·취소·오류 계약 유지(수치 비트 동등성은 없음), ISP는 기존 서비스 계약 유지, DIP는 App 소유/VM 계약 주입이다.

GRASP: Information Expert는 모델의 텐서 정규화와 서비스의 원본 좌표, Creator는 App/세션 소유 서비스, Controller는 VM, Low Coupling은 중립 픽셀·pose, High Cohesion은 추론/진단 책임 분리, Polymorphism은 기존 검사 서비스 계약, Pure Fabrication은 실행 세션·로그, Indirection은 ONNX 어댑터, Protected Variations는 공급자 경계/오류 보존으로 확인했다. 편집·이력은 변경하지 않았다. 설계 원칙 예외는 없다.

근거: [ONNX Runtime CUDA 공급자](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html), [C# CUDA 옵션](https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.OrtCUDAProviderOptions.html), [혼합 정밀도 지침](https://onnxruntime.ai/docs/performance/model-optimizations/float16.html). 기본 공식 모델의 원본/라이선스는 [PoseMatch 안내](posematch-tdcm.md)를 따른다.
