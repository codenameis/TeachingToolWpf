# OpenSrc / Comparisons

이 폴더는 주력 matcher를 대체하지 않는 CPU 비교 경로를 보관한다. 앱의 패턴 티칭 UI와 기본 검사 실행에는 연결하지 않는다. 모든 경로는 별도 모델/검색 호출로 실행하며 같은 결과 계약을 가진다고 가정하지 않는다.

| 경로 | 구현 범위 | 제한 |
|---|---|---|
| `DirectionalChamferMatcher` | 8방향 distance map, 선분 누적 비용, 제한된 이동 하한. OpenFDCM/FDCM 원문 전체 구현이 아니다. | 검색 입력 최대 256 Kpx. 원문 60방향/RANSAC 동작과 다르다. |
| `GrayGradientFusion` | 피라미드 상단 일반 masked NCC 후보, 하위 연속 방향 검증/정밀화. | Winograd FNM 및 논문의 가속 절차를 구현하지 않았다. |
| `BoldMatcher` | 선분 descriptor와 multi-instance pose voting/refit. [BOLD 원문](https://www.cv-foundation.org/openaccess/content/iccv_2013/papers/Tombari_BOLD_Features_to_2013_ICCV_paper.pdf)의 선분 방향 표현과 α/β 이웃 관계에서 착안했다. | 256 Kpx 이하, 피라미드 단계별 scene line 최대 384개, hypothesis 최대 50,000개. 원문 detector/매칭 파이프라인 전체 재현이 아니다. |

`BoldTrainer`는 기본 gradient 임계값 8 Gray8 단위로 학습 ROI 마스크를 완전히 지나는 선분만 등록한다. 각 선분에는 12×12 순환 histogram을 붙이고 이웃 bunch 크기 5, 10, 15, 20의 histogram을 합쳐 L2 정규화한다. `BoldMatcher`는 descriptor 거리/ratio를 통과한 대응으로 pose를 투표하고, 위치·방향·길이 일치 선분으로 similarity pose를 재적합한다. 중복 결과는 model mask의 외접 사각형 IoU로 억제한다. 결과 pose는 학습 ROI 기준 좌표계에서 이미지 좌표로 환산된 이미지 기준 자세다.

검색의 기본 대응 허용치와 위치/방향/길이 오차는 합성 fixture에서 초기화한 값이다. 실제 제품/배경/노출 데이터에서 precision-recall, pose 오차, 파라미터 민감도와 추론 시간을 다시 보정해야 한다. 학습 ROI가 마스크 없이 전달되면 학습 이미지 전체 사각형을 중복 억제 경계로 쓴다.

이 비교기는 point-to-line 연속 정밀화나 앱 레시피를 사용하지 않는다. 현재 구현의 타깃 판정 품질, 회전/배율 범위, clutter·가림·조명 변화는 실제 라벨 데이터로 검증해야 한다. `BoldSearchDiagnostics`에는 feature 수, 대응 수, 가설 수, 최대 consensus와 잘림 여부가 포함된다. 입력/연산 예산을 넘으면 크기를 제한하거나 여러 영역으로 나누고, 잘림 상태를 확인한다.

동일 합성 입력 E1/E2/E3 측정은 [검증 보고](../../../../docs/shape-matching-validation.md)와 [원시 JSON](../../../../docs/data/shape-matching-benchmark-2026-10-04.json)에 있다. 이 결과를 원 논문, HALCON, Cognex 또는 현장 품질과 동등하다고 해석하지 않는다.
