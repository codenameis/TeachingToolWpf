# ADR-070 — 티칭 기반 정직한 OCV(`TaughtFontOcvVerifier`) 분리 및 필드 마스크 후보 제약

- 상태: 승인·적용 (Accepted)
- 날짜: 2026-11-11

## 배경

1. 현장 인쇄 품질 검사의 핵심 용도 중 하나는 범용 미등록 문서 판독(OCR)이 아니라, **사용자가 티칭한 골든 폰트와 기대 문자열·필드 형식에 비추어 실제 인쇄물이 누락·번짐·오인쇄·저대비 없이 인쇄되었는지 검증하는 OCV(Optical Character Verification)**다.
2. 기존 `IndustrialOcrOcv` 네임스페이스에는 티칭 모델(`IndustrialOcrFontModel`, `IndustrialOcrTrainer`)과 다중 폰트 범용 휴리스틱(`UniversalMultiFontOcrEngine`)이 섞여 있었고, 필드 마스크를 오인식 결과를 기대 문자열로 덮어쓰는 사후 치환처럼 오용할 위험이 있었다.
3. 또한 [ADR-057](0057-inspection-sidebar-workflow.md) 원칙에 따라 인쇄 대비나 일치 점수가 경계 구간에 놓인 **검토 필요(`Review`/Warning)** 상태와 실제 불량(**`Ng`**)을 명확히 구분해야 한다.

## 결정

1. **호출 경로 및 공개 타입 분리 (파일당 공개 타입 1개 준수)**
   - 범용 다중 폰트 인식기(`UniversalMultiFontOcrEngine`)에 새로운 휴리스틱을 추가하지 않고, 부수 공개 타입을 `RecognizedCharBox.cs`, `UniversalOcrRegionResult.cs`로 분리했다.
   - 티칭 기반 OCV 전용 계약을 별도 단일 타입 파일 6개로 분리해 구성했다:
     - `OcvVerdictKind` (`Ok`, `Review`, `Ng`)
     - `OcrFieldFormatSpec` (기대 문자열 `ExpectedText`, 문자 클래스 마스크 `FormatMask`: `'D'` 숫자, `'A'` 대문자, `'a'` 소문자, `'X'` 영숫자, `'#'` 임의 인쇄 문자, 그 외 리터럴 기호, 선택 정규식 `ValidationRegex`)
     - `OcvVerificationOptions` (`named const` 기본값을 가진 불변 옵션 `record`)
     - `OcvCharacterVerification` (문자별 관측 문자·기대 문자·일치 점수·대비 GV·누락 획 비율·번짐 비율·판정·원인)
     - `OcvVerificationResult` (줄 전체 판정 `Ok`/`Review`/`Ng`, 문자별 상세 결과, 요약 원인)
     - `TaughtFontOcvVerifier` (사용자 주입 `IndustrialOcrFontModel` 기반 OCV 실행기 및 현장 샘플 줄 티칭 추출 보조 메서드 `ExtractTeachingSamplesFromLine`)

2. **필드 형식(`OcrFieldFormatSpec`)의 후보 제약 전용 사용 (정답 강제 치환 금지)**
   - `FormatMask`는 해당 슬롯에서 비교할 등록 글리프 후보 집합을 좁히는 **후보 제약(Candidate Constraint)**으로만 사용한다.
   - 관측 패치가 허용 후보 글리프와 일치하지 않거나(`MatchScore < MinPassScore`), 후보 내 최상 글리프가 `ExpectedText`와 다를 경우 결과를 기대 문자로 고쳐 주지 않고 즉시 `OcvVerdictKind.Ng`와 구체적 불일치 원인을 반환한다.

3. **인쇄 결함 지표 및 ADR-057 `Ok` / `Review` / `Ng` 3단계 판정**
   - 문자별 ZNCC+IoU 결합 일치 점수(`MatchScore`), 전경-배경 대비(`ContrastGv`), 골든 템플릿 대비 획 누락 비율(`MissingStrokeRatio`), 배경 영역 침범/브릿지 번짐 비율(`InkBleedRatio`)을 각각 산출한다.
   - 문자 불일치, 개수 불일치, 정규식 불일치, 임계 미달 점수(`< 0.58`), 심각한 저대비(`< 22.0 GV`), 과도한 획 누락(`> 0.28`) 또는 번짐(`> 0.30`)은 모두 제품 불량(`Ng`)으로 판정한다.
   - 모든 문자가 기대 문자열·형식과 일치하고 치명적 결함 임계는 넘지 않았으나 경계 점수(`[0.58, 0.72)`), 경계 대비(`[22.0, 34.0) GV`), 경미한 획 약화/번짐 구간에 걸친 경우에만 `OcvVerdictKind.Review`로 분리해 작업자 재검토를 유도한다.

## 검증 결과 (Stage 2 `holdout` 실측 — 실제 불일치와 합성 결함 분리 표기)

`TaughtFontOcvHoldoutTests`에서 `holdout` 이미지의 픽셀을 일절 티칭에 사용하지 않고(`Consolas`/`Arial` 등록 글리프 + `tuning` 단일 줄 샘플만 티칭), `holdout` 단일 줄 영숫자 ROI `N = 9`개에 대해 검증한 결과는 다음과 같다.

