# ROI 마스킹

2026-10-06. [ADR-040](decisions/0040-roi-masking.md).

## 사용법

1. 검사 ROI 하나를 선택한다. 오른쪽 상단 **마스킹**에서 **사각형 추가** 또는 **타원 추가**를 누른다.
2. ROI 안에서 드래그한다. 여러 영역을 연속해서 그릴 수 있다. **마스크 그리기 완료** 또는 **Esc**로 선택 도구로 돌아간다. 드래그 중 Esc는 그 초안을 취소한다.
3. 사선이 있는 반투명 음영이 제외 영역이다. ROI 밖으로 그린 부분은 적용되지 않는다. 목록에서 선택하면 해당 마스크가 더 진하게 표시된다.
4. **선택 마스크 위치·크기**를 펼쳐 ROI 로컬 좌상단 기준 X/Y/너비/높이(px)를 바꾸고 **선택 마스크 변경 적용**을 누른다. ROI 회전 시 이 좌표축도 함께 회전한다.
5. **선택 삭제**, **모두 지우기**, **Ctrl+Z/Ctrl+Y**를 사용할 수 있다. 복사·붙여넣기에 마스크가 포함되고 ROI 크기 변경 시 비례해서 변한다.

**표시**는 음영만 켜고 끈다. 꺼도 학습·검사에는 적용된다. 최대64개이며 마지막 마스크를 추가하면 그리기를 종료한다. 그룹 부모는 대상이 아니다. 기존 마스크를 직접 드래그해 이동/resize하는 핸들은 이번 버전에 포함하지 않는다. 숫자는 소수 셋째 자리까지 표시하지만 바꾸지 않은 값의 정밀도는 유지한다.

## 검사 의미

학습에서는 원본 ROI에서 가린 픽셀을 제외한다. 마스크가 바뀌면 세 매칭 방식 모두 재학습해야 한다. Undo로 학습 당시 마스크를 복원하면 모델을 다시 사용할 수 있다. 원본 파일·프레임·표시용 필터 픽셀은 변경하지 않는다.

**선택 ROI 검사**는 마스크와 겹치는 대상을 결과에서 제외한다. 부분적으로 가려진 대상을 남은 부분만으로 재점수화하는 기능은 아니다. 형상은 전처리 지지 영역도 제외하므로 마스크 가장자리의 대상도 제외될 수 있다. **전체 이미지 검사**에는 모델에 저장된 학습 마스크가 적용되며 문서의 모든 ROI 마스크를 합치지는 않는다. 검색 여유가 있어도 마스크 기준 프레임은 실제 ROI다.

## 검증 상태

- PASS: 실제 원/오목 폴리곤 clipping, 회전·resize, 포인터 초안·취소·대상 변경, 한 번 Undo/Redo·복사·삭제, 잘못된 값/그룹 차단,64개 상한에서 그리기 종료 Core7개.
- PASS: MVVM 입력 오류·보기 토글·미수정 숫자의 정밀도 보존과 세 방식의 마스크 변경 재학습/Undo5개.
- PASS: 원본 NCC/형상 학습 제외, 전체 마스크 오류, 두 형상 방식의 원본 피라미드 정밀화 후 제외와 비마스크 결과 보존, 도형 내부 제외 통합4개.
- PASS: GPU 상수48바이트 배치1개.
- PASS: 의도한 제외 후보가 검증 예산을 소모하거나 유효 대상을 가리지 않는 알고리즘 회귀1개. 전처리 이후 호출자가 입력 마스크를 재사용해도 제외 영역을 보존한다.
- PASS: 실제 CUDA 호출을 확인한 CPU/CUDA 제외 결과·남은 pose/점수 동등성1개. 합성320×128이며 건너뜀0, 대용량 성능 검증은 아니다.
- PASS: 전체 Release 빌드 경고/오류0, `git diff --check`. 숫자 정밀도·64개 상한·입력 버퍼 수명 보완을 포함한 최종 전체 회귀1,137개 통과(신규19개 포함, 실패/건너뜀0).
- FAIL(첫 UI 스모크): RTX3060·D3D12 debug에서 앱 시작과 사각형 입력은 통과했으나 타원 테스트의 시작점이 원 ROI 밖이라 추가 검증이 실패했다. 시작점을 ROI 안으로 수정했다.
- NOT_RUN(최종 UI): 수정 후 Windows 앱 재실행이 거절되어 타원 입력·음영 픽셀 비교 완료를 확인하지 못했다. WARP 실행도 미검증이다. 초기 실행을 최종 화면 검증 PASS로 간주하지 않는다.
- NOT_RUN(최종 형식 검사): 최초 컨벤션 검사 실패 후 이번 변경 파일에 한정해 whitespace/style 수정을 적용했다. 최종 `Format-Code.ps1 -Verify` 실행이 거절되어 최종 통과 여부는 미확인이다.
- NOT_RUN: 현장 영상 검출률, 많은 마스크의60FPS·검사 지연 분포, Linux. 기존 성능 미달 기록을 대체하지 않는다.

| 프로젝트 | 통과 | 실패 / 건너뜀 |
|---|---:|---:|
| TeachingToolWpf.Core.Tests | 255 | 0 / 0 |
| TeachingToolWpf.Imaging.Tests | 272 | 0 / 0 |
| VisionAlgorithm.Tests | 249 | 0 / 0 |
| VisionInspection.Tests | 111 | 0 / 0 |
| VisionCanvas.Tests | 198 | 0 / 0 |
| VisionCompute.Tests | 52 | 0 / 0 |

최종 원본 TRX는 `artifacts/mask-verified`, 이전 회귀는 `artifacts/mask-regression` 및 `artifacts/mask-final`, 첫 UI 실패 로그는 `artifacts/mask-gpu-01/artifacts/smoke.log`에 있다. [보관 증거](data/roi-masking-20261006.zip)에 함께 저장했다.

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
dotnet test tests/TeachingToolWpf.Core.Tests -c Release --filter FullyQualifiedName~RoiMaskTests
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --filter 'FullyQualifiedName~RoiMaskViewModelTests|FullyQualifiedName~MaskChangeInvalidates'
dotnet test tests/VisionInspection.Tests -c Release --filter 'FullyQualifiedName~ShapeExclusion|FullyQualifiedName~OriginalTrainingMasks|FullyQualifiedName~ExclusionInside'
dotnet test tests/VisionCanvas.Tests -c Release --filter FullyQualifiedName~MaskGpuLayoutTests
dotnet test tests/VisionAlgorithm.Tests -c Release --filter FullyQualifiedName~MaskingTests
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --filter FullyQualifiedName~RoiMaskCudaTests
dotnet test TeachingToolWpf.slnx -c Release --no-restore --logger trx --results-directory artifacts/mask-regression
pwsh -File scripts/Format-Code.ps1 -Verify
# 새 작업 디렉터리에서 앱 실행: --smoke --mask-smoke --gpu-debug
# WARP: 위 인수에 --software-rendering 추가
```
