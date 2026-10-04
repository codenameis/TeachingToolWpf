# OpenSrc / PatternMatching

현재 OpenSrc 첫 matcher다. 이는 LINE-2D 계열 방향 응답 원리를 논문에서 독립 재구현한 결합 경로이며 원 코드 포팅이나 단일 논문의 전체 재현이 아니다.

앱에서는 OpenCV/논문 기반/자체 기하 세 방식으로 분리한다. 논문 모드는 `BuildGeometricModel=false`, `EvaluateGeometry=false`로 아래 1~4/6 단계를 사용하고 자체 기하 모드만 5단계를 추가한다. 형상 후보 검색 구현은 공유한다. 각 방식의 모델·설정은 앱에서 독립 보관한다. [ADR-020](../../../../docs/decisions/0020-matching-modes-and-result-limits.md)

1. `GradientImage`의 5-tap 평활화/Sobel gradient, 축 방향 비최대 억제, 서브픽셀 edge, 피라미드 준비.
2. `ShapeTrainer`의 ROI mask 기반 방향 특징 선택과 회전·균일 배율 템플릿 생성.
3. `OrientationMaps`의 180° unsigned 8-bin 방향 확산/응답 평면, `CandidateSearch`의 coarse-to-fine 제한 후보 검색.
4. `ContinuousVerifier`의 원본 해상도 연속 방향 score/support와 `PointToLineRefiner`의 robust 자세 보정.
5. `GeometricQualityEvaluator`의 최종 원본 pose에서 선택 특징 coverage·대응 거리 RMS·전체 학습 윤곽 기반 clutter 측정 및 선택형 합격 기준.
6. `MatchSuppression`의 마스크 외접 경계 IoU 억제.

`GeometricQualityModel`은 전체 NMS 학습 윤곽(최대 262,144개)과 정확한 마스크를 불변 복사해 보관한다. 희소 특징을 dense 윤곽 대용으로 사용하지 않는다. `SearchOptions`의 `MinimumCoverage`, `MaximumFitErrorPixels`, `MaximumClutterRatio`는 기본 해제이고 `GeometricTolerancePixels`는 원본 픽셀 거리다. `EvaluateGeometry=false`/`Verify=false`는 품질 계산을 생략하며 결과 `Geometry`는 null이다. 최종 원본 검증에서만 품질 기준을 사용한다. [ADR-019](../../../../docs/decisions/0019-geometric-match-quality.md)의 자체 수식이며 PatMax 점수와 호환되지 않는다.

`ClutterWeight`가0보다 크면 `BuildClutterModel` 또는 `BuildGeometricModel`로 저장한 전체 윤곽/마스크가 필요하다. 최종 `Score = DirectionScore × (1 − ClutterWeight × ClutterRatio)`이며 라이브러리 기본비중0/앱기본1이다. Paper의 추가 윤곽 점수와 선택적인 기하 gate를 구분한다. 축소 후보의 비중은0이며 원본 pose에서 최종 계산한다. [ADR-022](../../../../docs/decisions/0022-inspection-scoring-execution-and-primitives.md).

검색 예산이 넘으면 `SearchDiagnostics`에 후보/검증/출력 잘림을 보낸다. 검증 예산 부족 시 근접 pose가 예산을 독점하지 않도록 다른 위치·회전·배율 후보를 먼저 방문하고, 남는 예산을 원래 점수 순서로 채운다. 최종 NMS·점수 기준은 유지하며 근사 검색의 완전성을 보장하지 않는다. 알고리즘 입력은 Gray8 원본 좌표이며 WPF/GPU/ROI Core는 여기서 참조하지 않는다. SIMD 기본값은 true이며 지원되지 않으면 scalar로 계산한다. 그룹·병렬·tiled 경로는 선택 값이다. GPU 모드에서는 호스트가 중립 `IImagePreprocessor`를 주입해야 하며 미주입/실패를 CPU 성공으로 대체하지 않는다. CUDA의 실제 평활화·Sobel 구현은 별도 VisionCompute.ILGPU에 있다.

2026-10-04 후속: 특징 반경에 따른 회전·배율 샘플 간격, 크기별 특징 분산, 단계별 위치/각도/배율 보정을 적용했다. 앱은 큰 원본의 검사 피라미드에서 후보를 찾고 `RefineCandidate`로 원본 crop을 최종 검증한다. `InspectionPyramid`의 2×2 면적 평균과 `ShapeModel.AtPyramidLevel`은 표시 LOD와 독립적이다. `CorrelationTemplate/CorrelationMatcher`는 OpenCV CCOEFF_NORMED 기반 고정 방향/크기 위치 검색이며 native Mat은 `Imaging`에 격리한다. 논문 원문과 실제 구현의 차이는 [ADR-016](../../../../docs/decisions/0016-template-matching-and-pyramid-search.md), 실측은 [8K 검사 성능](../../../../docs/matching-performance-2026-10-04.md)을 따른다.

비교 코드는 [`../Comparisons`](../Comparisons) 아래 별도 경로다. E1/FDCM 착안, E2/FNM 착안, E3/BOLD 비교기가 있으나 어느 것도 원 논문 전체 재현은 아니다. 앱 주력 경로에 섞지 않으며 입력 제한·동일 장면 측정과 미검증 범위는 [비교 경로 안내](../Comparisons/README.md), [논문 대응/제약](../../../../tasks/pattern-matching-plan.md), [검증 기록](../../../../docs/shape-matching-validation.md)을 따른다.
