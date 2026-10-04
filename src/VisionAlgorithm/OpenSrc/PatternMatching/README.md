# OpenSrc / PatternMatching

현재 OpenSrc 첫 matcher다. 이는 LINE-2D 계열 방향 응답 원리를 논문에서 독립 재구현한 결합 경로이며 원 코드 포팅이나 단일 논문의 전체 재현이 아니다.

1. `GradientImage`의 5-tap 평활화/Sobel gradient, 축 방향 비최대 억제, 서브픽셀 edge, 피라미드 준비.
2. `ShapeTrainer`의 ROI mask 기반 방향 특징 선택과 회전·균일 배율 템플릿 생성.
3. `OrientationMaps`의 180° unsigned 8-bin 방향 확산/응답 평면, `CandidateSearch`의 coarse-to-fine 제한 후보 검색.
4. `ContinuousVerifier`의 원본 해상도 연속 방향 score/support와 `PointToLineRefiner`의 robust 자세 보정.
5. `MatchSuppression`의 마스크 외접 경계 IoU 억제.

검색 예산이 넘으면 `SearchDiagnostics`에 후보/검증 잘림을 보낸다. 후보 단계는 근사 검색이다. 알고리즘 입력은 Gray8 원본 좌표이며 WPF/GPU/ROI Core는 여기서 참조하지 않는다. scalar 구현이 기본이고 SIMD·그룹·병렬·tiled 경로는 별도 선택 값이다.

비교 코드는 [`../Comparisons`](../Comparisons) 아래 별도 경로다. E1/FDCM 착안, E2/FNM 착안, E3/BOLD 비교기가 있으나 어느 것도 원 논문 전체 재현은 아니다. 앱 주력 경로에 섞지 않으며 입력 제한·동일 장면 측정과 미검증 범위는 [비교 경로 안내](../Comparisons/README.md), [논문 대응/제약](../../../../tasks/pattern-matching-plan.md), [검증 기록](../../../../docs/shape-matching-validation.md)을 따른다.
