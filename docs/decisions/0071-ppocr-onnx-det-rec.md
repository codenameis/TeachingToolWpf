# ADR-071 — 범용 OCR을 위한 PP-OCRv4 ONNX (Det + Rec) 도입 및 구현

- 상태: **승인됨 (Accepted — 2026-11-11 사용자 명시적 승인)** (`AGENTS.md` ONNX 허용 범위를 `PoseMatch.Tdcm`에 더해 범용 OCR 서비스까지 확장; 미세조정 학습 실행 및 기본 엔진 자동 교체는 제외)
- 날짜: 2026-11-11

## 배경 및 결정 사유

1. 1단계 정직한 기준선([ocr-honest-baseline.md](../ocr-honest-baseline.md)) 및 3단계 보정([ocr-evaluation.md](../ocr-evaluation.md)) 결과, 정답 사전을 제거하고 `tuning` 오인식 통과율 $\le 5\%$로 합격 임계값을 보정한 고전 템플릿/투영 엔진 3종(`IndustrialOcrOcv`, `LightweightOcr`, `CpuDeepOcr`)의 `holdout`(31개 ROI) 거부율은 **93.5% ~ 100.0%**로 측정되어, 처음 보는 다중 폰트·한글·영수증·칩 마킹의 범용 판독 용도로는 사용할 수 없음이 확인되었다.
2. 기대 문자열과 동일 인쇄 그룹의 골든 폰트가 주어지는 정형 인쇄 품질 검사는 `TaughtFontOcvVerifier` / `TaughtFontOcvService`(티칭 기반 OCV)로 UI까지 연결해 분리하고, 미등록 다중 폰트·한글·반전 각인·기울어진 문자열을 읽는 **범용 OCR** 트랙에는 독립 프로젝트 `src/VisionOcr.PpOcr`의 PP-OCRv4 ONNX(`Det + Rec`) 파이프라인을 도입한다.
3. 사용자의 2026-11-11 명시적 승인에 따라 사전 학습된 공식 PP-OCRv4 ONNX 추론만 연결하며, 신경망 학습(fine-tuning) 실행과 기본 OCR 엔진 자동 교체(`IndustrialOcrOcv` 기본값 유지)는 수행하지 않는다.

## 구현 결정

### 1. 공식 사전 학습 모델 출처·라이선스·SHA-256 (`models/ppocr/`, `.gitignore` 제외)

모든 가중치와 사전 파일은 공식 PaddleOCR `PP-OCRv4` 체크포인트를 ONNX로 변환 배포하는 `RapidAI/RapidOCR` `v3.10.0` 릴리스(라이선스: **Apache-2.0**)에서 내려받아 SHA-256을 검증했다. 대용량 이진 가중치는 Git 추적 대상에서 제외(`models/` `.gitignore`)하고 로컬 경로를 `PpOcrModelOptions`로 주입한다.

| 파일명 | 역할 | 크기 (Bytes) | 라이선스 | SHA-256 | 공식 다운로드 URL |
| --- | --- | ---: | --- | --- | --- |
| `ch_PP-OCRv4_det_mobile.onnx` | DBNet 텍스트 영역 검출 (`Det`) | `4,745,517` | Apache-2.0 | `d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9` | https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv4/det/ch_PP-OCRv4_det_mobile.onnx |
| `en_PP-OCRv4_rec_mobile.onnx` | 영문·숫자·기호 인식 (`Rec`) | `7,653,044` | Apache-2.0 | `e8770c967605983d1570cdf5352041dfb68fa0c21664f49f47b155abd3e0e318` | https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv4/rec/en_PP-OCRv4_rec_mobile.onnx |
| `korean_PP-OCRv4_rec_mobile.onnx` | 한국어·영문·숫자 인식 (`Rec`) | `24,067,780` | Apache-2.0 | `ab151ba9065eccd98f884cf4d927db091be86137276392072edd4f9d43ad7426` | https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv4/rec/korean_PP-OCRv4_rec_mobile.onnx |
| `en_dict.txt` | 영문 CTC 문자 사전 (95줄 + 공백 클래스) | `190` | Apache-2.0 | `5662df9d2d03f0e8ca0d3b0649d6acbab904b6a14b3d3521463c71c37c668ce3` | https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv4/rec/en_PP-OCRv4_rec_mobile/en_dict.txt |
| `korean_dict.txt` | 한국어 CTC 문자 사전 (3,688줄 + 공백 클래스) | `14,480` | Apache-2.0 | `aa1fdc8ae8f7cd40a0ec4edb472eb0421e11427e6ccfee9915440742c18b0a20` | https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv4/rec/korean_PP-OCRv4_rec_mobile/korean_dict.txt |

