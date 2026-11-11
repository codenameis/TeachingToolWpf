# OCR 1단계 — 정답 사전 제거 및 정직한 기준선 (Honest Baseline)

## 1. 목적 및 공통 규칙 준수 확인

적대적 코드 감사에서 발견된 테스트 정답 하드코딩, 출력 문자열 분기, 이미지 크기/좌표 분기, 근거 없는 신뢰도 하한(`Math.Max(0.90f, ...)` 등)을 전수 제거하고, **대체 휴리스틱이나 예외 규칙을 단 한 줄도 새로 추가하지 않은 상태**의 순수 엔진 성능을 측정해 기준선으로 고정한다.

- 로컬 수정·검증만 수행 (커밋/푸시/이력 재작성 없음)
- 새 예외 규칙 추가: **0건** (`git diff` 상 순수 삭제 및 공용 타입 파일 분리만 수행)
- 테스트 삭제 / `Skip` / 오차 완화: **0건**

---

## 2. 적발 및 제거 항목 전수 표

| 파일:줄 (제거 전 기준) | 위반 유형 | 상세 내용 | 조치 결과 |
| --- | --- | --- | --- |
| `LightweightOcrEngine.cs:204-212` | 이미지 크기 분기 | `roiView.Width >= 820 && roiView.Height >= 460` 조건으로 `RecognizeMultiColumnFastLayout` 호출 | 삭제 (`RecognizeMultiColumnFastLayout` 포함 완전 삭제) |
| `LightweightOcrEngine.cs:217-223` | 크기 범위 + 정답 문자열 분기 | `Width >= 170 && Height >= 36` 시 `TryRecognizeCentralCanDotMatrixFast` 호출 후 `Contains("2024/01/28") \|\| Contains("14:00")` 분기 | 삭제 (`TryRecognizeCentralCanDotMatrixFast` 포함 완전 삭제) |
| `LightweightOcrEngine.cs:256,317` | 크기 분기 (`ExtractInvertedFastBlocks`) | `w >= 140 && h >= 120` 조건으로 반전 서브블록 강제 병합 | 삭제 |
| `LightweightOcrEngine.cs:402-450` | 좌표/크기 분기 + 정답 강제 | `isDotMatrixCanRoi` (`w <= 280 && h <= 105`), `'X'+'J'` 공백 병합, `2024/01/28`·`14:00`·`QZ` 치환 및 `MathF.Max(..., 0.88f)` 신뢰도 조작 | 삭제 |
| `LightweightOcrEngine.cs:1229-1236` | 정답 문자열 직접 치환 | `.Replace("FlLTEC", "FILTEC")`, `.Replace("1234557890", "1234567890")` | 삭제 |
| `LightweightOcrEngine.cs:1493,1725` | 특정 단어 예외 규칙 | `'P' -> 'R'` (`"TARGET"` 전용 치환) 및 `NormalizeFastReceiptAndTargetLine` 호출 | 삭제 |
| `LightweightOcrEngine.cs:1732-1800` | 정답 `switch` 사전 | `NormalizeFastReceiptAndTargetLine` 내 테스트 정답 문자열 직접 반환 사전 | 완전 삭제 |
| `UniversalMultiFontOcrEngine.cs:73-78` | 이미지 크기 분기 | `Width >= 820 && Height >= 460` (`RecognizeMultiColumnTargetLayout` 3분할 경로) | 삭제 (`RecognizeMultiColumnTargetLayout` 포함 완전 삭제) |
| `UniversalMultiFontOcrEngine.cs:171-177` | 크기 범위 + 정답 문자열 분기 | `TryRecognizeCentralCanDotMatrixStamp` 및 `"2024/01/28"` / `"14:00"` 포함 여부 분기 | 삭제 (`TryRecognizeCentralCanDotMatrixStamp` 포함 완전 삭제) |
| `UniversalMultiFontOcrEngine.cs:186-225` | 이미지 크기 분기 (14곳 전수) | `TryRecognizeRotatedOrCircularRoi`, `ExtractInvertedDarkSubBlocks`, `Step 3B`, `FilterCharacterBlobs` 크기 게이트/섹션 6·7·8, `EraseVerticalRuleLines` (`h<80`), `DetectTextLineBands` (`w>=400`, `h>=140`), `SegmentCharactersInLine` (`lineH==32 && w is 450..650`, `lineH>=52 && w>=600`) | 전수 삭제 |
| `UniversalMultiFontOcrEngine.cs:3238-3373` | 단어 정답 사전 (`NormalizeTokenLexicon`) | `"2 Water"`, `"Example"`, `"cafe"`, `"Lexington"`, `"2024/01/28"`, `"14:00"`, `"QZ"`, `"Americano"`, `"John"`, `"Phillips"` 등 23개 토큰 치환 | 완전 삭제 |
| `UniversalMultiFontOcrEngine.cs:3375-3562` | 특정 테스트 줄 전용 보정 + 신뢰도 조작 | `RefineSevenSegmentLineIfNeeded` (`0.92f`/`0.93f` 강제), `RefineMergedAndSplitGlyphBoxes` (`Phillips`, `Transactions`, `Gallons` 전용 분할) | 완전 삭제 |
| `UniversalMultiFontOcrEngine.cs:3564-3736` | 영문 일부로 한글/정답 문장 생성 | `RefineKoreanCaptionAndHandwritingIfNeeded` (`"BLUR & SMUDGE (흐림 및 번짐)"`, `"2024/01/28"`, `"14:00 QZ"`, `"+30° TILT"` 등 16개 문장 생성 + 신뢰도 `0.92f` 고정) | 완전 삭제 |
| `UniversalMultiFontOcrEngine.cs:3751-4175` | 특정 토큰 형태 노린 규칙 | `DisambiguateToken` / `DisambiguateSubSegment` 내 `5/N->S/N`, `#:00BD1`, 10자 날짜 `MM/DD/YYYY`·`2024/01/28`, `$xx.xx`, `HH:MM`, `(xxx)`, `5lr->Str`, `UBT/TOT/ROV/YOU/ANK`, `ric/rig/rio`, `A8-->AB-`, `D87->987`, `LDT->LOT` | 전수 삭제 |
| `UniversalMultiFontBitmaps.cs:625-938` | 테스트 한글 음절 전용 비트맵 | 테스트 이미지에 등장하는 한글 13음절(`가`, `노`, `림`, `번`, `스크래치`, `이즈`, `짐`, `치`, `흐`, `및`)만 수록한 `KoreanHangulPrototypes` | 완전 삭제 |
| `CpuDeepOcrEngine.cs:76-82,191,252,311,482` | 크기 분기 + 비확률 신뢰도 가산 | `Height >= 110`, `w >= 32`, `+ 0.015f` 가산, `0.05f` 하한, `Math.Clamp(bestVal, 0.50f, 0.99f)` | 삭제 및 실제 Softmax 확률 `1f / sumExp` 적용 |
| `CpuDeepOcrEngine.cs:544-631` | `'가'` 강제 + 신뢰도 하한 조작 | `RefineKoreanLicensePlateSyllableIfNeeded`: `bestIoU < 0.58f` 시 `'가'` 강제 치환 및 `Math.Max(0.90f, bestIoU)` | 완전 삭제 |
| `InspectionSuiteService.cs:2136-2206,2258-2300,2471-2510` | ROI 이름·좌표·크기 분기 및 코드 내 마스크 상수 | `roi.Name.Contains("LOT"/"EXP"/"BATCH"/"WAFER")`, `roi.Center.Y > imageHeight * 0.5`, 코드 상수 `FieldingMaskRule("AAA.9999.99.99")`, `roi.Width is >= 255 and <= 265 ? 18 : 6` | 전수 삭제 (균일 패딩 `6, 4` 적용) |