| 검증 시나리오 | 결함 데이터 성격 | 대상 수 (`N`) | 측정 결과 | 비고 |
| --- | --- | ---: | --- | --- |
| 정상 인쇄 검증 (`ExpectedText = GT`) | 실제 `holdout` 원본 | 9 | `OK` **1/9 (11.1%)**, `Review` **2/9 (22.2%)**, `NG` **6/9 (66.7%)** | 동일 문자열 일치(`OK + Review`) **3/9 (33.3%)** (`OCR3-L1`, `OCR2-RightTitle`, `OCR4_CityStateZip`). 나머지 6개는 해당 부품 전용 폰트(도트매트릭스 `OCR7_DotMatrixExp`, OCR-A 특수기호 `OCR2-RightTotal` 등) 미티칭 상태에서 정직하게 `NG` 처리됨 |
| 인쇄 내용 불일치 (`ExpectedText` 끝 글자 변경) | **실제 영상 + 기대값 불일치** | 9 | **`NG` 검출 9/9 (100.0%)** | 기대 문자열로 자동 교정 0건 |
| 필드 형식 마스크 충돌 (`FormatMask` 클래스 반전) | **실제 영상 + 마스크 불일치** | 9 | **`NG` 검출 9/9 (100.0%)** | 마스크 위반 시 후보 기각 및 `NG` 판정 확인 |
| 획 누락 결함 (전경 밴드 소거) | **합성 결함** (실제 결함 샘플 부재) | 9 | **`NG` 검출 9/9 (100.0%)** | 문자 개수 누락 / `MissingStrokeRatio` 검출 |
| 잉크 번짐/브릿지 결함 (전경 과다 확장) | **합성 결함** (실제 결함 샘플 부재) | 9 | **`NG` 검출 9/9 (100.0%)** | 문자 병합 / `InkBleedRatio` 검출 |
| 심각한 저대비 결함 (`Peak-to-Peak 8 GV`) | **합성 결함** (실제 결함 샘플 부재) | 9 | **`NG` 검출 9/9 (100.0%)** | 전경 미검출 / `ContrastGv < 22.0` 검출 |
| 경계 대비 경고 검증 (`ContrastGv ≈ 28 GV`) | **합성 경계 조건** (`OCR3-L1` 기준) | 1 | **`Review` 판정 1/1 (100.0%)** | `IsNg == false && NeedsReview == true` (ADR-057 준수) |

## 3단계 재작업 보강 (2026-11-11)

1. **인쇄 그룹(`Print Group`) 분리 및 합성 폰트(`Consolas`/`Arial`) 제거**
   - 서로 다른 인쇄 공정·폰트를 한 골든 모델에 섞어 평가하던 문제를 수정해, `labels.json` (`schemaVersion: 2`)에 24개 `printGroups`와 그룹별 지정 근거를 추가하고 같은 그룹 내부에서만 비중첩 `teach` → `verify` 평가를 수행한다.
2. **편집거리(DP) 시퀀스 정렬 및 `MaxLineCharacters` 초과 명시 보고**
   - `TaughtFontOcvVerifier.VerifyLine`은 단순 인덱스 대조 대신 동적 계획법(DP) 편집거리 정렬을 수행해 중간 글자 1개 누락(`삭제(누락)`)이나 과분할(`삽입(과분할)`)이 뒤따르는 정상 글자들의 연쇄 `Ng`로 번지지 않게 격리한다.
   - 분할된 문자 박스가 `OcvVerificationOptions.MaxLineCharacters`를 초과할 때 조용히 자르지 않고 즉시 `OcvVerdictKind.Ng`와 `"최대 허용 문자 수 초과"` 원인을 반환한다.
3. **옵션 불변식 및 필드 마스크 이스케이프/문자 클래스 문법**
   - `OcvVerificationOptions` 생성 시 `PassMatchScore >= ReviewMatchScore`, `ReviewContrastGv >= MinContrastGv`, 비율 `[0, 1]`, `MinCharWidth <= MaxCharWidth`, `MaxLineCharacters in [1, 256]` 불변식을 강제한다.
   - `OcrFieldFormatSpec`에 백슬래시 이스케이프(`\D`, `\A`, `\a`, `\X`, `\#`, `\\`, `\[`)와 문자 클래스(`[0-9A-F]`, `[AB12]`)를 추가해 와일드카드 기호와 리터럴 문자의 충돌을 해결했다.
4. **재측정 요약 (`docs/ocr-evaluation.md` 3절 상세 수록)**
   - **실사 변형(`Perturbed Real`, `N = 80`)**: 정상 `OK` **75/80 (93.8% >= 90.0%)**, 정상 `OK` 분모 기준 7종 결함 검출률 **전 항목 100.0% >= 95.0%** 통과(`OCR7_LowContrast` 4/4 `OK` 포함).
   - **실사 독립(`Real Independent`, `N = 7`)**: 정상 `OK` 분모 기준 7종 결함 검출률은 **4/4 (100.0%)**이나, 정상 `OK`율은 **4/7 (57.1%)**로 목표 상수(`0.90`) 미달이며 단정을 약화하지 않고 실패 상태 그대로 보고한다.

## 한계 및 주의사항

- `TaughtFontOcvVerifier`는 **동일 인쇄 공정·폰트의 정상 샘플을 먼저 티칭(`IndustrialOcrTrainer.Train`)한 뒤 검증**하는 OCV 전용 도구다. 티칭하지 않은 임의 폰트에 그대로 적용하면 `NG`가 발생하는 것이 정상 동작이며, 미등록 다중 폰트 판독 용도로 주장하지 않는다.
- 현재 저장소에 실제 생산 라인의 물리적 인쇄 불량 촬영본이 없으므로, 결함 검출 수치는 정상 `OK` 샘플 위에 실제 글리프 패치 치환(`Homoglyph`)/인접 교환/획 소거/번짐/저대비를 주입한 실측치로만 기록하며 현장 불량 검출률 달성으로 과장하지 않는다.
