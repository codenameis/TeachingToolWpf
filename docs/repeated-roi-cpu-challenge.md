# 1억 ROI CPU 챌린지 — 국소 변경과 실측

> 이후 사용자 승인으로 [메인 문서 통합과 CPU/GPU 추가 개선](main-editor-repeated-roi-validation.md)을 진행했다. 아래 수정 범위와 수치는 그 이전 단계의 기록이다.

2026-10-13, `main` 로컬 작업. 사용자의 수정 범위 최소화 요청에 따라 CPU 전용 WARP를 시험했다. [기존 DX12 표시 대기 개선](dx12-frame-pacing.md)의 슬롯 fence·Present(1)·대기 프레임1과 기존 편집/이력 구조를 유지한다. 커밋·푸시·새 의존성·설계 원칙 예외는 없다.

최종 실행 코드 변경은 **기존 Pipeline의 셰이더 진입점 선택 한 줄과 HLSL 한 파일**이다. CPU(WARP)는 빈 픽셀을 먼저 종료하고, GPU는 원래 계산 순서를 유지한다. CPU 시간/실행물 해시 계측은 기존 벤치마크와 집계기에, 셰이더 진입점 컴파일 확인은 기존 테스트에 추가했다.

## CPU 계측과 비교 조건

기존 `cpu_ms`는 렌더 스레드의 **경과 시간**이며 표시 준비/fence 대기를 포함한다. 이번에는 벤치마크 구간 경계에서 `Process.TotalProcessorTime` 차이도 기록한다. 이는 앱 프로세스의 사용자+커널 CPU 시간 합이다. WARP 작업자·UI·GC·계측이 포함되고 DWM/다른 프로세스는 제외된다. [Microsoft API 계약](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.totalprocessortime?view=net-10.0)을 따른다.

- `ProcessCpuMilliseconds`: 4초 측정 구간에서 실제 소비한 CPU 시간 합.
- `AverageCpuCores`: 위 CPU 시간 / 측정 경과 시간. 논리 코어 사용량 상당이며 사용률 백분율이 아니다. 이 장비는 논리 프로세서12개다.
- 집계의 `ProcessCpuMsPerFrame`: 구간 CPU 시간 / 같은 구간 제출 수. 병렬 작업을 합한 비용 추정이며 한 프레임의 wall time이 아니다.
- 입력 함수 p95, 프레임 간격, CPU 시간, 대기 시간은 별도 지표로 유지한다. WARP의 GPU 시간은 null이다.

Ryzen5 5600H/32GB, 8192² PCB,100,020,001개 반복 ROI, 수정 셀1개, viewport DIP1171.33×698.15/DPI1.5. GPU debug OFF, metrics와 render-profile ON, 구간마다 워밍업1초/측정4초, 화면 맞춤/확대의 수평·수직·대각 이동/회전과 개별 이동/회전이다. 렌더 제출 FPS이며 물리 표시/입력 지연 측정은 아니다. 일반 문서의 임의 ROI1억 개 지원으로 해석하지 않는다.

## CPU 결과

실행물 확인 후 `before-verified`→최종 CPU/GPU 진입점 분리 빌드의 `final-warp` 비교다. **CPU 전용60FPS 목표는 FAIL**이다. 최종 개별 편집의 프레임당 CPU 비용은 약6~16% 감소했지만 확대 이동에서는 감소가 거의 없는 구간도 있었다. 모든 조작에서 일정한 CPU 개선을 입증하지 못했다. 프레임당 CPU 비용 감소가 항상 프로세스 전체 사용률 감소를 뜻하지는 않는다. 예를 들어 개별 이동은 제출 수가 늘어 평균 논리코어 상당 사용량이5.39→5.59로 증가했다.

| 연속 입력 | FPS 전→후 | 프레임당 프로세스 CPU 합 전→후(ms) | 평균 논리코어 상당 전→후 |
|---|---:|---:|---:|
| 확대·수평 | 36.06 → 36.00 | 162.65 → 162.43 | 5.86 → 5.84 |
| 확대·수직 | 36.06 → 36.00 | 163.30 → 161.24 | 5.88 → 5.79 |
| 확대·대각 | 36.00 → 36.00 | 162.00 → 161.68 | 5.83 → 5.82 |
| 확대·회전 | 35.49 → 35.93 | 162.41 → 156.79 | 5.76 → 5.64 |
| 개별 이동 | 32.90 → 36.06 | 164.72 → 155.49 | 5.39 → 5.59 |
| 개별 회전 | 32.61 → 36.06 | 167.82 → 141.60 | 5.50 → 5.09 |

최종 화면 맞춤은59.71~60.07FPS, 제출 간격 p95 약21.0~21.1ms다. 확대/개별 편집의 p95는34.87~35.03ms로 여전히 프레임 예산을 크게 넘는다. 최종 화면 맞춤의 프레임당 CPU 비용은60.03~62.50ms였다.

