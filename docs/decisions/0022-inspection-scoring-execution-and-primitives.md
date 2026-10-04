# ADR-022: 추가 윤곽 점수, 검사 실행 제어와 일반 도형 검출

> 후속: [ADR-023](0023-portable-inspection-and-candidate-budget.md)에서 빠르게 프리셋을 제거하고 검사 DX12를 ILGPU CUDA로 교체했다. 아래 프리셋·DX12·647개 테스트·성능 수치는 교체 전 시점의 기록이다. 점수와 일반 도형 계약은 유지한다.

- 일자: 2026-10-04
- 상태: 구현 및 합성·실제 앱 검증 완료. 현장 검출률과 전체 GPU 검색은 미검증·후속 범위다. 최종 증거는 `tasks/todo.md` 참조.
- 근거: 학습 핀 주변에 대각선이 추가되어도 커스텀 매칭 점수가0.999로 표시된다는 사용자 보고, 속도/GPU/일반 도형 요청.

## 점수 계약

기존 방향 점수는 선택된 학습 특징 위치의 gradient 방향을 확인하여 다른 위치에 추가된 윤곽을 감점하지 않았다. 방향 점수는 ROI 전체의 동일성이나 확률이 아니다.

최종 점수는 `DirectionScore × (1 − ClutterWeight × ClutterRatio)`다. ClutterRatio는 최종 pose의 실제 학습 마스크 안 scene edge 중 전체 학습 윤곽으로 설명되지 않는 비율이다. 면적/픽셀 차이/불량 확률이 아니다. 마스크 밖은 감점하지 않는다. 방향 점수는 누락·방향 불일치를 계속 평가한다.

앱의 논문/자체 기하 방식은 감점 비중 기본1, 라이브러리는 호환을 위해0이다. `BuildClutterModel`로 논문 모드도 공통 전체 윤곽·마스크를 저장하며 `GeometryModel`은 여전히 null이다. 기하 모드의 coverage/RMS/최대clutter gate는 별도다. 전체 윤곽 저장소는 공유하고 이중 할당하지 않는다.

축소 후보는 방향 점수를 유지하고 감점은 최종 원본 pose에서만 수행한다. 최소점수·정렬·NMS·출력 상한은 최종 점수에 적용한다. 원본 모델 누락 또는 Verify=false이면 후보가 없어도 서비스 시작에서 거부한다. 정밀화는 오래된 방향/추가윤곽/기하 값을 무효화한다. 비중과 대응 허용 거리는 사용자 설정이다. OpenCV NCC 점수식은 변경하지 않는다.

합성 재현: 깨끗한 핀1.000, 대각선 추가 핀은 방향1.000/추가윤곽25.2%/최종약0.748. 첨부 스크린샷에서 직접 측정한 값이 아니다. 앱8K procedural PCB/기존 사용자ROI에서는256개를 유지하고 최저 최종점수가약0.79로 내려갔다.

## 실행 설정

각 방식의 프리셋은 아래 값만 변경한다. 합격 점수·최대 결과 수·학습 모델·감점 비중을 유지한다.

| 프리셋 | 후보 | 원본 검증 후보/창 | 축소 검증 후보/창 | 정밀화 |
|---|---:|---:|---:|---|
| 빠르게 | 512 | 32 | 64 | 끔 |
| 균형 | 2048 | 64 | 256 | 켬 |
| 정밀 | 8192 | 256 | 1024 | 켬 |

OpenCV에는 후보 수만 적용한다. 프리셋과 실제 값이 다르면 사용자 설정으로 표시한다. CPU 병렬도1~16은 별도이며 기본1이다. 예산 축소는 누락/불완전 경고를 만들 수 있어 배속을 보장하지 않는다. 패턴/일반 도형의 검사 시간 한도는0~600초,0은 무제한이다. 시간 초과·취소·오류·늦은 반환은 OK/NG로 게시하지 않는다. 제출된 GPU/네이티브 호출은 안전하게 완료한 뒤 취소하므로 종료 시각이 한도를 넘길 수 있다.

## GPU 경계

중립 `VisionAlgorithm.OpenSrc.PatternMatching.IImageSmoother`로 평활화 배열을 주입한다. 구현은 앱 `Imaging/Compute`에 있고 VisionAlgorithm/Core/Canvas에 DX12 의존성을 추가하지 않는다. CPU가 기본이다. GPU는 실제 DX12 compute로 `[1,4,6,4,1]/16` 가로·세로 평활화를 수행한다. gradient·후보·점수·정밀화는 CPU다. 전체 GPU 매칭/학습이라고 표현하지 않는다.

렌더러와 queue/fence/명령 메모리/검사 버퍼를 공유하지 않는다. D3D12 device는 adapter별 singleton이므로 자체 COM 참조와 독립 queue/resource 수명을 소유한다. 전역 debug 상태와 렌더러 장치를 강제 변경하지 않는다. GPU 오류는 명시적으로 보고하고 CPU로 조용히 전환하지 않는다. 완료 또는 장치 제거 뒤 자원을 반환한다. Signal 실패는 같은 값의 실제 enqueue와 drain 후 최초 오류를 보고한다. 장치가 정상이라고 보고하면서 Signal이 영구 실패하면 완료 증거가 없으므로 worker가 버퍼를 보유하며 재시도한다. 시간 한도는 강제 해제 수단이 아니다.

