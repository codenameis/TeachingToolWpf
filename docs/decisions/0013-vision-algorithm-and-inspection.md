# ADR-013: VisionAlgorithm의 고전적 2D 형상 매칭과 앱 연결

날짜: 2026-10-04. 상태: **구현된 1차 결합 경로 승인**. 진행/미검증 상태는 [작업 목록](../../tasks/todo.md), 상세 경계·논문 대응·제약은 [구현 계획](../../tasks/pattern-matching-plan.md)과 [검증 보고](../shape-matching-validation.md)에 둔다.

## 결정

PoseMatch-TDCM이 딥러닝 기반이라는 확인 뒤 사용자는 딥러닝을 제외하고 전통적 패턴 매칭으로 방향을 확정했다. Python, 학습 가중치, PyTorch/TorchSharp/ONNX Runtime과 외부 native matcher wrapper를 넣지 않는다. CPU에서 실행되는 새 C# matcher를 `src/VisionAlgorithm`에 두고 TeachingToolWpf가 직접 참조한다. Cognex/Halcon 폴더는 공급자 SDK가 없는 경계 문서만 갖는다.

주력 경로는 8방향 응답 맵의 피라미드 후보 검색 → 원본 해상도 연속 gradient 검증 → robust point-to-line 자세 정밀화 → 최종 점수 재검증과 IoU NMS다. 이는 P1/P2에서 선택한 원리의 제품 결합 구현이며 특정 단일 논문의 원본 파이프라인 복제 또는 상용 도구와 동등 구현이 아니다. 자세 범위는 학습 ROI 기준 편차이고, 반환 pose와 overlay는 이미지 기준 절대각이다. 희소 학습 마스크도 model 경계에 보존해 NMS/표시가 edge 샘플만으로 축소되지 않게 한다.

앱은 `Imaging/Matching`에서 기존 원본 타일/프레임을 Gray8 계약으로 읽고 ROI의 실제 기하 마스크와 중심·방향을 전달한다. 비동기 `ShapeMatchingViewModel`은 세션 epoch와 취소를 검사한다. 결과는 읽기 전용 `MatchMarker`로 DX12 장면에 전달한다. 검사 결과는 ROI, recipe 또는 편집 이력의 일부가 아니다. 미래 VisionCanvas에는 검사 UI/판정/모델을 옮기지 않는다.

## 검토한 대안

| 대안 | 결정 |
|---|---|
| PoseMatch-TDCM 모델을 Python/ONNX로 구동 | 최종 딥러닝 제외 조건과 충돌해 제외 |
| 단일 NCC 패치 매칭을 주력 shape matcher로 사용 | 요청한 방향 기반 후보·가림 점수·point-to-line 정밀화를 제공하지 않아 제외 |
| HALCON/Cognex/OpenCV matcher native wrapper | 독립 CPU C# 엔진 조건과 충돌해 제외 |
| MainWindow 또는 ROI 객체에서 검색·모델 상태 처리 | UI/도메인 책임과 renderer/source 수명을 묶어 최소 typed 앱 어댑터와 ViewModel로 분리 |
| 결과를 ROI 문서에 기록하거나 별도 검사 Undo stack 생성 | 현재 작업은 읽기 전용 matcher integration이며 기존 편집 이력에 영향 주지 않도록 제외 |
| BOLD/E1/E2 구현을 주력 앱 경로에 함께 실행 | 비교 경로는 별도 측정·선택만 허용. 동일 frame에서 모든 알고리즘을 무조건 돌리지 않음 |

E1 directional chamfer, E2 gray-gradient fusion, E3 BOLD식 선분 descriptor matcher를 `OpenSrc/Comparisons`에 독립 비교기로 둔다. 테스트/벤치마크 전용이며 패턴 검사 패널에 노출하지 않는다. 동일 합성 장면 실측에서 E1과 E3는 3개 대상 중 2개, E2는 3개를 찾았다(10px 위치 허용 오차). 이 결과는 제한된 합성 fixture 결과이며 비교기 또는 원문 알고리즘의 일반 품질을 나타내지 않는다. BOLD 입력 상한은 256 Kpx다.

## 검토 결과와 한계

- **SOLID:** SRP는 matcher/trainer, app pixel adapter, ViewModel, renderer marker로 분리했다. OCP는 실제 변형인 OpenSrc 및 미래 SDK 폴더 경계를 유지하고 불필요한 plugin API를 만들지 않았다. LSP는 다른 algorithm provider 구현이 없어 현재 검증 불가이며 취소/입력 계약 시험은 계속한다. ISP는 아직 사용하지 않는 generic provider 계약을 만들지 않았다. DIP는 순수 `VisionAlgorithm`과 앱 조립에서 구현을 연결했다.
- 비교 경로에서도 BOLD 등록은 `BoldTrainer`, 이웃 descriptor는 `BoldDescriptor`, scene 대응/pose voting은 `BoldMatcher`와 `BoldPoseEstimator`, 공통 geometry·입력 검증은 작은 내부 책임으로 나눴다. 앱의 기본 UI는 주력 matcher만 호출한다.
- **GRASP:** 좌표·마스크는 feature/model과 app adapter의 책임(Information Expert), trainer가 불변 model을 만든다(Creator), ViewModel이 사용자 작업을 조정한다(Controller), 이미지/알고리즘/render 경계를 typed data로 낮게 결합했다(Low Coupling). 매처·서비스·표시의 응집도를 나눴고(High Cohesion) 실제 변형만 별도 구현한다(Polymorphism). renderer source reader와 UI는 adapter로 분리(Indirection), model/data/layout 변화는 작은 값으로 격리(Protected Variations)했다. workspace/자원 cache와 공통 이력은 이번 변경에서 추가하지 않았다(Pure Fabrication 관련성은 후속 레시피/이력 요구에서 재평가).
- 수동 pixel/performance 최적화는 우선 측정하고, SOLID/GRASP를 완화하는 예외는 없으며 사용자 승인도 요청하지 않았다.
- P1/P2는 논문 기반 독립 재구현/참고 결합, P3 grouping은 정확히 같은 양자화 집합만 공유하는 제한 실험이다. E1/E2/E3도 원문 전체 파이프라인을 재현하지 않는다. `meiqua/shape_based_matching`과 OpenFDCM 코드는 포팅하지 않았다. 응답 맵/P2L 또는 비교 경로가 원문과 같거나 상용 수준이라는 비교 근거는 없다.
- 후보/검증 예산 초과는 진단 및 앱 상태로 표시한다. `MatchResult`의 coarse-to-fine 탐색은 근사이므로 전체 탐색 누락 가능성을 완전 검색으로 오해시키지 않는다.
- 실제 WPF 패널 클릭, RTX adapter 기록, 8K/24K real-image 속도/정확도는 수동/현장 검증이 남아 있다. JSON benchmark는 독립 합성 입력에 대한 CPU 비교만 설명한다.
