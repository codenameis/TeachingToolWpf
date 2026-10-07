# TeachingToolWpf / VisionCanvas 산업용 ROI 필터

2026-10-06, .NET SDK 10.0.401 / Windows x64 / RTX 3060 Laptop GPU. [ADR-038](decisions/0038-industrial-roi-filters.md).

## 사용법

1. 앱에서 ROI를 선택하고 오른쪽 ROI 필터에서 항목을 선택한다.
2. 새 엔진 항목은 `CpuOnly` 또는 `CpuGpu`, 필요한 반경/sigma/임계값을 설정한다. `설정 적용`으로 등록하고 미리보기 ON/OFF를 선택한다. [후속 MVVM 컨트롤·슬라이더](filter-controls.md).
3. 입력칸에서 포커스를 옮기면 값이 확정된다. 등록 이후 변경은 자동 반영되며 Undo/Redo·복사·재선택에서도 설정을 유지한다.
4. 상태에서 실제 CPU 또는 CUDA 장치, 처리 전체 시간, 엔진 작업 메모리, 전송량과 CPU 전환 사유를 확인한다. 미리보기 OFF는 등록을 유지하고 표시만 끄며 재선택해도 유지한다. `등록 해제`는 Undo 가능한 설정 변경이다.

| 선택 항목 | 새 엔진 CPU | 실제 CUDA | 의미 |
|---|---|---|---|
| Gaussian | 검증 | 검증 | 분리 가능한 convolution |
| Erosion / Dilation | 검증 | 검증 | 회색조 사각형 min/max |
| Opening / Closing | 검증 | 검증 | 위 연산 두 단계 |
| White Top-hat / Black-hat | 검증 | 검증 | 원본-Opening / Closing-원본 |
| Threshold · 원본 명암 임계값 | 검증 | 검증 | 원본 명암에서 엄격한 `>`; 결과 0/255 |
| Gaussian → Black-hat → Threshold | 검증 | 검증 | 동일 반경 사용, 3단계 GPU 상주 후 한 번 다운로드 |
| 기존 Binarize / Canny | OpenCV CPU 유지 | 해당 없음 | 이전 8비트 변환과 임계값 의미 유지 |

반경 1–31은 **미리보기 픽셀** 기준이며 커널 폭은 `2r+1`이다. sigma는 0 초과–100 이하. 임계값은 Gray8 0–255, Gray16 0–65535 명암 단위다. UI 입력 범위는 0–65535이고 Gray8에서 255보다 높은 임계값은 전경이 없다. 원본 임계값은 내부 Float32로 표현한다.

## 영상·수명·실행 계약

- Gray16은 필터 전에 8비트로 줄이지 않는다. ROI 추출/회전 보간은 기존 ushort Mat 경로, 이후 엔진은 Float32 중간 결과를 유지한다. 비마스크 최종 화면 값에만 Gray16 `/257`, ties-to-even 반올림, 0–255 clamp를 적용한다. 12/14비트의 별도 화면 Window/Level 설정은 이 패널에 없다. 원본은 변경하지 않는다.
- RGBA/BGRA는 색상 순서에 맞게 OpenCV 회색조 입력 변환(약 0.299R+0.587G+0.114B)을 거치고 알파는 무시한다. 새 필터의 계산 자체는 C#/ILGPU 엔진이다. 공개 계약에 Mat을 노출하지 않는다.
- ROI 자체를 독립 영상으로 취급하고 경계는 replicate다. 원/삼각형/폴리곤 마스크는 **출력 표시 마스크**이며 통계의 표본 제한이 아니다. 회전된 사각 bounding 영역의 픽셀로 계산한다.
- ROI당 긴 축 최대 1,024px, 선택 전체 약 4Mpx, 라이브 최대 5회/초라는 기존 한도를 유지한다. 큰 ROI는 LOD/축소를 포함하므로 미세 결함 판정이나 원본 해상도 검사 결과로 사용하지 않는다. 검사 서비스에 이 미리보기를 연결하지 않았다.
- CPU 계획은 GPU 팩토리를 호출하지 않는다. CUDA는 앱 worker에서 첫 사용 시 생성한다. `CpuGpu`는 `PreferGpu` 요청이며 자동 최적 장치 선택/항상 빠른 실행을 의미하지 않는다. Typed 지원 불가만 CPU로 전환하고 단계별 이유를 반환한다. 잘못된 입력/구현 오류는 오류로 표시한다.
- 엔진 host 작업 예산 128MiB, GPU 작업 예산 256MiB. UI에 표시한 최대 작업 메모리는 ROI별 엔진 보고의 최대치이며 Mat·표시 배열·디코더·CUDA context·프로세스 전체 메모리를 포함하지 않는다. 입력은 canonical Float32로 전송되어 업/다운로드는 각각 4B/pixel이다.
- 마지막 설정/크기 계획 하나를 재사용한다. 서로 다른 여러 ROI를 번갈아 처리하면 재컴파일할 수 있다. `FilteredRoi.Execution`, `PreparationMilliseconds`, `ReusedPlan`으로 조회한다. 전체 시간은 원본 읽기/ROI 추출/장치 준비/필터/표시 변환을 포함한 worker 시간이다. UI debounce 100ms·게시·렌더 완료 시간은 포함하지 않는다.
- `RoiFilterPreview`는 단일 호출자와 `IDisposable` 계약이다. 진행 중 Dispose는 거부한다. 취소 후 ProcessAsync/WhenIdle 완료를 기다리고 preview, host가 소유한 GPU backend 순서로 해제한다. 단순 취소 요청 직후에 버퍼를 반환하지 않는다.

