# PoseMatch-TDCM C# 사용 및 검증

## 구성과 사용

패턴 패널에서 **PoseMatch-TDCM · ONNX** → 마스크 없는 사각형 ROI → 학습 → 검사 실행. 가중치는 DLL에 포함하므로 인터넷이나 Python 없이 실행된다. 학습 버튼은 사용자 템플릿을 등록하며 신경망 재학습을 수행하지 않는다. 기존 논문 형상/OpenCV 모델과 설정은 독립적으로 유지된다. 삭제한 자체 기하 enum값2를 재사용하지 않고 새 모드는3이다.

- 원본 학습: 각 변18~2048px, 비율1:2~2:1, 회전 사각형 지원. 내부36×36. 마스크/비사각형 학습은 오류로 알린다.
- 검색: 전체 이미지 또는 선택한 실제 ROI 안의 중심점. 원본 Gray8와 면적 평균 축소를 사용하며 화면 표시 LOD는 사용하지 않는다. 불완전한 마지막 원본 면적도 읽는다.
- 출력: 원본 좌표 중심, 영상 좌표 시계 방향 각도, 독립 가로/세로 크기, 신경망 신뢰도. 성공 검색은 사용자 확인(Gold), 예산 초과는 경고(Orange). 제품 불량 판정이나 원본 세부 결함 검사는 아니다.
- CPU/CUDA ONNX Runtime. 기존 논문 모드의 CUDA 선택과 별개다. DX12는 표시만 담당한다. [CUDA 설치·실측·제약](posematch-tdcm-cuda.md)을 따른다.

| 파라미터 | 기본값 | 의미 |
|---|---:|---|
| 최소 신뢰도 |0.5|국소 극대점의 신경망 점수, 품질 일치율 아님|
| 최대 결과/후보 |256/4096|초과 여부를 구별하여 표시|
| 추론 창 |512|224~1024,4배수.128px 겹침. 가장자리는 같은 크기에0으로 채움|
| CPU 스레드 |4|1~32, ONNX 세션 내부 병렬도|
| 최대 창 |4096|초과 시 전체 검색 완료로 처리하지 않음|
| 중복 억제 반경 |18|축소 영상 px,1~144|
| 중심 보정 반경 |15|축소 영상 px,0~72. 공식 >0.5 점수 중심 평균|
| 명암 자세 보정 |OFF|각도→X/Y배율→각도 코사인 유사도 탐색. CPU 추가 비용|
| 시간 한도 |0초|0은 제한 없음. ONNX/타일/후처리 취소 연동|

## 원본과 변환

