# ADR-058: 독립 영상 이상 감지 앱과 표시 컨트롤

- 날짜: 2026-10-16
- 상태: 구현. 로컬 검증만, 커밋·푸시하지 않음.
- 사용자 결정: 관제실형 HTML을 새 프로젝트/컨트롤로 만들며 실제 영상 입력·정상 학습·이상 감지까지 포함한다.

## 선택

별도 `AnomalyMonitor.slnx`에 알고리즘/세션 `VisionAnomaly`, 표시 `VisionAnomaly.Controls`, WPF MVVM 호스트 `AnomalyMonitor`와 테스트를 둔다. 기존 티칭 앱 및 검사 알고리즘은 변경하지 않는다. 표시에는 기존 DX12/WARP `VisionCanvas`와 중립 `VideoFrame`을 재사용한다.

`CameraSession`은 입력과 분석 worker의 수명을 소유한다. 각 영상은 독립 정상 모델과 시간 상태를 갖는다. App은 서비스·컨트롤·VM을 조립하고 생산자 종료를 기다린 뒤 표시 자원을 해제한다. VM은 구체 viewport를 알지 않는다. `MonitorLink`가 원본 좌표의 중립 검사 결과를 표시 자료로 옮긴다.

## 알고리즘 결정과 차이

[PatchCore 논문](https://arxiv.org/abs/2106.08265)과 [공식 구현](https://github.com/amazon-science/patchcore-inspection)의 정상 패치 메모리/최근접 거리 개념을 사용한다. 현재 구현은 고정 [Torchvision ResNet18 ImageNet1K_V1](https://docs.pytorch.org/vision/stable/models/generated/torchvision.models.resnet18.html)의 layer2/3를 사용한다. 국소 평균 후 28×28에 맞추고 384채널 중 매4번째 채널을 사용해96차원 패치로 만든다. CPU ONNX로 실행한다.

30~200개 정상 프레임 중 매5번째는 외관 임계값 보정에 보관한다. 나머지 패치에서 reservoir8192개를 모은 뒤 farthest-first512개를 선택한다. 최근접 RMS 거리의 프레임 최댓값을 사용하며 보정 프레임 최댓값의1.15배를 기준으로 둔다. 원문의 WideResNet50/전체 후보/점수 재가중을 재현하지 않았고 PatchCore 벤치마크 수치를 인용해 이 앱 성능으로 주장하지 않는다. 백본 미세학습은 하지 않는다.

움직임은 [OpenCV Farneback](https://docs.opencv.org/4.x/dc/d6b/group__video__track.html) flow를8×8영역의 X/Y속도·속력으로 요약한다. 미디어 시간 차로 정규화하며 정상 평균/표준편차와의 차이를 비교한다. AI-VAD의 자세·객체·행동 특징 전체를 구현한 것이 아니다.

## 입력·판정 계약

파일/USB/RTSP 입력은 OpenCV VideoCapture를 사용한다. 기존 slim 패키지는 video/videoio 모듈이 없어 **새 앱만 full Windows runtime**을 명시한다. 파일은 미디어 시각, 실시간은 단조 시각을 사용한다. RTSP/HTTP 열기5초·읽기2초 timeout을 요청하지만 실제 backend/device의 취소 지연은 별도 검증 대상이다.

원본 크기로 표시/증거 저장하고 관심 영역은224×224로 분석한다. 대기열은 최신1개, 분석 완료 후200ms 대기다. 따라서 짧은 사건·미세 결함 누락 가능성과 건너뜀을 명시한다. 원본8K 표시와8K 세부 검사·60FPS는 구별한다.

외관은 한 분석 프레임으로 알람을 내고 움직임은2개 이상 프레임과0.6초 지속을 요구한다. 정상 회복3프레임의 히스테리시스로 중복을 줄인다. 파일 끝·연결 실패·취소는 정상/불량 제품 판정으로 바꾸지 않는다. 알람 검토 결과는 모델 학습과 분리한다. 모델 교체는 명시적이며 특징 파일 hash/해상도/수치/영역을 검증한다.

## 검증과 남은 범위

기능과 데이터·원시 결과는 [검증 기록](../anomaly-monitor-validation.md), 사용법은 [앱 안내](../../src/AnomalyMonitor/README.md)에 둔다. USB/RTSP 실기, 현장 CCTV/제품 정확도,4채널8K 부하, 장시간 저장 용량 관리와 별도 PC 배포는 미검증이다. 이전 티칭 앱의 성능 목표를 이 신규 기능의 합격 결과로 대신하지 않는다.