초기 원본(`before`)의 개별 이동/회전은33.04/36.00FPS와162.17/165.04 CPU ms/frame이었다. 두 초기 후보(`coverage`, `coverage-repeat`)의 같은 구간은35.50~36.06FPS와135.31~141.93 CPU ms/frame이었다. 최종 개별 이동의 CPU 비용은 이 초기 후보보다 높았다. 진입점 분리 직전의 같은 CPU 바이트코드(`coverage-verified`)는 확대/개별 비용이 원본 대비4~10% 낮았지만 요약 비용은 증가했다. **최종 재실행에서 확대 개선폭이 재현되지 않은 구간도 포함**한다. 반복 변동이 있으므로 최선의 수치를 일반적인 개선율로 사용하지 않고, 마지막 결과를 표에 사용한다. 모든 방향·회전·장비에서 같은 성능을 보장하지 않는다.

상세/화면 맞춤 캡처는 원본과 최종 후보의 SHA256이 각각 완전히 같다.

- 상세: `DBD11673EF5F74C80A8C40458A0D66FEABEBF486321DB56D69222B3A01840633`
- 화면 맞춤: `0C4D423D0DC61111FD8FB98D494D2D5B716019C0EA41C64F9EC4C88640ACAE17`

## 실험과 범위

| 후보 | 변경 범위 | 판단 |
|---|---|---|
| 빈 픽셀 처리 후 수정 ID 조회 | 기존 HLSL에서 coverage가0인 픽셀을 먼저 종료 | 개별 편집 CPU 비용 감소 관측, 확대 개선폭은 변동. 이미지 동일. WARP에만 유지 |
| 상세 모드 계산 단순화 | 같은 셰이더에서 frac 기반 계산 | FPS 개선 없음, CPU 비용 증가. 원복 |
| 거리 하한으로 조기 종료 | 같은 셰이더에 SDF 전 범위 검사 | FPS 개선 없음, CPU 비용 증가. 원복 |
| 가시 셀 사각형 인스턴싱 | 기존 Pipeline/HLSL 두 파일의 가시 행·열/VS 경로 | 약35~36FPS, CPU 개선 없음. 원복; 생산 경로에 남기지 않음 |

인스턴싱은 같은 윤곽 계산을 유지했지만 삼각형 보간 변경으로 상세 캡처 해시도 달랐다. 효과 없는 후보의 정확성 예외를 허용하지 않았다. 최종 실행 셰이더와 Canvas DLL의 SHA256을 보고서에 추가해 실행물 식별을 보존한다.

coverage-first를 GPU에도 공통 적용한 비교(`gpu-before-verified`→`gpu-final-verified`)에서는 확대/개별 GPU p95가5.57~5.77ms→5.70~5.99ms인 구간이 있었다. 평균 제출은약60FPS지만 GPU 연산 개선 근거는 없어 **최종 GPU 진입점은 원래 순서로 유지**한다. 두 경로는 같은 `Coverage` 계산을 공유하고 Pipeline이 `gpu.IsSoftware`로 컴파일할 진입점만 고른다. SDK10.0.26100의 `fxc /T ps_5_1`로 비교한 바이트코드 SHA256은 다음과 같다.

- 원래 GPU PS와 최종 `PSMain`: `E93B93F9F28BBB41D61F37D2C75584E87EA5C318F0A77814811E3794C4E6F162`로 동일.
- 검증한 coverage-first PS와 최종 `PSMainCpu`: `1882D15DA7ECD774F758C9045388DB47A383356F64809E2F84F736D656515936`로 동일.

`gpu-final-verified`라는 파일명은 이 공통 적용 후보의 측정명이며 최종 채택 GPU 코드의 결과로 해석하지 않는다.

최종 GPU 실행은 `final-gpu`다. 첫 화면 맞춤59.79FPS/최대28.94ms, 나머지 구간59.997~60.012FPS, 제출 간격 p95 17.05~17.28ms였다. 확대/개별 GPU p95는5.23~5.89ms다. 해당 구간의 앱 프로세스 CPU 사용량은평균0.035~0.113 논리코어 상당이며, DWM/다른 프로세스를 포함한 전체 시스템 CPU는 아니다. 최종 GPU 두 캡처도 각각 원래 GPU 캡처와 해시가 같다. 엄격한16.67ms 예산은 계속 FAIL이다.

원복 파일의 과거 수정 시간이 증분 빌드에 남긴 실험용 산출물을 상수 레이아웃 회귀 테스트가 검출했다. `cpu-challenge-before-repeat`와 `cpu-challenge-gpu-before`는 **비교에서 제외**한다. 파일 변경 시간을 갱신하고 다시 빌드해64바이트 계약을 복구했으며, 이후 소스/배포 셰이더 해시를 확인하고 `*-verified`로 재측정한다. 제외된 실행을 원래 구현의 성능 근거로 쓰지 않는다.