VisionCanvas 재사용 호스트는 기본 `new RoiFilterPreview(loader, retainFrame)`로 CPU를 사용할 수 있다. CUDA를 원하는 호스트만 ILGPU 프로젝트를 참조하고 세 번째 인자로 장치 팩토리를 주입한다. Canvas 자체에는 ILGPU 참조가 없다. 독립 엔진의 [예제·벤치마크](../IndustrialVision.Filters/README.md)는 앱 없이도 실행된다.

## 실제 검증

| 항목 | 결과/증거 |
|---|---|
| 앱 Release 빌드 | PASS, 경고/오류 0 |
| Canvas 전체 회귀 | PASS 194개 (색상 순서·회전 원형 추가 전); 최종 새 엔진 통합 15개 별도 PASS |
| 앱 필터 세션 + 실제 CUDA | PASS 10개, CUDA Skip 0. 새 9개 선택 항목 모두 실제 장치 비교 |
| Core 등록·이력·복사 | PASS 13개 |
| WPF 실제 앱 `--smoke` | PASS, 종료 0. 숫자 바인딩·한 번의 Undo·다중 ROI·CPU/CUDA 마스크 일치·DX12 캡처·기존 회전/라이브/이미지 교체 포함 |
| 독립 CUDA smoke | PASS, Gray16/Float32 4096개 비교, NVIDIA GeForce RTX 3060 Laptop GPU / CUDA |
| 스타일 | `scripts/Format-Code.ps1 -Verify` PASS (WPF workspace load 경고는 있었음) |

테스트는 4095/4096의 한 단계 차이, 16비트 보존, 독립적인 사각 morphology 참조, 이미지보다 큰 Gaussian, RGB/BGR 순서·alpha, 회전된 원형 출력 마스크, CPU에서 GPU 팩토리 미호출, 명시적 GPU 불가 fallback, 구현 오류 전파, 취소, 계획 재사용을 확인한다. CUDA 테스트는 장치가 없으면 명시적으로 Skip한다. 실제 무GPU PC는 이번 실행 환경에서 미검증이다.

최초 앱 smoke에서 완료된 Task를 기다린 직후 Closing 안에서 Close를 재호출하는 오류를 발견했다. dispatcher에 양보한 뒤 종료하도록 수정했고 같은 전체 시나리오 종료 0으로 재검증했다.

최종 기능 smoke의 두 ROI(1024×977, 1024×614)는 CPU **122.33ms**, CUDA 준비를 포함한 CpuGpu **198.84ms**였다. 동시 개발 부하가 있는 단일 기능 검증이며 p50/p95 벤치마크가 아니다. GPU가 더 빠르다는 근거로 사용하지 않는다. 반복 처리 성능은 독립 엔진의 suites와 별도로 측정해야 한다.

증거: `artifacts/filter-tests/*.trx`, `artifacts/smoke.log`, `artifacts/industrial-filter-smoke.json`, `artifacts/industrial-filter-app.png`. 보존본은 `docs/data/industrial-roi-filters-20261006.zip`.

```powershell
dotnet build src/TeachingToolWpf/TeachingToolWpf.csproj -c Release
dotnet test tests/VisionCanvas.Tests -c Release
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --filter 'FullyQualifiedName~FilterPreviewSessionTests|FullyQualifiedName~IndustrialFilterCudaTests'
dotnet test tests/TeachingToolWpf.Core.Tests -c Release --filter 'FullyQualifiedName~FilterRegistrationTests|FullyQualifiedName~RoiClipboardTests'
src/TeachingToolWpf/bin/Release/net10.0-windows/TeachingToolWpf.exe --smoke
pwsh -File scripts/Format-Code.ps1 -Verify
```

Flat-field 보정 맵 UI, 나머지 단계 A/B/C, 자유 순서 레시피 편집기, 원본 해상도 검사 연결, 현장 검사 정답 데이터 검증, 이 UI 경로의 반복 지연 분포는 남은 작업이다. 이번 변경은 구현된 엔진 필터를 ROI 미리보기에서 사용하는 범위다.