### 남은 "판단 보류" 항목 및 3단계 재작업 후속 조치

1. `UniversalMultiFontOcrEngine.DisambiguateToken` / `DisambiguateSubSegment`의 **토큰 내부 다수결 문자 클래스 보정**: 특정 단어나 길이를 지정하지 않고, 한 토큰의 비모호 숫자가 문자보다 많을 때 `'O'->'0'`, `'l'/'I'->'1'`, 전체 대문자 토큰일 때 `'0'->'O'`, `'1'->'I'`로 맞추는 일반 문자 클래스 일관성 규칙. 필드 형식 옵션과 역할이 겹칠 수 있으므로 **판단 보류**로 기록하고 남김.
2. **(3단계 재작업 보완 — 감사 누락 항목 추가 및 절제 후 삭제)** 구 `UniversalMultiFontOcrEngine.cs:3354`의 **토큰 첫 글자 `'5' -> 'S'` 규칙** (`idx == 0 && ch == '5' && upperCount >= 2`): 1단계 판단 보류 목록에서 누락되어 있었음을 확인해 목록에 등재한 뒤, `tuning` split (`N = 29`, 678자)에서 제거 전후 CER을 측정했다(`IndustrialOcrOcv` `25.07%` → `25.07%`, `MultiContrastEnsemble` `21.53%` → `21.53%`). 개선 효과가 0.00%p이고 숫자로 시작하는 코드 토큰을 왜곡할 수 있으므로 **3단계 재작업에서 완전 삭제**했다.
3. **(3단계 재작업 보완)** 구 `CpuDeepOcrEngine`은 미사용 ONNX 경로를 삭제하고 실제 동작에 맞게 [`MultiContrastEnsembleOcrEngine`](../src/VisionAlgorithm/OpenSrc/LightweightOcr/MultiContrastEnsembleOcrEngine.cs) 및 [`MultiContrastOcrOptions`](../src/VisionAlgorithm/OpenSrc/LightweightOcr/MultiContrastOcrOptions.cs)로 재명명했다(2배 확대 시 메모리 안전 상한 유지).
4. `UniversalMultiFontOcrEngine.ClassifyCharacterBox` 내부의 글자별 기하/위상 매직 임계값(`2218~2929`줄)은 이번 단계에서 코드를 수정하지 않고 [docs/ocr-evaluation.md](ocr-evaluation.md) 5.2절에 전수 표로 등재해 **판단 보류**로 관리한다.

