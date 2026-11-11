# OCR·OCV 통합 평가 및 PP-OCRv4 ONNX 비교 실측 보고서

## 1. 데이터셋 구성 및 분할 동결 (`schemaVersion: 2`)

- **라벨 파일**: [labels.json](../tests/TeachingToolWpf.Fluent2.Tests/OcrBenchmark/labels.json) (`schemaVersion: 2`, `splitSeed: 20261111`, 작업 착수 시점에 동결·재수정 금지)
- **전체 규모**: 로컬 저장소의 10개 실사·합성 테스트 이미지(`ocr1.png`~`ocr5.png`, `ocr6.jpg`, `OCR7.jpg`, `expire.jpg`, `dotocr.jpg`, `kensa.png`), 총 **60개 ROI** (기준 문자 **1,137자**) 및 **25개 인쇄 그룹(`printGroups`)**
  - `tuning`: **29개 ROI (48.3%, 678자)** — 임계값 스윕(`MisreadPassRate <= 5%`), 허용 문자 집합 전후 비교 전용
  - `holdout`: **31개 ROI (51.7% >= 30.0%, 459자)** — `dotocr.jpg` 5개 ROI + `kensa.png` 칩 #2~#6 10개 ROI 포함, 성능 보고 전용(`holdout` 열람 후 임계값 재조정·예외 규칙 추가 금지)
- **픽셀 비중첩 보장**:
  - 동일 이미지 내 `tuning` ROI와 `holdout` ROI 간 축정렬 경계 상자 중첩 **0건**
  - 동일 인쇄 그룹 내 OCV `teach` 영역(`teachEntryIds` + `ocvSpecimens`)과 `verify` 영역(`verifyEntryIds`) 간 픽셀 중첩 **0건**

---

## 2. 테스트 실행 분리 (`Category=KnownGap`) 및 안티치트 가드

### 2.1 회귀 테스트와 알려진 미달 목표(`KnownGap`) 분리 (`A1.3`)

목표 상수(`TargetNormalOkRate = 0.90`, `TargetDefectDetectionRate = 0.95`)를 완화하지 않고, 현재 고전 1D 투영 분할 한계로 목표에 미달하는 OCV 목표 검증 테스트 2건에만 `[Trait("Category", "KnownGap")]`을 부여해 일상 회귀 게이트와 분리했다.

```powershell
# 일상 회귀 게이트 (안티치트 가드, 5x7 렌더러 동일성, OCV 워크플로, PP-OCR ONNX CPU/CUDA, UI 회귀 전체 — 100% PASS 필수)
dotnet test tests\TeachingToolWpf.Fluent2.Tests\TeachingToolWpf.Fluent2.Tests.csproj --filter "Category!=KnownGap"

# 알려진 목표 미달(OCV 정상 OK율 >= 90%) 확인용 실행 (목표 상수 0.90/0.95 유지 상태에서 정직하게 FAIL)
dotnet test tests\TeachingToolWpf.Fluent2.Tests\TeachingToolWpf.Fluent2.Tests.csproj --filter "Category=KnownGap"
```

### 2.2 안티치트 가드 ([OcrAntiCheatGuardTests.cs](../tests/TeachingToolWpf.Fluent2.Tests/OcrBenchmark/OcrAntiCheatGuardTests.cs))

- `src/` 하위 전체 **723개 `.cs` 파일**(`src/VisionOcr.PpOcr/` 포함)에 대해 `labels.json` 전 항목의 길이 6 이상 부분 문자열(**506종**), 문자열 결합/보간/문자 배열 치트, OCR 경로 내 ROI `Name` 분기, 비승인 이미지 크기(`Width`/`Height`) 분기, 근거 없는 신뢰도 하한(`Math.Max(0.9f, ...)`) **0건**을 강제한다.

---

## 3. 평가 정직화 전/후 — 인쇄 그룹(`Print Group`) 단위 OCV 결과 (`A1.1`~`A1.2`, `A5`)

