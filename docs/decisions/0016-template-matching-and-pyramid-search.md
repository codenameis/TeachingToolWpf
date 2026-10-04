# ADR-016 — OpenCV 템플릿 매칭과 큰 ROI 형상 검색 개선

날짜: 2026-10-04. 사용자 요청은 기본 OpenCV 템플릿 매칭 추가, 느린 기존 패턴 매칭 개선, 논문 근거 재확인이다. 재현 설명은 “ROI 하나 추가 → 학습 → 검사, ROI 크기 512 이상 추정”이다. 실제 사용자 영상은 제공되지 않아 기본 8192×8192 합성 샘플로 측정했다.

## 결정과 구현

검사 방식에 **형상 매칭**과 **OpenCV 템플릿**을 제공한다. 각 방식의 등록 모델은 세션에서 독립적으로 유지된다. 전환은 실행을 취소하고 결과를 무효화한다. 공통 worker 수명·입력 epoch·판정·GPU 값 overlay·편집 이력 계약은 유지한다.

OpenCV 경로는 고정 버전 `4.13.0.20260627`의 `Cv2.MatchTemplate(CCoeffNormed)`를 호출한다. 원본 Gray8과 도형 마스크를 사용하며 회전/배율 탐색과 서브픽셀 정밀화는 하지 않는다. 기준점은 학습 crop의 실제 ROI 중심 오프셋을 유지한다. ROI 방향은 검출 회전각이 아니라 학습 시 고정 방향으로 표시한다. 윤곽 특징점이 없으므로 해당 옵션을 비활성화한다. 비유한 정규화 값은 제외하고 명암 변화가 없는 템플릿은 학습 오류로 알린다. `Mat`은 알고리즘의 `OpenSrc/PatternMatching/Imaging` 안에만 있다. 네이티브 호출이 반환된 후 취소를 확인하고 자원을 해제한다. [OpenCV 4.13 API와 마스크 수식](https://docs.opencv.org/4.13.0/df/dfb/group__imgproc__object.html)을 따른다.

큰 형상 검색의 병목은 원본 검색 창마다 큰 모델 halo를 다시 읽고 전체 피라미드·응답맵을 반복 생성하는 구조였다. 8K/512 ROI에서 225개 창, 원본 픽셀 처리량 약 5.95배, 전처리 약 20초였다. ROI 768의 이전 창 배치는 약 38.24배의 픽셀 처리를 요구한다(후자는 배치 산술이며 이전 실행 시간 측정이 아니다).

앱은 ROI 크기에 따라 최대 6단계까지 학습한다. 큰 이미지에서는 원본 level-0 타일을 읽어 **학습과 동일한 반올림 2×2 면적 평균**을 적용하고, 가장 거친 모델로 제한된 창을 검색한다. 표시 LOD나 필터 미리보기는 사용하지 않는다. 통과 후보 주변만 원본 해상도로 다시 읽어 단계별 자세 보정·연속 검증·point-to-line 정밀화를 수행한다. 최종 사용자 점수/지지율과 정확한 검색 ROI 포함 조건은 원본 단계에서 적용한다. 거친 단계의 ROI는 보정 여유만큼 확장해 작은 검색 영역 경계의 후보를 보존한다.

각 피라미드 단계에서 위치와 함께 각도·배율을 보정한다. 최상위 각도/배율 샘플 간격은 특징 반경으로 제한하고, 템플릿 예산을 넘으면 학습 오류를 반환한다. 특징점 간격은 모델 크기에 맞춰 시작해 강한 에지 한 부분의 독점을 줄인다. SIMD 기본 사용과 scalar fallback은 응답 동등성 테스트로 확인한다. 전체 개선 수치를 SIMD 단독 효과로 해석하지 않는다.

## 검색·메모리 예산

- 형상 일반/축소 검색 영상: 최대 2,097,152 pixel. 학습 및 단일 원본 정밀화 crop: 최대 8,388,608 pixel. crop을 동시에 여러 개 전처리하지 않는다.
- OpenCV 학습: 최대 8,388,608 pixel. 검색 창: 최대 16,777,216 pixel. 영상 뷰의 backing memory 전체를 복사하지 않고 행별 유효 폭만 복사한다.
- 기본 후보 2,048, 형상 일반 검증 64, 큰 이미지의 거친 창 검증 256, 창별 원본 정밀화 seed 64, 최종 표시 16개다. 전체 seed/상관 후보도 2,048개로 제한한다. 후보·검증·내부 seed 제한은 모두 검색 불완전으로 보고해 Orange 경고가 된다. 최종 사용자가 지정한 출력 개수 제한과 내부 작업 잘림을 구별한다.
- OpenCV 창들은 템플릿 왼쪽 위 위치를 나눠 소유한다. 창별 최대점 후보를 모아 전역 IoU 억제를 한 번 적용한다. 창별 억제 후 재억제하면 유효 결과를 잃는 사례를 회귀 테스트에 넣었다.
- 이 값은 입력·후보 상한이며 프로세스 working set의 고정 상한이 아니다. 기본 크기 ROI 시험의 실제 peak working set은 약 990 MiB였다. GPU 메모리 수치와 구분한다.

## 논문 대응 재검토

| 원문 | 확인한 대응 | 차이와 이번 보정 |
|---|---|---|
| [Hinterstoisser et al., Gradient Response Maps for Real-Time Detection of Textureless Objects, TPAMI 2012](https://vincentlepetit.github.io/files/papers/comp_hinterstoisser_pami12.pdf), §2.1–2.5 | 8방향 unsigned 양자화, OR 확산, 최대 절대 cosine 응답 LUT, 위치별 선형 배열과 SIMD 누적 | RGB 최대 기울기 채널과 3×3 방향 다수결을 그대로 구현한 것이 아니다. Gray8 평활화/Sobel과 독립 특징 선택을 사용한다. 반경에 따른 샘플 간격과 특징 분산을 보완했다. |
| [Steger, Occlusion, Clutter, and Illumination Invariant Object Recognition, ISPRS 2002](https://mv.in.tum.de/_media/members/steger/publications/2002/isprs-comm-iii-steger.pdf), §2–4 | 방향 유사도·가림 시 고정 분모, 피라미드 자세 검색, 최소제곱 정밀화의 근거 | 기존 링크는 2002 논문인데 2001의 다른 제목으로 표기해 바로잡았다. 논문의 signed/global polarity 식, partial-sum early exit, 4D quadratic 보간 전체를 재현하지 않는다. 현재 보존 극성은 음수 dot을 0으로 자르고, 정밀화는 검색 에지 법선·robust 가중치를 쓰는 독립 결합이다. 각도/배율을 고정한 채 피라미드를 내리던 결함을 수정했다. |

따라서 명칭은 **논문 아이디어를 결합한 전통적 형상 매칭**이다. 단일 논문의 충실한 재현, HALCON/Cognex 동등성, 실촬영 검출률을 주장하지 않는다. coarse-to-fine, 국소 최대, 후보 예산, coarse NMS는 근사 탐색이며 미세 특징이 축소에서 사라지는 입력도 후속 평가가 필요하다. 딥러닝 의존성은 추가하지 않았다.

## SOLID·GRASP 검토

| 원칙 | 검토 근거 |
|---|---|
| SRP | 원본 읽기, 형상 검색 흐름, native 상관 계산, UI 상태, 판정/overlay 분리 |
| OCP | 독립 `ITemplateMatchingService` 경로와 방식 선택 추가, Core/Canvas 검사 분기 없음 |
| LSP | 두 서비스 모두 취소·원본 좌표·독립 reader 수명·늦은 결과 거부 계약 검증 |
| ISP | 학습/검색만 가진 typed 서비스, native 객체 공개 없음 |
| DIP | MainWindow 조립 지점에서 서비스 주입, 알고리즘은 앱/Core/Canvas 참조 없음 |
| Information Expert | 모델이 피라미드 값 뷰 제공, 알고리즘이 응답/NMS·유효 pose 검증 소유 |
| Creator | 학습기가 불변 모델을 만들고 native 경계가 Mat 생성·해제 소유 |
| Controller | ViewModel은 비동기 명령과 게시를 맡고 계산은 서비스에 위임 |
| Low Coupling | GrayImage/모델/pose 값 계약만 경계 통과 |
| High Cohesion | ShapePyramidSearch는 대형 검색, TemplateMatchingService는 상관 검색 흐름 담당 |
| Polymorphism | 앱의 typed 서비스 주입을 테스트 대역과 실제 구현이 공유 |
| Pure Fabrication | reader·피라미드 검색 서비스로 I/O와 검색 창 기술 책임 격리 |
| Indirection | 앱 adapter와 native adapter로 공급자/표시 소스 직접 결합 차단 |
| Protected Variations | OpenCV 변경은 native 경계, UI는 공급자 비의존 overlay 사용 |

승인되지 않은 원칙 예외는 없다. 독립 리뷰의 seed 잘림·이중 NMS·backing memory 복사·초기 pose 검증 지적을 수정하고 회귀 테스트로 확인했다. 검증 결과와 원시 측정은 [성능·검증 기록](../matching-performance-2026-10-04.md)을 따른다.