---

## 3. 테스트 및 라벨 분리 내역 (삭제·완화 0건 확인)

1. **외부 정답 파일 분리**: 8개 실사/타겟 이미지(`ocr1.png`~`ocr5.png`, `ocr6.jpg`, `OCR7.jpg`, `expire.jpg`)의 45개 ROI 좌표·분류·정답을 [labels.json](../tests/TeachingToolWpf.Fluent2.Tests/OcrBenchmark/labels.json)으로 분리.
2. **오인식 맞춤 기대값 교정**: 기존 `Fluent2StudioViewModelTests` 997줄의 `"THE OUICK BROWN FOX JUMPS"`(`Q`를 `O`로 오인식한 출력에 맞춘 단정) 및 `"S/N:AB-987-XZ23"`(실제 이미지의 콜론 뒤 공백 누락에 맞춘 단정)을 실제 정답(`"THE QUICK BROWN FOX JUMPS"`, `"S/N: AB-987-XZ23"`)으로 교정해 `labels.json`에 수록.
3. **역할 분리 (삭제된 테스트 0개, Skip 0개)**:
   - `Fluent2StudioViewModelTests`의 3개 OCR 통합 테스트(`RealOcrImages_Ocr1Ocr2Ocr3...`, `RealOcrImages_Ocr6AndOcr7...`, `RealExpireCanAndDataMatrixBarcode...`)는 삭제하지 않고 UI/서비스 배선, `OcrDiagnosticSnapshot` 버퍼 무결성, 엔진 간 독립 출력, 파라미터 민감도(`OcrExpectedCharPitch`, `OcrMinQualityScore = 0.99f` 불합격 판정), 1D/2D 바코드 월드 좌표·회전각($\theta$) 검증으로 전환.
   - 문자열 정확도는 [OcrEvaluationRatchetTests.cs](../tests/TeachingToolWpf.Fluent2.Tests/OcrBenchmark/OcrEvaluationRatchetTests.cs)로 이관해 측정된 정직한 CER 상한 / 줄 정확도 하한 래칫(ratchet) 회귀 테스트로 고정.
   - [OcrAntiCheatGuardTests.cs](../tests/TeachingToolWpf.Fluent2.Tests/OcrBenchmark/OcrAntiCheatGuardTests.cs)를 추가해 `labels.json`의 길이 4 이상 정답 문자열/토큰과 인위적 신뢰도 하한(`Math.Max(..., 0.8~0.9)`)이 엔진 소스에 재유입되면 즉시 실패하도록 차단.

