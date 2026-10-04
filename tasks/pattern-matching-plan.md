# VisionAlgorithm 2D 형상 매칭 구현과 검증

작성일: 2026-10-04. 이 계획은 앞서 제안했던 NCC 중심 계획을 대체한다. 딥러닝은 제외한다. 주력 구현은 LINE-2D 계열의 방향 응답 후보 검색, 원본 해상도 연속 방향 검증, robust point-to-line 자세 정밀화의 결합이다. 이는 단일 논문 원본의 재현이나 HALCON/Cognex 호환 구현이 아니다. 현재 진행 표시는 [tasks/todo.md](todo.md), 설계 결정은 [ADR-013](../docs/decisions/0013-vision-algorithm-and-inspection.md)이다.

## 프로젝트 경계와 사용 흐름

2026-10-04 검사 표시 수정: 결과 수명·네 가지 판정·검출 위치·특징점 옵션은 [ADR-015](../docs/decisions/0015-inspection-result-display.md)를 따른다. 학습·검색 엔진은 유지하며 앱이 판정을 결정하고 `ResultOverlay`를 만들어 Canvas에 전달한다. 진행/검증 상태는 todo의 검사 표시 항목이 최신이다.

`src/VisionAlgorithm/VisionAlgorithm.csproj`는 .NET 10 순수 C# 라이브러리이며 TeachingToolWpf만 참조한다. WPF, DX12, Core, OpenCV 또는 상용 SDK 참조를 두지 않는다. 데이터 계약은 Gray8 `GrayImage`, ROI 래스터 마스크, 기준점과 회전, 불변 `ShapeModel`, typed pose·진단이다. 검색 worker의 임시 메모리와 모델은 구별하고 모델 자체는 검색 중 수정하지 않는다.

앱 흐름은 기존 단일 ROI 선택 → `ShapeMatchingViewModel.Train` → 원본 픽셀 어댑터 → `ShapeTrainer.Train` → 현재 이미지 또는 선택 검색 범위에서 `ShapeMatcher.Search` → 결과 epoch 검사 → 읽기 전용 `MatchMarker` → 기존 ScenePainter GPU overlay다. ROI 문서·편집 이력은 검사 결과로 변경하지 않는다. 렌더 소유 `ITileSource`는 별도 worker에서 열고 읽은 뒤 닫는다. 라이브는 마지막 프레임을 멈춘 다음 학습/검사하도록 UI를 제한한다.

알고리즘 좌표는 원본 이미지 픽셀의 `double` 중심 좌표다. ROI 마스크는 원본 crop의 pixel-center 포함으로 만든다. 학습 기준점은 ROI 중심이고, 모델 내부 특징과 경계는 학습 ROI의 방향을 0으로 하는 지역 좌표다. `TrainingOptions`의 회전 범위는 학습 ROI 기준 편차이고 `MatchPose.Angle`/화면 overlay는 이미지 기준 절대각이다. 검색 영역은 모델 기준점 허용 영역이며 원은 정확한 원 판정, 폴리곤은 경계 포함 판정을 쓴다. 화면 줌/스크롤은 매칭 계산에 들어가지 않는다.

## 주력 검색 경로

1. `ShapeTrainer`는 Gaussian 5-tap 평활화와 Sobel 기울기로 에지를 찾고, 축 방향 비최대 억제·강도 순위·거리 기반 분산 선택으로 피라미드별 최대 512 특징을 고른다. ROI 마스크 밖의 특징은 제거한다. 각 feature는 기준점 상대 위치와 단위 방향벡터를 갖는다.
2. 양자화 방향은 8-bin 180도 주기다. polarity는 후보 단계에서 무시하고, 연속 검증 단계에서 보존/반전 허용을 적용한다. 회전·균일 배율 템플릿은 위치와 방향 bin을 함께 변환한다.
3. `OrientationMaps`는 radius 2의 수평·수직 방향 확산, 0–100 cosine 응답표, step-4 위치 서브픽셀 평면 배열을 만든다. `CandidateSearch`는 최상위 피라미드의 응답을 스칼라 또는 SIMD로 누적하고 국소 최대를 추린 뒤 더 미세한 수준에서 후보를 좁힌다. 후보·템플릿 상한을 넘으면 불완전 상태를 진단한다. 피라미드·국소 최대 검색은 근사 탐색이며 무손실을 보장하지 않는다.
4. `ContinuousVerifier`는 원본 해상도의 bilinear 연속 gradient를 사용해 translation/angle/scale 주변을 단계적으로 최적화한다. 약한/누락/영상 밖 특징도 고정 분모에 남긴다. `PointToLineRefiner`는 서브픽셀 에지 대응과 robust 가중 point-to-line 최소제곱으로 자세를 조정한다. 관측 불가 각도, 조건 불량, 지지 부족, 과도한 업데이트, 잔차·점수 악화는 원 자세와 거부 진단을 보존한다.
5. `MatchSuppression`은 학습 마스크의 pixel-cell 외접 경계를 현재 절대각/배율로 변환해 IoU 중복을 제거한다. 모델 경계는 희소 edge 점만으로 축소하지 않는다.