[논문](https://arxiv.org/html/2510.01678v1), [공식 MIT 저장소](https://github.com/ZhouJ6610/PoseMatch-TDCM), revision `a6adc3a74fcf38ddbe4beb49e4bc76be07f848a2`. 가중치 `dict/model.pth`를 `weights_only=True`로 읽고 strict load했다. 양쪽 ConvNeXtV2 stem/stage1, 동적 depthwise convolution, PixelShuffle, score/sign/cos/X/Yscale의 다섯 head를 유지했다.

`scripts/Export-PoseMatch.py`가 opset17, batch1, 동적 검색 H/W, 고정36×36 템플릿 모델을 만든다. SHA256/변환 도구 버전/오차는 [conversion.json](../src/PoseMatch.Tdcm/Models/conversion.json)에 있다. PyTorch는 격리된 변환 환경에서만 필요하다. 실행 프로젝트에는 Python/Torch 의존성이 없다. 공식 test.py의 **BGR 순서에 ImageNet 평균/표준편차 적용**을 그대로 따른다. 앱 원본 경로는 Gray8을3채널 복제하므로 컬러 예제와 입력 조건이 다르다.

변환 재현 시 Python3.12의 별도 venv에 `scripts/posematch-export-requirements.txt`를 설치하고 공식 저장소를 위 revision으로 checkout한 뒤 `python scripts/Export-PoseMatch.py artifacts/posematch-tdcm-upstream src/PoseMatch.Tdcm/Models`를 실행한다. 스크립트는 revision과 모델/가중치 수정 여부를 검사한다. 변환용 ONNX Runtime1.31.0과 배포용 C#1.24.4를 각각 실제 golden 출력으로 검증했다.

공식 test.py의 선택적 OpenCV 명암 보정과 달리 C# 옵션은 bilinear 역방향 샘플링과 유한 각도/배율 탐색이다. 신경망 출력 일치 검증과 이 후처리의 정확도 검증을 구별한다. 여러 대상을 찾는 국소 극대점/NMS와 큰 영상 타일 탐색은 C# 확장이다. 네트워크의 GRN 때문에 창 크기를 바꾸면 같은 위치의 점수가 달라질 수 있다.

## 검증과 재현

```powershell
dotnet test tests/PoseMatch.Tdcm.Tests/PoseMatch.Tdcm.Tests.csproj -c Release
dotnet test tests/TeachingToolWpf.Imaging.Tests/TeachingToolWpf.Imaging.Tests.csproj -c Release
dotnet run --project benchmarks/PoseMatch.Tdcm.Benchmarks/PoseMatch.Tdcm.Benchmarks.csproj -c Release -- docs/posematch-tdcm-benchmark.json
dotnet run --project src/TeachingToolWpf/TeachingToolWpf.csproj -c Release -- --inspection-smoke
```

- PASS: PyTorch/ONNX 224²,320×256,512² 다섯 head 비교(절대/상대 허용2e-4), 공식 이미지의 C# 독립 golden probe/argmax 대조.
- PASS: 실제 명암 자세 보정의 공식 예제 각도45~55°, 폭/높이28~36px. 원 예제 기준+50°, 약32px.
- PASS: 취소/잘못된 크기/Dispose, 독립 X/Y배율, 결과 상한, 원본 reader 수명, 큰 회전 학습 ROI, 마스크 거부, 창 예산 경고.
- PASS: 앱 독립 모델·설정,180° 출력 보존, 마스크 변경 시 재학습 요구, 기존 두 모드 회귀.
- PASS: 실제 WPF 모드 선택→원본 ROI 학습→ONNX 검색→DX12 표시. 사용자 앱과 별도 숨긴 진단 인스턴스에서 기존 검사 회귀도 통과.
- PASS: 전체 솔루션 회귀1,252개(Core371/App280/Algorithm194/Canvas235/Compute52/Inspection113/TDCM7). 최종 옵션 상한 수정 후 App280/TDCM7 재검증. 최종 고정 창 앱 smoke 종료 코드0.
- PASS: 224/256/512/1024 추론 창과36/72/128/256/512/1024/2048 크기 계열의 원본 서비스 벤치마크 완료. 이 PASS는 측정 완료이며 성능/정확도 목표 충족이 아니다.

## 측정 해석

[기계 판독 측정 결과](posematch-tdcm-benchmark.json)는 원본 읽기/학습/검색과 순수 창 추론을 분리한다. 창 측정은 첫 회와3회 반복, 서비스 크기별 측정은1회이다. 원본 서비스 입력은 공식 예제를 확대해 Gray8으로 공급하는 절차적 타일 소스이며 실제8K 파일 디코드나 현장 검출률 시험이 아니다. 단일 예제의 가장 가까운 결과 오차만으로 전체 검출 정확도를 주장하지 않는다. 신뢰도0.5에서는 추가 후보가 나오는 조건도 있다.

기준 PC Ryzen5 5600H,4스레드, Release, 명암 자세 보정OFF. 검색8192×8192에서:

| 크기 계열 | 실제 학습 ROI | 서비스 ms | 검색 창 | 반환 수 | 가장 가까운 결과 위치 오차(px) |
|---:|---:|---:|---:|---:|---:|
|36|31×33|114264.0|484|2|1.19|
|72|62×66|26963.0|121|2|3.71|
|128|110×117|8071.5|36|1|6.44|
|256|220×235|2329.1|9|1|10.44|
|512|441×469|445.5|1|1|20.13|
|1024|882×939|505.5|1|1|50.06|
|2048|1764×1877|569.2|1|2|63.56|

최대2048×2048의 회전 학습 입력 수용은 별도 테스트했다. 위 크기 계열은 공식31×33 템플릿을 비례 확대하므로 실제 ROI 치수를 함께 적었다. 모델 입력은 모든 경우36×36이다. 정답으로 정의한 대상은1개이며 반환 수2는 추가 후보가 있다는 뜻이다. 큰 ROI 조건의 수십 px 오차는 정밀 검사에 그대로 사용할 수 있는 정확도가 아니다.

224/256/512/1024 정사각 추론 창의 준비 후3회 중앙값은 각각39.22/54.58/237.53/1093.72ms다. 세션 최초 생성169.48ms는 별도다. 창 처리에는 정규화와 후처리가 포함된다. 서비스 표에는 원본 읽기와 세션 생성이 포함되지만 작업 큐 대기·using 종료 정리·UI 게시/표시는 포함되지 않는다. 최종 고정 창 앱 smoke의 TDCM 서비스는395.7ms(읽기8.5, 세션 준비112.8, 추론/후처리269.4,1창)였다. 이는 기존 smoke 입력의 기능 검증 수치이며8K나 클릭부터 표시 완료 수치가 아니다.

창을 실제 남은 영상 크기로 자동 축소한 시도는1024계열에서 검출0개 회귀가 있어 폐기했다. 고정512창에서 다시 검출1개를 확인했다. GRN은 창 전체의 통계에 영향을 받으므로 사용자가 창 설정을 변경할 때 정확도를 재검증해야 한다.

CPU125ms/CUDA80ms 목표는 달성하지 못했다. WPF 게시/표시 시간을 제외한 서비스 수치를 클릭부터 표시 완료까지의 수치로 사용하지 않는다. 큰 ROI의 내부36×36 정규화는 원본 세부를 잃으므로 크기를 키운다고 정확도가 좋아지지 않는다. 모델의 대상 크기/도메인과 맞는지 실제 입력으로 먼저 확인해야 한다. 후속 ONNX CUDA 경로와 RTX3060 실측은 [CUDA 검증](posematch-tdcm-cuda.md)에 기록했다. Linux 실제 실행, 현장 Kensa18V 검출률/180° 제품 불량 판정은 미검증이다.