adapter/완료 호출/픽셀 수/전송·대기·readback 포함 wall time을 기록한다. GPU timestamp와 전체 검사 시간과 구별한다. RTX3060에서 독립 정수 convolution, stride/offset/홀수 크기, 극단 종횡비8Mpx, 재사용/취소/해제와 최종 검색 동등성을 검증했다.

## 일반 도형과 앱 경계

`OpenSrc/ShapeDetection`은 학습 없이 이진 닫힌 외곽을 분류한다. Mat은 기존 허용 경계 `PatternMatching/Imaging/PrimitiveContourExtractor`에만 둔다. 분류·계약은 중립 C# 데이터다. 삼각형, 직각 사각형, 원, 대칭 십자가가 대상이다. 타원·T/L/I·마름모·링은 음성 검증한다. 채워지고 분리된 도형을 지원하며 열린 선/테두리만/붙음/부분 가림은 지원하지 않는다.

자동 Otsu/수동 문턱, 밝음/어두움/자동 극성, 도형 선택, 크기6~512px, 윤곽 근사, 최소 점수, 최대 결과 수, 시간 한도를 노출한다. 원은 반경 일관성, 다각형은 근사 면적 일치를 평가하므로 종류별 점수를 검출 확률로 비교하지 않는다.

독립 원본 reader가2Mpx 이내 창과 최대 도형 폭보다 큰 halo를 준비한다. 중심 소유권으로 중복을 막고 선택 ROI는 실제 윤곽 전체를 포함해야 한다. 잘림·예산/출력 상한은 경고다. 실제 닫힌 윤곽을 표시한다. 독립 서비스/VM/패널을 사용하고 `InspectionToolsViewModel`은 선택/단일 실행/overlay 선택만 조정한다. 도구 가용성과 라이브 입력 유효성을 분리해 같은 정지 프레임의 결과를 도구 전환에서 보존한다. 검사 결과는 편집 이력에 넣지 않는다. 샘플 이미지 교체는 기존 원자적 Undo/Redo 경로를 사용한다.

## 실측

원본8192² procedural PCB, 기존 핀ROI(중심759.518/546.178,105.827×129.459), 최소점수.72, 최대결과1000. 설정마다 워밍업1/측정3. 모두256/256 핀 일대일 검출과1px 위치 기준 통과. CPU/GPU pose·점수·clutter 동등. 노트북 전원/열은 통제하지 않았다.

| 설정 | 중앙값 ms |
|---|---:|
| 기존 방향 점수만 | 4220.96 |
| 새 감점·CPU 균형 | 4213.40 |
| CPU 빠르게 | 4158.23 |
| CPU 정밀 | 4198.97 |
| CPU 균형·병렬4 | 4288.23 |
| GPU 전처리·균형 | 4508.38 |

이 입력은 예산에 도달하지 않아 프리셋 차이가 작다. GPU는 전송/대기와 작은 crop 반복으로 전체 기준약7% 느렸으며 가속 달성으로 보고하지 않는다. 원본은 `docs/data/inspection-controls-20261004.json`, 실행 기록은 `artifacts/inspection-controls-benchmark.log`다. 실촬영8K/24K 검출률·실시간FPS는 미검증이다.

## 최종 검증

Release 전체 647개 PASS(Core 182 / 앱 227 / Algorithm 138 / Canvas 100), skip 0. `artifacts/inspection-controls-verified-tests.log`와 프로젝트별 TRX에 결과를 기록했다. `Format-Code.ps1 -Verify`도 통과했다.

별도 배포본의 RTX 3060 Laptop / DX12 debug 검사 smoke는 exit 0이며 렌더 오류·버퍼 상태 경고가 없다. CPU·GPU 각각 8K 핀 256개, 일반 도형 4종, 폴리곤·검색 점선·기존 판정색, 샘플 Undo/Redo를 검증했다. 초기 기본 힙 UAV 상태 경고는 COMMON 생성과 버퍼 승격·감쇠 규칙에 맞춰 수정했고 실제 GPU 동등성 14개를 다시 통과했다. BMP 샘플은 배포 디코더에서 지원하지 않아 같은 원본 픽셀의 PNG로 교체했다. 실제 PNG 로더의 307,200개 픽셀 전수 일치와 4종 검출도 테스트했다.

직접 실행한 새 앱에서 도형 샘플을 열고 도형 검출을 눌러 4개 / 78.9ms를 확인했다. 실제 Windows 창 스크린샷은 `artifacts/inspection-controls-user-state/primitives.jpg`다. DX12 readback 합성 캡처와 구별한다. 새 앱은 결과를 표시한 상태로 유지했고 기존 사용자 창을 종료하지 않았다. 단일 UI 실행 시간은 반복 측정 중앙값과 구별한다.

## 설계 검토

SOLID: SRP(점수/장치/도형/조정 분리), OCP(평활화 교체), LSP(동일 픽셀·취소·실패 계약), ISP(좁은 계약), DIP(알고리즘/Core 독립) 확인.

GRASP: Information Expert(윤곽/마스크 계산 위치), Creator(reader/backend 수명), Controller(실행 조정), Low Coupling(DTO/Canvas overlay), High Cohesion(장치·커널·버퍼), Polymorphism(평활화 구현), Pure Fabrication(완료 정책/도구조정), Indirection(조립점), Protected Variations(네이티브 격리) 확인. 원칙 예외 없음.