> [!IMPORTANT]
> 모든 비율 지표는 `성공/분모 (비율%) [Wilson 95% 신뢰구간 하한~상한]` 형식으로 기록하며, 분모 $n < 10$인 항목에는 `[표본 부족]` 태그를 함께 표기한다. 결함 검출률의 분모는 **정상 판정이 `OK`인 샘플만** 포함한다(이미 `NG`/`Review`인 샘플 제외).

### 3.1 실사 독립(`Real Independent`) OCV 그룹별 결과 (`Category=KnownGap`, 목표 `OK >= 90.0%` 미달)

| 인쇄 그룹 (`groupId`) | 이미지 / 인쇄 방식 | Teach 수 | Verify 수 ($n$) | 정상 `OK`율 [Wilson 95%] | `Review` / 거짓 `NG` | 결함 7종 검출률 (분모 = 정상 `OK`만) | 비고 |
| --- | --- | ---: | ---: | --- | --- | --- | --- |
| `pg_dotocr_5x7_crisp` | `dotocr.jpg` 상·중단 5×7 도트 | 7 | 2 | **2/2 (100.0%)** `[34.2%~100.0%][표본 부족]` | `0` / `0` | 7종 모두 **2/2 (100.0%)** `[34.2%~100.0%][표본 부족]` | `"MFG:200523"`, `"EXP:200527"` 정상 `OK` |
| `pg_dotocr_5x7_textured` | `dotocr.jpg` 하단 거친 지질 도트 | 6 | 3 | **1/3 (33.3%)** `[6.1%~79.2%][표본 부족]` | `0` / `2` | 7종 모두 **1/1 (100.0%)** `[20.7%~100.0%][표본 부족]` | 종이 결 노이즈로 5×7 `'O'`↔`'D'` 유사도 경합 |
| `pg_ocr7_sans_bold` | `OCR7.jpg` 산세리프 대문자 | 2 | 2 | **1/2 (50.0%)** `[9.5%~90.5%][표본 부족]` | `0` / `1` | 7종 모두 **1/1 (100.0%)** `[20.7%~100.0%][표본 부족]` | `OCR7_Title` 포인트 크기 차이 및 `'T'+'A'` 접촉 |
| `pg_kensa_chip_laser` | `kensa.png` 반도체 레이저 각인 (칩 #1 티칭 → 칩 #2~#6 검증) | 2 (`Chip 1`) | **10** | **2/10 (20.0%)** `[5.7%~51.0%]` | `2/10 (20.0%)` / `6/10 (60.0%)` | 7종 모두 **2/2 (100.0%)** `[34.2%~100.0%][표본 부족]` | 아래 3.3절 상세 분석 참조 |
| **합계 (실사 독립)** | **4개 활성 그룹** (`21개 그룹 NOT_RUN`) | **17** | **17** | **6/17 (35.3%)** `[17.3%~58.7%]` — **목표 `90.0%` 미달** | `2/17 (11.8%)` / `9/17 (52.9%)` | 7종 모두 **6/6 (100.0%)** `[61.0%~100.0%][표본 부족]` | `TaughtFontOcv_PerPrintGroup_RealIndependent_MeetsTargetRates` (`KnownGap`) |

### 3.2 실사 변형(`Perturbed Real`) 정직화 전/후 비교 (`A1.1`: Leave-One-Out 분리)

- **정직화 전 (구 자기 검증)**: 검증 대상 줄 $X$의 원본 패치를 골든 폰트 티칭에 포함시킨 뒤 같은 줄 $X$의 변형본(서브픽셀 이동 `+0.45,-0.30px`, 회전 `+1.5°`, 가우시안 노이즈 `σ=3.5`, 밝기 `-10%`)을 검증함 → `75/80 (93.8%)` (`kensa.png` 포함 29줄 기준 `102/116 = 87.9%`). 이는 **변형 강건성 자기 검증**이지 미학습 샘플에 대한 OCV 성능이 아님.
- **정직화 후 (Leave-One-Out OCV)**: 줄 $X$의 변형본을 검증할 때 **티칭 집합에서 줄 $X$를 반드시 제외**하고 같은 그룹의 나머지 비중첩 줄/`ocvSpecimens`만으로 골든 폰트를 구성함. 줄 $X$를 제외하면 티칭 표본이 0개가 되는 **단일 줄 그룹 18개 줄은 `NOT_RUN`**으로 분리 집계함.

| 구분 | 포함 대상 | 실행 줄 / 제외(`NOT_RUN`) 줄 | 정상 `OK`율 [Wilson 95%] | `Review` / 거짓 `NG` | 결함 7종 검출률 (분모 = 정상 `OK`만) | 판정 |
| --- | --- | --- | --- | --- | --- | --- |
| **정직화 후: Leave-One-Out 실사 변형 (실제 OCV 성능)** | 검증 줄 $X$ 티칭 제외 (`17`줄 × 4변형) | 실행 `17`줄 (`n=68`) / `NOT_RUN` `18`줄 | **42/68 (61.8%)** `[49.9%~72.4%]` | `4/68 (5.9%)` / `22/68 (32.4%)` | `Homoglyph` **17/17 (100.0%)** `[81.6%~100.0%]`, 외 6종 **42/42 (100.0%)** `[91.6%~100.0%]` | **목표 `90.0%` 미달** (`KnownGap` 분리) |
| **별도 참고표: 변형 강건성 (자기 검증 — OCV 성능 아님)** | 티칭 줄 자신 포함 (`29`줄 × 4변형) | 실행 `29`줄 (`n=116`) | **102/116 (87.9%)** `[80.8%~92.7%]` (기존 20줄 `75/80 = 93.8%`) | `5/116 (4.3%)` / `9/116 (7.8%)` | `Homoglyph` **69/69 (100.0%)**, 외 6종 **102/102 (100.0%)** | 미세 이동·회전·노이즈 강건성 참고치일 뿐 OCV 합격 근거로 사용 금지 |

### 3.3 `kensa.png` 반도체 칩 레이저 마킹(`pg_kensa_chip_laser`) 상세 결과 (`A5`)

- **티칭 조건**: 좌상단 `Chip 1`의 비중첩 2개 줄(`"K9X8G08U0M"`, `"SAMSUNG"`, `LightOnDark`, `DotClosingRadius = 1`)만 골든 폰트로 티칭.
- **검증 대상 (`holdout` 10개 ROI, `Chips 2..6`)**:
  - **OCV 결과 (`TaughtFontOcvVerifier`)**: 정상 `OK` **2/10 (20.0%)** `[5.7%~51.0%]` (`Kensa_Chip3_Part`, `Kensa_Chip5_Part`), `Review` **2/10 (20.0%)** (`Kensa_Chip2_Part`, `Kensa_Chip6_Part` — `'0'`↔`'8'`/`'U'` 유사 문자 마진 부족), 거짓 `NG` **6/10 (60.0%)** (`Chip4_Part` `'8'→'S'` 표면 그레인 간섭, `Chips 2..6 Vendor` `"SAMSUNG"` 중 `'M'`/`'U'`/`'N'` 레이저 도트 끊김·과분할 및 `'N'↔'M'` 혼동).
  - **원인**: 1D 수직 투영 분할기가 레이저 도트 간 미세 끊김이 있는 넓은 자폭 글자(`'M'`, `'U'`, `'N'`)를 과분할하거나 인접 글자와 병합함.

| ROI ID (`holdout`) | 정답 (`GT`) | OCV 판정 (`Decoded`, `Mean`) | `IndustrialOcrOcv` (`All` → `UpperSet`) | `LightweightOcr` (`All` → `UpperSet`) | `MultiContrastEnsemble` (`All` → `UpperSet`) | **`PP-OCRv4 ONNX`** (`All`, `Mean`) |
| --- | --- | --- | --- | --- | --- | --- |
| `Kensa_Chip2_Part` | `K9X8G08U0M` | `Review` (`K9X8G08U0M`, `0.866`) | `K9KOGOBUDM` → `K9K0GO8UDM` | `#IVCK#)ON` → `#5VCK#/0N` | `K9KOGOBUDM` → `K9K0GO8UDM` | `K9X8G08UOM` (`0.952`, CER `10.0%`) |
| `Kensa_Chip2_Vendor` | `SAMSUNG` | `NG` (`SAMSUMG`, `0.909`) | `$Ah9uNG` → `$AA9UNG` | `IMSUDB` → `9MSU#B` | `SAAISUNG` → `SAA1SUN6` | **`SAMSUNG`** (`0.971`, **CER `0.0%`**) |
| `Kensa_Chip3_Part` | `K9X8G08U0M` | **`OK`** (`K9X8G08U0M`, `0.866`) | `K9XBGDBUOW` → `K9XB6DBUOW` | `K9X!(M0Dk` → `K9X26M0DV` | `K9XBGDBUOW` → `K9XB6DBUOW` | `K9X8608UOM` (`0.962`, CER `20.0%`) |
| `Kensa_Chip3_Vendor` | `SAMSUNG` | `NG` (`SAXMMANG`, `0.746`) | `SAMS/N6` → `SAMS/N6` | `MtE9h6` → `M#E9#6` | `SAMS/N6` → `SAMS/N6` | **`SAMSUNG`** (`0.996`, **CER `0.0%`, Pass**) |
| `Kensa_Chip4_Part` | `K9X8G08U0M` | `NG` (`K9X8G0SU0M`, `0.825`) | `K9XBGOBUDW` → `K9XBGO8UDW` | `#1X00#)04` → `#9X00#I0N` | `K9X8608U0M` → `K9X8608U0M` | `K9X8608U0M` (`0.923`, CER `10.0%`) |
| `Kensa_Chip4_Vendor` | `SAMSUNG` | `NG` (`SAMSUMG`, `0.818`) | `9AMSUNB` → `9AMSUNB` | `SMIUHE` → `SM5U#6` | `9AMSUNB` → `9AMSUNB` | **`SAMSUNG`** (`0.995`, **CER `0.0%`, Pass**) |
| `Kensa_Chip5_Part` | `K9X8G08U0M` | **`OK`** (`K9X8G08U0M`, `0.880`) | `K9K86O8UDk` → `K9K86O8UDK` | `#5dX4#)DN` → `#50X4#IDN` | `K9X66O8UDM` → `K9X66O8UDM` | `K9X8G08UOM` (`0.963`, CER `10.0%`) |
| `Kensa_Chip5_Vendor` | `SAMSUNG` | `NG` (`SAUSUNG`, `0.875`) | `$Aht6UNG` → `$AKK6UNG` | `SIKMRE` → `SIKMR6` | `6AM6UR6` → `6AM6UR6` | **`SAMSUNG`** (`0.997`, **CER `0.0%`, Pass**) |
| `Kensa_Chip6_Part` | `K9X8G08U0M` | `Review` (`K9X8G08U0M`, `0.869`) | `K9X8DD6U0M` → `K9X8DD6U0M` | `R!BM08UDN` → `R1BM08UDN` | `K9XBGDBUOM` → `K9XB6DBU0M` | `K9X8608U0M` (`0.958`, CER `10.0%`) |
| `Kensa_Chip6_Vendor` | `SAMSUNG` | `NG` (`SAMSUNO`, `0.858`) | `$AM6UNB` → `$AM6UNB` | `IA#9#KD` → `#A#9#KD` | `SAM$UN6` → `SAM$UN6` | **`SAMSUNG`** (`0.991`, **CER `0.0%`**) |

---

## 4. 범용 OCR 4종 엔진 전체 비교 (`A1.4`, `A3.3`, `B3.1`~`B3.4`)

> [!WARNING]
> **고전 엔진 3종의 구조적 한계 명시 (`A1.4`)**: **현재 고전 엔진 3종(`IndustrialOcrOcv`, `LightweightOcr [실험용]`, `MultiContrastEnsembleOcrEngine`)은 범용 판독 용도로 사용 불가(`holdout` 거부율 `93.5% ~ 100.0%`)**하다. 내장 원형이 표준 5×7 도트 + 소수 비트맵 원형뿐이므로 미등록 다중 폰트·한글·영수증·반도체 각인을 일반화해 읽지 못한다. 동일 인쇄 그룹의 기대 문자열 검증에는 티칭 기반 OCV(`TaughtFontOcvService`)를, 미등록 다중 폰트 범용 판독에는 `PP-OCR (ONNX)`를 사용해야 한다.

### 4.1 60개 ROI(`29 tuning` + `31 holdout`) 보정 후 4개 엔진 성능 비교표

합격(`IsPass`) 임계값은 모두 **`tuning` (`N = 29`)에서만** 오인식 통과율(`MisreadPassRate`) $\le 5.0\%$를 만족하는 최소 거부율 조합으로 고정했다(`holdout` 열람 후 재조정 없음):
- `IndustrialOcrOcv`: `mean >= 0.952`, `minChar >= 0.800`
- `LightweightOcr [실험용]`: `mean >= 0.865`, `minChar >= 0.865`
- `MultiContrastEnsemble` (`CpuDeepOcr`): `mean >= 0.938`, `minChar >= 0.865`
- `PP-OCRv4 ONNX` (`VisionOcr.PpOcr`): `mean >= 0.995`, `minChar >= 0.000` (보정 전 `0.50` 기준 `tuning MisreadPass = 51.7%` → 보정 후 `0.0%`)

| 엔진 | 장치 | Split ($N$) | 줄 완전일치율 [Wilson 95%] | CER (편집거리/기준자수) | 거부율 (`Reject`) | 오인식 통과율 (`MisreadPass`) | 신뢰도-정확도 상관 (`r`) | 세션 초기화 / `ColdMs` / `WarmMs` |
| --- | --- | --- | --- | --- | --- | --- | ---: | --- |
| `IndustrialOcrOcv` | CPU | `tuning` (29) | 8/29 (**27.6%**) `[14.7%~45.7%]` | **25.07%** (170/678) | 86.2% (25/29) | **3.4%** (1/29) | 0.667 | `0` / 8.75 / **8.54 ms** |
| `IndustrialOcrOcv` | CPU | **`holdout` (31)** | 7/31 (**22.6%**) `[11.4%~39.8%]` | **28.10%** (129/459) | **100.0%** (31/31) | **0.0%** (0/31) | 0.759 | `0` / 2.04 / **2.00 ms** |
| `LightweightOcr [실험용]` | CPU | `tuning` (29) | 1/29 (**3.4%**) `[0.6%~17.2%]` | **41.15%** (279/678) | 96.6% (28/29) | **3.4%** (1/29) | 0.540 | `0` / 10.20 / **9.95 ms** |
| `LightweightOcr [실험용]` | CPU | **`holdout` (31)** | 4/31 (**12.9%**) `[5.1%~28.9%]` | **74.29%** (341/459) | **100.0%** (31/31) | **0.0%** (0/31) | 0.855 | `0` / 2.25 / **2.15 ms** |
| `MultiContrastEnsemble` | CPU | `tuning` (29) | 8/29 (**27.6%**) `[14.7%~45.7%]` | **21.53%** (146/678) | 82.8% (24/29) | **3.4%** (1/29) | 0.803 | `0` / 25.65 / **25.64 ms** |
| `MultiContrastEnsemble` | CPU | **`holdout` (31)** | 7/31 (**22.6%**) `[11.4%~39.8%]` | **26.36%** (121/459) | **93.5%** (29/31) | **3.2%** (1/31) | 0.607 | `0` / 18.89 / **18.82 ms** |
| **`PP-OCRv4 ONNX`** | **CPU (기본)** | `tuning` (29) | **14/29 (48.3%)** `[31.4%~65.6%]` | **10.03%** (68/678) | 65.5% (19/29) | **0.0%** (0/29) | 0.764 | `567.44 ms` (3세션) / 124.39 / **125.88 ms** |
| **`PP-OCRv4 ONNX`** | **CPU (기본)** | **`holdout` (31)** | **18/31 (58.1%)** `[40.8%~73.6%]` | **6.97%** (32/459) | 71.0% (22/31) | **9.7%** (3/31) | 0.176 | `567.44 ms` (공유) / 68.40 / **68.41 ms** |
| **`PP-OCRv4 ONNX`** | **CUDA (RTX 3060)** | `tuning` (29) | **14/29 (48.3%)** `[31.4%~65.6%]` | **10.03%** (68/678) | 69.0% (20/29) | **0.0%** (0/29) | 0.764 | `2,776.42 ms` (3세션) / 1,878.01 / **785.59 ms** |
| **`PP-OCRv4 ONNX`** | **CUDA (RTX 3060)** | **`holdout` (31)** | **18/31 (58.1%)** `[40.8%~73.6%]` | **6.97%** (32/459) | 71.0% (22/31) | **9.7%** (3/31) | 0.176 | `2,776.42 ms` (공유) / 1,345.50 / **343.25 ms** |

- **CUDA 감속 실측 분석**: 소형 단일 ROI(`batch = 1`)에서 `PP-OCRv4` 동적 폭(`targetWidth = max(320, ...)`)과 `Det` 32배수 동적 해상도로 인해 ONNX Runtime CUDA EP의 cuDNN 알고리즘 탐색·커널 재컴파일 비용이 발생하여 CPU(`holdout WarmMs = 68.41 ms`)보다 RTX 3060 CUDA(`343.25 ms`)가 약 5.0배 느리게 측정되었다. 이에 따라 기본 실행 장치는 결정론적이고 지연이 낮은 **CPU**로 유지한다.
- **기본 엔진 유지 사유 (`B3.4`)**: 사용자 승인 범위(`기본 엔진 자동 교체는 승인 범위가 아님`) 및 외부 모델 파일(`models/ppocr/*.onnx`, 약 36.5 MB) 유무에 따른 실행 환경 독립성을 보존하기 위해 앱 기본 OCR 선택지는 기존 `IndustrialOcrOcv`를 그대로 유지하고, `PP-OCR (ONNX)`는 서비스 주입 시 선택 가능한 독립 옵션으로 노출한다.

### 4.2 허용 문자 집합(`OcrAllowedCharacterSet.UpperAlphaNumericAndSymbols`) 측정 (`A3.3`)

- 대문자 전용 줄(`tuning` $n = 21$, `holdout` $n = 27$)에서 `All` 대비 `UpperAlphaNumericAndSymbols` 적용 시:
  - **소문자 오인식 개수**: 세 고전 엔진 모두 `tuning` (`20 → 0`, `13 → 0`, `20 → 0`) 및 `holdout` (`12 → 0`, `23 → 0`, `6 → 0`)에서 **완전 0건**으로 제거됨.
  - **전체 CER 변화**: `IndustrialOcrOcv` `holdout` `28.13% → 29.41%`, `LightweightOcr` `holdout` `80.82% → 76.21%`, `MultiContrastEnsemble` `holdout` `26.60% → 28.13%` (소문자 후보가 제외된 자리에서 대문자/숫자 원형 간 `G↔6`, `S↔9/$` 오류가 남아 원형 부족 자체는 문자 집합 제한만으로 해결되지 않음).

### 4.3 `PP-OCRv4 ONNX` 취약 범주 및 미세조정(Fine-Tuning) 데이터·라벨링 제안 (`B3.3`, 학습 미실행)

1. **취약 범주 실측**:
   - **고각도 회전·특수기호 (`rotated`)**: `OCR7_Tilt15/30/45` (`tuning` CER `70.37%`), `OCR7_Vertical90` (`holdout` CER `83.33%`) — `°` 기호 누락 및 축정렬 ROI 내부 대각선 짧은 텍스트 박스 분할 한계.
   - **반도체 레이저 동형 문자 (`Kensa_Chip*_Part`)**: `"K9X8G08U0M"`에서 `'0'↔'O'`, `'G'↔'6'` 혼동으로 CER `10.0%~20.0%` 발생(`"SAMSUNG"`은 5/5 전수 일치).
   - **곡면 2줄 도트매트릭스 (`Expire_TwoLineStamp`, `Expire_Line2_TimeCode`)**: 캔 바닥 금속 반사광 + 넓게 떨어진 5×7 도트에서 `'Q'→'0'/'8'`, `':'→'.'` 혼동(`"2024/01/28"` 단일 줄은 100% 일치).
2. **향후 미세조정(Fine-Tuning) 계획 제안 (이번 작업에서는 학습 실행 안 함)**:
   - **데이터 수량**: ① 반도체 레이저 각인 크롭(실사 1,500줄 + 밝기/그레인 증강 3,000줄, 특히 `0/O`, `G/6`, `8/B`, `1/I` 집중), ② 산업용 5×7 / 7×9 CIJ 도트매트릭스 실사 1,500줄(금속 곡면 반사 포함), ③ 한글·영문 혼용 라벨/번호판 1,000줄(공백·괄호 포함) — 총 **약 4,000~5,500 단일 줄 실사 ROI** (`train 70% / val 15% / test 15%`, 장비·로트 단위 분할로 누수 차단).
   - **라벨링 규격**: PaddleOCR `TextRecognition` 표준 탭 구분 텍스트(`crop_rel_path\tGT`) + 회전/다줄용 4점 폴리곤 JSON(`points` 시계 방향 4점 + `transcription`), 작업자 2인 교차 검수로 `0`/`O` 라벨 오염 방지.

---

## 5. 고전 무티칭 OCR 3종 제거 및 산업용 택타임 실측

### 5.1 고전 무티칭 OCR 3종 제거 및 단일 `OCR / OCV` 통합

- **제거 대상 및 근거**: 4.1절에서 실측된 바와 같이 무티칭 고전 OCR 3종(`UniversalMultiFontOcrEngine`, `LightweightOcrEngine.RecognizeFastRoi`, `MultiContrastEnsembleOcrEngine`/`CpuDeepOcr`)은 `holdout` 거부율이 **`93.5% ~ 100.0%`**에 달해 범용 판독기로 사용할 수 없으므로 사용자 지시에 따라 제품 코드에서 제거했다.
- **통합 구성 (`OCR / OCV`)**:
  1. **범용 문자 판독 (`OCR`)**: `VisionOcr.PpOcr` (`PP-OCRv4 ONNX`, `det` + `en/korean rec`).
  2. **티칭 인쇄 품질 검증 (`OCV`)**: `TaughtFontOcvVerifier` / `TaughtFontOcvService` (`OK` / `Review` / `NG` 3상태 검증, 획 결손·번짐·대비 측정, `.ocvfont` 입출력).
  3. **티칭 보조 연결**: `PP-OCR` 판독 문자열을 선택한 ROI의 `OCV` 기대 문자열로 즉시 복사하는 1클릭 명령(`ApplyPpOcrTextToOcvExpectedText`) 제공 및 기존 문서 열거형(`IndustrialOcrOcv`/`LightweightOcr`/`CpuDeepOcr`) 자동 정규화(`NormalizeAlgorithmKind`).

### 5.2 `VisionOcr.PpOcr` 및 `VisionInspection` 택타임 최적화 항목

1. **무할당 버퍼 재사용 및 `Vector256` SIMD 전처리**: `ArrayPool<float>.Shared`·`ArrayPool<byte>.Shared` 대여(`DenseTensor<float>(Memory<float>, dims)` 직접 래핑), `Vector256<float>` 평면 정규화(`Rec`/`Det`) 및 `Vector256<byte>` 비트 보수 극성 반전(스칼라 꼬리·폴백 포함).
2. **무복사 1패스 CTC 디코딩 및 무손실 한영 단락**: 타임스텝당 어휘 복사 없이 단일 순회로 허용 문자 집합 마스킹·최대 로짓 탐색·소프트맥스 보정을 수행하며, 영문 가중 신뢰도 `enScore >= 0.990`일 때 한국어 세션이 수학적으로 역전 불가능함을 이용해 출력 변화 `0%`로 `_koreanSlot` 호출을 생략한다.
3. **32px 폭 버킷 다중 ROI / 다중 쿼드 배치(`RecognizeBatch`)**: 다중 ROI 및 `Det+Rec` 다중 단어 크롭을 32px 단위 폭 버킷으로 정렬·묶어 실행하되, CPU 실행 시 SVTR-LCNet 중간 활성 텐서가 L2/L3 캐시(`~1.9 MB`)에 상주하도록 마이크로배치 크기(`batch = 2`)로 제한하고 CUDA에서는 동적 배치를 사용한다.
4. **운용 프리셋(`PpOcrSpeedPreset`) 분리**:
   - `UniversalAuto` (기본): 단줄/다줄 자동 판별, 양극성 자동 탐색, 한·영 신뢰도 선택, 저신뢰 180° 재검사를 모두 유지(**60개 ROI 정확도 `32/60 (53.3%)`, 전체 CER `8.71% (99/1,137)` 보존**).
   - `HighSpeedProduction` (고속 양산): 정렬·조명 극성이 고정된 양산 라인용(`RecOnly` 직행 + 단일 극성 + 단일 언어 슬롯 + 정방향 전용).

### 5.3 Release CPU 택타임 실측 (`PpOcrInferenceServiceTests`, 중앙값 Warm 지연)

| 시나리오 | 대상 데이터 | 최적화 전 / 순차 `UniversalAuto` | 배치 `UniversalAuto` | **`HighSpeedProduction` (고속 양산)** | 비고 |
| --- | --- | ---: | ---: | ---: | --- |
| **단일 ROI (`PP-OCR`)** | `kensa.png` `Kensa_Chip2_Vendor` (`"SAMSUNG"`) | `57.30 ms` | `57.30 ms` | **`9.96 ms`** (`5.8×` 단축) | `"SAMSUNG"` 정확 일치 (`Pass=True`) |
| **10-ROI 실사 칩 트레이 (`PP-OCR`)** | `kensa.png` `Chips 2..6` (`5칩 × 2줄 = 10 ROI`) | `546.12 ms` (`54.61 ms/ROI`) | `508.97 ms` (`50.90 ms/ROI`) | **`132.36 ms` (`13.24 ms/ROI`)** (`4.1×` 단축) | 순차↔배치(`Auto`) 10/10 문자열·판정 100% 동일 |
| **32-ROI 산업용 트레이 (`PP-OCR`)** | `kensa.png` 칩 마킹 32개 ROI 배치 | `1,647.18 ms` (`51.47 ms/ROI`) | `1,552.64 ms` (`48.52 ms/ROI`) | **`372.52 ms` (`11.64 ms/ROI`)** (`4.4×` 단축) | CPU 단순 `batch=32`(`814.55 ms`) 대비 L2 캐시 마이크로배치(`batch=2`)가 `2.2×` 빠름 |
| **다중 단어 영수증 (`Det+Rec`)** | `ocr6.jpg` 전체 영수증 (DBNet + 쿼드 배치 인식) | `261.57 ms` | **`194.72 ms`** | N/A (다줄은 `Det+Rec` 사용) | 검출 쿼드 폭 버킷 마이크로배치 적용 |
| **티칭 기반 `OCV` 검증 (`TaughtFontOcvVerifier`)** | `kensa.png` 픽셀 비중첩 (`Chip 1` 티칭 → 검증) | — | — | **단일 `2.660 ms` / 32-ROI `24.87 ms` (`0.777 ms/ROI`)** | 고정 인쇄 마킹 초고속 인라인 검증 경로 |

- **미실행(`NOT_RUN`) 및 한계 명시**:
  - OpenVINO EP / TensorRT INT8·FP16 엔진 변환 가속 및 실제 현장 카메라·조명·PLC 연동 인라인 지속 처리량은 **`NOT_RUN`**이다.
  - `HighSpeedProduction` 프리셋은 단줄 크롭·단일 극성·정방향 정렬이 확보된 양산 ROI 전제이며, 다줄 문서·미지 극성·180° 역방향 입력에는 기본 `UniversalAuto`를 사용해야 한다.