### 2. 런타임 패키지 통일 및 명시적 세션 수명 (Zero Singleton)

- **패키지 통일 (`B1.2`)**: `src/VisionOcr.PpOcr/VisionOcr.PpOcr.csproj`를 분리 생성하고, 기존 CPU 전용 `Microsoft.ML.OnnxRuntime` 1.24.4가 네이티브 `onnxruntime.dll`을 덮어쓰던 충돌을 해소하기 위해 `VisionAlgorithm.csproj`와 `VisionOcr.PpOcr.csproj` 모두 기존 `PoseMatch.Tdcm`과 동일한 `Microsoft.ML.OnnxRuntime.Gpu` **1.24.4**로 통일했다.
- **세션 수명 (`B2.1`)**: `PpOcrInferenceService : IPpOcrInferenceService`는 생성자에서 1회만 `InferenceSession`을 초기화(`SessionCreationCount == 3`: `det` + `en_rec` + `korean_rec`)하고 앱 `App.xaml.cs` 조립 루트에서 주입 후 창 종료 시 명시적으로 `Dispose()`한다.
- **모델 누락·손상 처리**: 필수 파일이 없으면 `PpOcrModelStatus.MissingModel`(`"모델 미설치"`)과 빈 결과를 정직하게 반환하고, 손상된 `.onnx` 파일은 예외를 삼키지 않고 `InvalidDataException`을 즉시 발생시킨다.
- **CPU 기본 + 선택형 CUDA**: 기본 장치는 결정론적 `Cpu`이며, `Cuda` 요청 시 초기화 실패 원인을 `CudaFallbackReason`에 기록한 뒤 CPU 세션으로 폴백한다.

### 3. 공식 전처리·후처리 규격 준수 (`B2.2`~`B2.5`)

- **`Rec` 전처리 (`PpOcrPreprocessor.BuildRecInputTensor`)**: 높이 `48` 고정, 종횡비 보존 선형 리사이즈(`targetWidth = max(320, ceil(48 * W / H))`), 픽셀 정규화 `(x / 255.0f - 0.5f) / 0.5f`, 우측 패딩 `0.0f`, `[1, 3, 48, targetWidth]`.
- **`Det` 전처리·후처리 (`PpOcrPreprocessor.BuildDetInputTensor` + DBNet)**: 테두리 중앙값 패딩 후 `32`의 배수 공간 정렬, ImageNet `(unit - mean) / std` 정규화, 확률 맵 이진화(`DefaultBinThreshold = 0.30f`) → 컨투어 평균 점수 필터(`DefaultBoxThreshold = 0.60f`) → 폴리곤 확장(`DefaultUnclipRatio = 1.5f`) → `WarpPerspective` 4점 투시 정립 크롭(`H/W >= 1.5` 시 `90°` 회전 보정).
- **CTC 디코딩 및 문자 집합 마스크 (`B2.3`~`B2.4`)**: 출력 텐서가 확률 분포인지 검사 후 필요 시 실제 `Softmax`를 수행하고, `blank = 0`, `1..N = dict`, `N + 1 = ' '`(`BaseRecLabelDecode(use_space_char=True)`) 규칙으로 연속 중복 제거 및 `blank` 제거를 수행한다. `OcrAllowedCharacterSet`은 argmax 이전 CTC 클래스 마스크로만 적용하며 사후 문자열 치환이나 `Math.Max(0.90f, ...)` 신뢰도 부풀리기는 일절 수행하지 않는다.
- **UI 조건부 노출 및 기본 엔진 유지 (`B2.6`, `B3.4`)**: `InspectionSuiteService.IsPpOcrWired == true`일 때만 `"PP-OCR (ONNX)"` 선택지를 노출하며, 기본 OCR 알고리즘은 기존 `IndustrialOcrOcv`를 유지한다.