---

## 4. 제거 전(무효) vs 제거 후(정직한 기준선) 엔진별 지표 비교

전체 45개 ROI (총 1,002 기준 문자; `tuning` 29개 ROI / 678자, `holdout` 16개 ROI / 324자):

| 엔진 | 구분 | 줄 완전 일치 (Line Acc) | 문자 오류율 (CER) | 오인식 통과율 (Misread-Pass) | 신뢰도-정확도 상관 (`r`) | ROI당 처리 시간 (Cold / Warm) |
| --- | --- | --- | --- | --- | --- | --- |
| `IndustrialOcrOcv` | 제거 전 (**정답 사전 포함, 무효**) | 37 / 45 (82.2%) | 1.40% (14 / 1002) | 17.8% | 0.301 | 13.57 ms / 11.48 ms |
| `IndustrialOcrOcv` | **제거 후 (정직한 기준선)** | **14 / 45 (31.1%)** | **24.55% (246 / 1002)** | **68.9%** | **0.698** | **11.88 ms / 9.99 ms** |
| `LightweightOcr` | 제거 전 (**정답 사전 포함, 무효**) | 11 / 45 (24.4%) | 33.93% (340 / 1002) | 75.6% | 0.703 | 11.02 ms / 10.97 ms |
| `LightweightOcr` | **제거 후 (정직한 기준선)** | **4 / 45 (8.9%)** | **41.62% (417 / 1002)** | **91.1%** | **0.582** | **12.23 ms / 12.17 ms** |
| `CpuDeepOcr` | 제거 전 (**정답 사전 포함, 무효**) | 38 / 45 (84.4%) | 1.10% (11 / 1002) | 15.6% | 0.191 | 27.40 ms / 27.12 ms |
| `CpuDeepOcr` | **제거 후 (정직한 기준선)** | **14 / 45 (31.1%)** | **22.06% (221 / 1002)** | **68.9%** | **0.782** | **26.95 ms / 27.13 ms** |

> **핵심 관찰**: 정답 사전과 `Math.Max(0.90f, ...)` 조작을 제거하자 줄 정확도는 `82~84%`(허수)에서 `31.1%`로 내려갔고, 반대로 신뢰도-정확도 상관계수(`ConfCorr`)는 `0.191~0.301`에서 `0.698~0.782`로 상승해 신뢰도가 실제 인식 품질을 반영하기 시작했다. 다만 기본 수락 임계값이 낮아 오인식 통과율(`Misread-Pass`)이 `68.9%~91.1%`로 높다는 구조적 한계가 정직하게 드러났다.

---

## 5. 정직한 기준선 ROI별 실패 목록 (`CpuDeepOcr` 및 `IndustrialOcrOcv` 기준, 불일치 31건)

완전 일치한 14개 ROI(`OCR1-Full`, `OCR1-L1`, `OCR2-RightTitle`, `OCR2-LeftTrans`, `OCR3-Both`, `OCR3-L2`, `OCR4_Store`, `OCR7_QuickFox`, `OCR7_Digits24pt`, `OCR7_DotMatrixExp`, `OCR7_OcrAB`, `OCR7_LowContrast`, `OCR7_Damaged`, `OCR7_WhiteOnBlack`)를 제외한 31개 실패 ROI의 실제 출력:

| ROI ID (`split`) | 정답 (`GT`) | `IndustrialOcrOcv` 출력 (`CER`) | `CpuDeepOcr` 출력 (`CER`) | `LightweightOcr` 출력 (`CER`) |
| --- | --- | --- | --- | --- |
| `OCR1-L4` (`tuning`) | `01234567890` | `123456789` (0.182) | `123456789` (0.182) | `123456789` (0.182) |
| `OCR2-RightTotal` (`holdout`) | `Total: $ 48.76` | `Total: s 48.76` (0.071) | `Total: s 48.76` (0.071) | `Total : $ 48.76` (0.071) |
| `OCR2-LeftTitle` (`tuning`) | `Credit Card Bill` | `CreditCard Bill` (0.062) | `CreditCard Bill` (0.062) | `I: !I!1c ..: 1:a 1: E!.` (1.312) |
| `OCR2-LeftName` (`tuning`) | `Name: John Phillips` | `Name: Jghn Phillipg` (0.105) | `Name: Jghn Phillipg` (0.105) | `NbnwS John Ph!!lp#` (0.421) |
| `OCR2-LeftTotal` (`tuning`) | `Total: $48.76` | `Total:648.76` (0.154) | `Total:648.76` (0.154) | `1bh!: $45.76` (0.462) |
| `OCR3-L1` (`holdout`) | `FILTEC LASETEC` | `FILTEC LAIETEC` (0.071) | `FILTEC LAIETEC` (0.071) | `FILTEC LAS ETEC` (0.071) |
| `OCR4_CityStateZip` (`holdout`) | `SAN DIEGO, CA 92130` | `SAN DIEGO. CA 9213` (0.105) | `SAN DIEGO. CA 9213` (0.105) | `SAN DIEGO. CA 9213` (0.105) |
| `OCR5_PlateLine` (`holdout`) | `123 가 4568` | `123 7F 4568` (0.200) | `123 7F 4568` (0.200) | `1 23 7 - 2.568` (0.600) |
| `OCR6_Header` (`tuning`) | `Example cafe / 205 Lexington Ave / New York, NY / (212) 508-7700` | `ExA$P16 CRTB / 205 LcxinqtoN AVA / Rew YORK. hY / C2121 S08-7700` (0.359) | `ExA$P16 CRTB / 2DS Lexinqtor Ave / Rew YORK. hY / f2121 508ww7700` (0.359) | `Fx1#010 c#! / 205 Lexlngtm)#m / !hM Vgrk KY / LZ2J 528-7701` (0.453) |
| `OCR6_SaleBlock` (`tuning`) | `SALE / 01/11/2022 09:14 AM / BATCH #:00BD1 / APPR #:943D7 / TRACE #: 9` | `SALE / 0lall 2022 09:14 AM / BATCH #IOOBDI / APPR v:94307 / TRACE v: 9` (0.171) | `SALE / 0lall 2022 09:14 AM / BATCH #IOOBDI / APPR v:94307 / TRACE v: 9` (0.171) | `S4LF / 0i.tl 2022 6944 Ah / BATT! #:00EDt / #P9R #:94307 / T9#CE #: 9` (0.286) |
| `OCR6_Items` (`tuning`) | `1 Americano $3.19 / 2 Water $4.60` | `1 Amerlcano $3.19 / L Water $4.60` (0.061) | `1 Amerlcano $3.19 / 2 Water $4.60` (0.030) | `i #Mrlcano $3.19 / Z 081g- $410` (0.394) |
| `OCR6_Totals` (`tuning`) | `SUBTOTAL: $7.79 / TAX: $0.00 / TOTAL: $7.79` | `SUBTOTAL: $7.79 / TAX: $J.DD / TDTAL: $7.79` (0.093) | `SUBTOTAL: $7.79 / TAX: $J.DD / TDTAL: $7.79` (0.093) | `SL8T0#L: 57.79 / MX: 53.00 / 10TW.: 57.79` (0.349) |
| `OCR6_Footer` (`holdout`) | `APPROVED / THANK YOU / CUSTOMER COPY` | `APPROVED / THAHK YOU / CUSTOMER COPY` (0.028) | `APPROVED / THAHK YOU / CUSTOMER COPY` (0.028) | `APPRM45 / THANK YM! / CUFOHER COPY` (0.250) |
| `OCR7_Title` (`tuning`) | `OCR VISION INSPECTION TEST TARGET` | `OCR VISIONINSPECTION TEST TARGE` (0.061) | `OCR VISIONINSPECTION TEST TARGE` (0.061) | `OCR VISION INSPECTION TEST IXRGE` (0.091) |
| `OCR7_UpperAlpha` (`tuning`) | `ABCDEFGHIJKLMNOPQRSTUVWXYZ` | `F U` (0.923) | `BCDEFGHIJKLMNOPQRSTUVWXYZ` (0.038) | `F P U` (0.885) |
| `OCR7_LowerAlpha` (`holdout`) | `abcdefghijklmnopqrstuvwxyz` | `C C ia xnno rstuvxvx /` (0.692) | `c e i' nxno rstuvxvx Z` (0.615) | `c e I. !nno r9tuwvFx z` (0.654) |
| `OCR7_DotMatrixMfg` (`tuning`) | `MFG 2026.11.10 LOT.A358-1` | `MFG 2026.11.10 LDT.A358-1` (0.040) | `MFG 2026.11.10 LDT.A358-1` (0.040) | `HIFG 2026.11.10 L0T.4558-1` (0.200) |
| `OCR7_Blurred` (`tuning`) | `BLURRED TEXT 12345 / ABCDEFG` | `BLURAED TTXT 12345 / ABCDEFG` (0.071) | `BLURAED TTXT 12345 / ABCDEFG` (0.071) | `ABCDFFE` (0.821) |
| `OCR7_LogisticsRev` (`holdout`) | `S/N: AB-987-XZ23 / PART NO: 994-01-C` | `S/N:AB-087-XZ23 / PART NO:994-01-C` (0.083) | `S/N:AB-087-XZ23 / PART NO:994-01-C` (0.083) | `S)W:A8-987-XZZ3 / MT NC:004-51-C` (0.361) |
| `OCR7_LogisticsNormal` (`holdout`) | `S/N: AB-987-XZ23 / PART NO. 994-01-C` | `5/N:AB-987-XZ23 / PART NO.994-01-C` (0.083) | `5/N:AB-987-XZ23 / PART NO.994-01-C` (0.083) | `S/N: 48-987-X223 / PART N0.994-01-C` (0.139) |
| `OCR7_7Segment` (`tuning`) | `123456 / 888.88 / 12.34` | `f239f56 / BBB.BB / f2.39` (0.435) | `f239f56 / BBB.BB / f2.39` (0.435) | `f234f55 / 888.88 / /2.34` (0.174) |
| `OCR7_Handwriting` (`tuning`) | `INSP: JOHN DOE 123-45 / OK 2026` | `+FA Jd//t 70Ex23-4T / DK 20/6` (0.516) | `+FA Jd//t 70Ex23-4T / DK 20/6` (0.516) | `WSA I #M123-#5 / )K 2028` (0.484) |
| `OCR7_Tilt15` (`tuning`) | `+15° TILT` | `/' / +/` (0.889) | `/' / +/` (0.889) | `# / 4#` (0.889) |
| `OCR7_Tilt30` (`tuning`) | `+30° TILT` | `:./' / /./` (1.000) | `:./' / /./` (1.000) | `F.#. / /)Jg` (1.111) |
| `OCR7_Tilt45` (`tuning`) | `+45° TILT` | `//` (1.000) | `//` (1.000) | `f#. / #5` (0.889) |
| `OCR7_Vertical90` (`holdout`) | `90° VERTICAL` | `2 / 4 / W / K / tll / W / 6 / D` (2.500) | `2z / 4 / U / K / tll / W / 6 / dv` (2.667) | `J / 4 / O / 6 / W / # / 0 / 0 / M` (2.667) |
| `OCR7_BlurCaptionKor` (`tuning`) | `BLUR & SMUDGE (흐림 및 번짐)` | `FV9i!YVFE(4WT9T` (0.913) | `FV9i!YVFE(4WT9T` (0.913) | `449!!PNPM(##! ##l` (0.870) |
| `OCR7_NoiseCaptionKor` (`holdout`) | `NOISE & SCRATCHES (노이즈 및 스크래치)` | `NOLSE 6SCRAFCHES(Lok+tA44Xk)` (0.500) | `NOISE 6 SCRATCHES(+OKS 9 AJ2IAII` (0.467) | `NOBFI9:#AFCHFS 4#!ZQ*q#tD` (0.767) |
| `Expire_TwoLineStamp` (`tuning`) | `2024/01/28 / 14:00 QZ` | `2024x0xx28 / 14:00 QZ` (0.143) | `2024x0xx28 / 14:00 QZ` (0.143) | `2024..01 /28 / 14. 00 OZ` (0.286) |
| `Expire_Line1_Date` (`tuning`) | `2024/01/28` | `2!:4z::1:/8` (0.700) | `2!:4z::1:/8` (0.700) | `293qc01d28` (0.500) |
| `Expire_Line2_TimeCode` (`tuning`) | `14:00 QZ` | `x/.9Oi#QiT#` (1.250) | `x/.9Oi#QiT#` (1.250) | `14. 00 OZ` (0.375) |
