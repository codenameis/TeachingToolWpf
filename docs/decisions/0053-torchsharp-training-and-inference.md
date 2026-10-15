# ADR-053: PoseMatch의 C# 학습·추론과 ONNX 비교

- 날짜: 2026-10-15
- 상태: 사용자 승인 범위의 로컬 구현. 현장 정확도·지연 목표는 미달.

사용자가 TorchSharp로 C# 학습·추론을 구현하고, PoseMatch 전체 탐색을 보존하면서 ONNX와 Kensa/8K CPU·CUDA를 비교하도록 요청했다. 속도보다 누락과 위치·각도 정확도를 우선한다. 커밋/푸시는 하지 않는다.

별도 `PoseMatch.Tdcm.TorchSharp`가 공식 모델의 ConvNeXtV2 두 분기, 동적 depthwise 상관, 두 PixelShuffle 단계와 다섯 head를 C#으로 정의한다. TorchSharp0.107.0/LibTorch2.10.0 CUDA12.8을 고정한다. 공유 픽셀 정규화·decoder·후처리와 `ITdcmMatcher`는 기존 PoseMatch 프로젝트에 남긴다. VisionInspection은 구체 TorchSharp를 참조하지 않고 App이 공급자 생성 함수를 주입한다. ONNX 비교 경로와 기존 전체 창 탐색은 유지한다.

공식 `.pth`는 최초 한 번 데이터 전용 `.tdcm`으로 변환한다. 학습·추론 시 Python 프로세스나 ONNX 실행은 사용하지 않는다. C# 학습은 모든 parameter의 gradient를 계산하되 작은 배치의 BN running 통계는 고정한다. 중심/배경의 불균형을 고려해 두 집합의 BCE를 각각 정규화하고, pose loss는 정답 중심 근처만 감독한다. 문턱0.5를 낮춰 검출 수를 맞추지 않는다. 이 미세학습 절차는 공식 논문의 학습 전체를 그대로 재현했다는 주장이 아니다.

기존 UI의 ROI 학습은 계속 템플릿 등록이다. 신경망 가중치 학습은 별도 C# CLI에서 명시적으로 실행하고, 결과 파일을 앱의 TorchSharp 가중치 경로에 선택한다. 학습과 검증 SourceId/영상 해시를 분리하고, 정확도 실패도 저장한다. 검증 실패 가중치를 기본 모델로 자동 교체하지 않는다. 체크포인트는 모델 가중치만 저장하며 optimizer 상태를 포함한 완전한 학습 재개는 아니다.

LibTorch의 스레드 수/TF32 정책은 프로세스 범위다. 앱의 검사 세션은 기존 gate로 직렬화하며 학습 CLI는 별도 프로세스로 실행한다. CPU/CUDA는 FP32, TF32 OFF로 대조한다. 취소는 네이티브 호출 전후·학습 갱신 전에 관찰하며 진행 중인 LibTorch 커널을 강제로 파괴하지 않는다. 이미지·검색 결과를 클릭 전에 준비하거나 정답을 검색에 입력하지 않는다.

공식 가중치의 실행 엔진 변경으로는 Kensa28/48,8K110/256의 누락과 pose 오류가 해결되지 않았다. [측정·사용법·남은 범위](../posematch-tdcm-torchsharp.md)를 따른다. 현장 정밀 라벨 없이 근사 정답으로 모델을 재학습해 합격을 선언하지 않는다. 설계 원칙 예외 없음.