WPR CPU 샘플링도 시도했으나 Windows가 `Failed to enable the policy to profile system performance`/`0xc5585011`로 거부했다. 기존 기록 없음과 시도 후 기록이 실행 중이지 않음을 확인했다. 권한·시스템 정책을 변경하지 않았다. 따라서 네이티브 함수별/코어별 ETW 병목을 확인했다고 주장하지 않는다. 프로세스 CPU 합계는 정상 수집했다.

## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore -m:1 --disable-build-servers
Get-FileHash src/VisionCanvas/Shaders/RepeatedRoi.hlsl
Get-FileHash src/TeachingToolWpf/bin/Release/net10.0-windows/Shaders/RepeatedRoi.hlsl
& .\src\TeachingToolWpf\bin\Release\net10.0-windows\TeachingToolWpf.exe --repeated-roi-benchmark --software-rendering --roi-report artifacts/cpu-check.json --metrics artifacts/cpu-check.csv --render-profile artifacts/cpu-check-profile.csv
pwsh -File scripts/Measure-RepeatedRoi.ps1 -Prefix artifacts/cpu-check
```

GPU 비교는 `--software-rendering`만 제외한다. 빌드/테스트가 끝난 뒤 성능 시험을 실행한다. 과거 보고서에 없는 CPU 시간/해시는 집계에서 null로 유지한다.

원시 보고서·CSV·캡처는 로컬 `artifacts/cpu-challenge-*`다. [보관 집계](repeated-roi-cpu-results.json)에 채택/제외 후보, 실행물 해시와 각 구간의 실제 수치를 보존한다. 최종 `final-warp`와 `final-gpu`는 각각 exit0이며 소스/배포 셰이더 및 실행 DLL 해시가 보고서와 일치함을 확인했다. 측정 중 빌드·테스트나 실행 파일 교체는 하지 않는다.

## 검증

- PASS: 최종 전체 Release 솔루션 빌드 경고0/오류0.
- PASS: Canvas206개, 실패/건너뜀0. 원래64바이트 상수 레이아웃과 VS/GPU PS/CPU PS 컴파일 포함.
- PASS: 이번 변경 C#3파일의 whitespace/스타일 검사, `git diff --check`. 전체 저장소의 기존 별도 검사 코드 줄바꿈 이슈를 수정하지 않았다.
- PASS: CPU/GPU 각 경로의 컴파일 바이트코드 전후 동등성, WARP 상세/요약 PNG 동일, native 더블클릭 선택 교체·개별 이동/회전·Undo/Redo·취소 검증.
- PASS: 읽기 전용 독립 리뷰, 아래 SOLID5/GRASP9 검토. 원칙 예외 없음.
- FAIL: 모든 프레임16.67ms 이내의60FPS. WARP 확대/개별 편집 약36FPS. GPU 평균약60FPS도 기존 엄격한 모든 프레임 수용 조건을 대신하지 않는다.
- NOT_RUN: WPR/ETW 함수별 네이티브 CPU 스택. OS 프로파일 권한 오류를 프로세스 CPU 합계 측정 실패와 혼동하지 않는다.

## 원칙 검토

| 원칙 | 검토 |
|---|---|
| SRP | 픽셀 판정은 셰이더, CPU 측정은 기존 벤치마크, 집계는 기존 스크립트에 유지 |
| OCP | 새 공개 계약/렌더 방식 없이 기존 파이프라인 내부의 컴파일 진입점 선택 |
| LSP | GPU/WARP에서 동일한 윤곽·수정 ID 판정 유지 |
| ISP | 공개 인터페이스 추가 없음 |
| DIP | Core에 DX12/진단 의존성 추가 없음 |
| Information Expert | coverage와 override 조회 순서는 해당 픽셀 셰이더가 소유 |
| Creator | GPU 리소스 생성·소유권 변경 없음 |
| Controller | 입력·편집·이력 흐름 변경 없음 |
| Low Coupling | 기존 셰이더/진단 내부에 국한 |
| High Cohesion | 관련 없는 렌더러/문서 정리 없음 |
| Polymorphism | GPU/WARP의 실제 실행 차이만 기존 내부 진입점으로 처리; 새 공개 대체 구현 없음 |
| Pure Fabrication | 계측은 도메인 밖의 기존 진단에 유지 |
| Indirection | RenderScene/파이프라인 경계 유지 |
| Protected Variations | 저장 구조·원본 이미지·ID·Undo/Redo·fence 수명 보존 |

많은 개별 수정,24K 실제 파일, 장시간/다른 장비, 일반 문서 전체 부하와 물리 입력→표시는 이번 범위에서 미검증이다. 구조 확대나 원칙 완화가 필요한 다음 구현은 사용자에게 먼저 질문한다.