단계 점수는 서로 다른 의미다. coarse 후보 cosine 응답, 연속 방향 score/support, 정밀화 잔차를 별도로 보고한다. threshold는 실데이터로 다시 조정해야 한다. 실행 기본 경로는 스칼라다. SIMD, 병렬 검증, tiled preprocessing, 양자화가 완전히 같은 템플릿의 그룹화는 선택형 실험이다.

## 논문·코드의 적용과 상태

| 자료 | 구현에 사용한 아이디어 | 상태와 차이 |
|---|---|---|
| Hinterstoisser et al., “Gradient Response Maps for Real-Time Detection of Textureless Objects,” TPAMI 2012, DOI 10.1109/TPAMI.2011.206 | 방향 양자화/확산, cosine 응답, 선형 메모리 배치와 피라미드의 빠른 후보 검색 | 논문 §2의 독립 재구현. 원문의 학습/특징 채택·응답 최적화·실험 재현과 동등하다고 주장하지 않는다. |
| Steger, “Similarity Measures for Occlusion, Clutter, and Illumination Invariant Object Recognition,” 2001 | gradient-direction 유사도, polarity 정책, 가림 특징의 고정 분모 | 점수의 목적을 참고한 결합 설계. 원 논문 전체 모델·정합 알고리즘 포팅은 아니다. |
| Konishi et al., “Fast and Precise Template Matching Based on Oriented Gradients,” ECCV Workshops 2012, DOI 10.1007/978-3-642-33885-4_63 | 정확히 같은 양자화 템플릿만 공유 누적하는 보수적 그룹 비교 | P3 영감의 제한 실험. P3의 회전/스케일 template grouping 재현이 아니다. |
| Tombari et al., “BOLD Features to Represent the Local Appearance of 3D Objects,” ICCV 2013 | 선분 방향 정규화, 이웃 선분의 α/β histogram, 다중 이웃 bunch와 descriptor matching·pose voting | E3 독립 비교기. 선분 검출·descriptor 설정과 voting/refit 구현이 원문 파이프라인과 다르며, 제품 기본 matcher가 아니다. |
| [`meiqua/shape_based_matching`](https://github.com/meiqua/shape_based_matching/tree/3df935f8e4b8c8a8ed72984211543e1b7696452e) | `line2Dup.h/.cpp`, `test.cpp`, `icp_refined`, `fusion_by_hand`를 구조와 수식 대조용으로 확인 | 검사한 master commit `3df935f8e4b8c8a8ed72984211543e1b7696452e`, [BSD-2-Clause license](https://github.com/meiqua/shape_based_matching/blob/3df935f8e4b8c8a8ed72984211543e1b7696452e/LICENSE). 소스 코드를 포팅/복사하지 않았다. HALCON의 공식 소스로 취급하지 않는다. |
| OpenCV `opencv_contrib/modules/rgbd/src/linemod.cpp` | LINE-MOD 구현 의도 교차 확인 | 검색 엔진은 2D 입력 전용이며 RGB-D/DX 구현을 쓰지 않는다. 코드를 복사/포팅하지 않았다. |

추가 비교 소스인 [OpenFDCM commit `5c6c1d422afc8010edf6eeb149329f353b2e77ce`](https://github.com/Innoptech/OpenFDCM/tree/5c6c1d422afc8010edf6eeb149329f353b2e77ce)은 [MIT license](https://github.com/Innoptech/OpenFDCM/blob/5c6c1d422afc8010edf6eeb149329f353b2e77ce/LICENSE)다. 현재 `DirectionalChamferMatcher`는 독립적인 8방향 거리/선분 비교 실험이며, OpenFDCM 전체 또는 MERL 논문의 FDCM이라고 부르지 않는다. `GrayGradientFusion`은 상위 일반 NCC와 하위 방향 검증의 독립 비교 경로며 E2 논문의 Winograd FNM·가속 구현을 포함하지 않는다. `BoldMatcher`는 OpenSrc 비교 폴더에 둔 별도 CPU 경로다. 12×12 α/β histogram과 5/10/15/20 이웃 bunch descriptor, descriptor nearest-match, generalized-Hough식 pose hypothesis 및 similarity least-squares refit을 조합한다. 원문의 detector·전체 descriptor 학습·투표 구현을 재현하지 않으며 현장 적합성 전 제품 경로로 선택하지 않는다. 한 번의 입력은 262,144px 이하, 피라미드별 선분은 384개 이하, pose hypothesis는 50,000개 이하로 제한한다.

원문과 코드 출처는 [P1 논문 PDF](https://vincentlepetit.github.io/files/papers/comp_hinterstoisser_pami12.pdf), [Steger 논문 PDF](https://mv.in.tum.de/_media/members/steger/publications/2002/isprs-comm-iii-steger.pdf), [P3 DOI](https://doi.org/10.1007/978-3-642-33885-4_63), [BOLD 원문 PDF](https://www.cv-foundation.org/openaccess/content_iccv_2013/papers/Tombari_BOLD_Features_to_2013_ICCV_paper.pdf), [OpenCV LINE-MOD 소스](https://github.com/opencv/opencv_contrib/tree/4.x/modules/rgbd)다. 구현 소스 파일에 외부 코드를 포함하지 않았으므로 코드 포팅/라이선스 attribution은 없고, 두 GitHub 저장소는 연구 참조와 고정 버전 감사를 기록한다.

## 앱 통합 경계

- 매칭 전용 어댑터는 앱의 `Imaging/Matching`에 두고 `ITileSource`/프레임의 원본 픽셀만 Gray8로 변환한다. stride·타일 경계·소스 수명은 앱 경계에서 해결한다. Gray16은 고정 상위 8bit, RGB는 고정 정수 luminance로 변환하며 이미지별 자동 명암 보정은 하지 않는다.
- 8K/24K 전체 이미지를 worker 배열 하나로 펼치지 않는다. reader는 최대 2,097,152 pixel인 검색 window와 모델/피라미드 halo를 읽는다. 전체 요청은 tile별 처리라면 시간이 크게 늘 수 있고, 테스트/실측 전 실제 24K 완료 성능을 주장하지 않는다.
- `ShapeMatchingViewModel`은 비동기 학습/검색/취소, 입력 epoch 확인, stale 결과 폐기를 담당한다. `ShapeMatchingPanel`은 학습/검색/결과의 최소 UI를 오른쪽 Inspector에 붙인다. ROI 속성/필터 기능은 기존 패널과 연결된 상태로 둔다.
- `RenderScene`과 `ScenePainter`는 매처 타입 없이 `MatchMarker` 경계/기준점/라벨만 표시한다. `VisionCanvas`를 현재 물리 프로젝트로 만들지는 않았으며 향후 이동 후보는 화면 표시·ROI 편집·탐색·범용 overlay다.

## 검증과 완료 상태

검증 증거의 보관 위치와 실행 상태는 [작업 목록](todo.md), [실측 JSON](../docs/data/shape-matching-benchmark-2026-10-04.json), [검증 보고](../docs/shape-matching-validation.md)다. 기존 솔루션 회귀 상태와 이 요청의 검증은 구분한다. 실제 현장 영상과 HALCON/Cognex 비교가 없으므로 일반화 정확도/동등성을 주장하지 않는다.

E1/E2/E3는 독립 비교 경로로 동일 합성 장면의 Release 측정을 추가했다. 원문 충실 구현 여부와 실제 제품 데이터의 결과는 구별하며, 측정·검출 수·자원 제한은 [검증 보고](../docs/shape-matching-validation.md)에 기록한다. 미완료로 남기는 항목은 Windows WPF 패널 수동 조작, RTX/GPU 정보, 실촬영/24K 원본 검색 성능과 정답 데이터, E1의 논문 충실 버전, E2 원문의 FNM/가속, E3 원문의 detector/feature/voting 전체 재현이다. `NOT_RUN`/`BLOCKED` 항목은 테스트를 생략해 통과 처리하지 않는다.
