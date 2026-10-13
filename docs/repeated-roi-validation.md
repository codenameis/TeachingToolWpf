# 1억 ROI 반복 격자 — 사용법·검증

> 현재 메인 버튼은 [메인 편집기 통합·부분 선택/복붙](main-editor-repeated-roi-validation.md)을 사용한다. 아래 별도 창 사용법과 수치는 이전 CLI 시험의 이력으로 보존한다.

2026-10-13, `main` 로컬 변경. [ADR-041](decisions/0041-repeated-roi-grid.md).

후속 [DX12 표시 대기 개선·전후 비교](dx12-frame-pacing.md)에서 생산 코드3파일의 국소 변경으로 최신 입력 소비와 제출 간격을 개선했다. 아래 최초 측정은 이력으로 보존한다.

이후 [CPU WARP 챌린지](repeated-roi-cpu-challenge.md)는 실제 프로세스 CPU 시간까지 비교한다. 셰이더의 국소 변경만 유지했으며 CPU 전용60FPS는 미달이다.

## 사용법과 범위

메인 상단 **1억 ROI 테스트**를 누르면 100,020,001개(10,001×10,001) 사각 ROI를 이미지 중심에 배치한 별도 시험 창이 열린다. 정지 이미지의 가로·세로가 모두1024px 이상이면 현재 파일을 독립 reader로 읽는다. 작은 이미지·라이브·이미지 없음은 8192×8192 합성 PCB 이미지를 사용한다. 기존 문서는 유지한다.

- 기본 전체 선택: 드래그, 방향 버튼, ±15° 버튼, 원형 회전 핸들로 전체 배치를 이동·회전한다.
- **선택 위치 확대** 후 다른 ROI를 더블클릭하면 이전 선택을 교체한다. 행·열(1~10,001) 입력으로 화면 밖의 셀도 지정할 수 있다.
- 개별 선택: 드래그/회전 또는 중심X/Y·너비·높이·절대각을 입력하고 **개별 ROI 적용**. 크기는 수치로 편집한다.
- Undo/Redo 또는 Ctrl+Z/Y, Esc 취소, Ctrl+A 전체 선택. 휠 확대/축소, Space+드래그와 화면 이동 도구, 미니맵 탐색.
- 축소 상태에는 요약 표시 안내가 나온다. 모든 ID와 정확한 좌표는 유지되며 확대하면 개별 윤곽을 표시한다.

반복 사각형의 공유 배치와 개별 수정값을 저장한다. 일반 ROI 객체를1억 개 할당한 시험이 아니다. 메인 문서의 임의 도형·계층·복사·필터·마스크·검사·저장까지1억 개로 확장한 것은 아니다. 개별 수정 수가 매우 커질 때의 메모리/처리 비용도 별도 검증이 필요하다.

## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore -m:1
& .\src\TeachingToolWpf\bin\Release\net10.0-windows\TeachingToolWpf.exe --repeated-roi-benchmark --roi-report artifacts/repeated-roi-check.json --metrics artifacts/repeated-roi-check.csv
pwsh -File scripts/Measure-RepeatedRoi.ps1 -Prefix artifacts/repeated-roi-check
```

CPU 표시 시험은 앱 인수에 `--software-rendering`을 추가한다. 단계별 CPU 진단은 `--render-profile artifacts/repeated-roi-check-profile.csv`, DX12 검증 계층은 `--gpu-debug`로 별도 실행한다. 성능 비교 중 빌드·테스트를 동시에 실행하지 않는다.

보고서 JSON은 native 선택 교체·개별 이동/회전·Undo/Redo·취소·총 개수를 검증한 후 기록한다. fit/detail 수평·수직·대각 이동·회전과 개별 이동·회전의10구간에 각각1초 워밍업,4초 연속 입력을 가한다. 초기 JIT·타일 로드와 캡처는 측정 구간 밖이며, 첫 준비 지연의 수용 증거로 사용하지 않는다. `.detail.png`, `.fit.png`, `.window.png`는 렌더 readback/합성 UI 캡처다.

CSV의 Stopwatch ticks를 각 입력 구간과 결합해 제출 간격을 집계한다. 정적 화면의 렌더 생략을 드롭으로 세지 않는다. FPS는 해당 구간 첫/마지막 제출 사이에서 계산한다. CPU 값에는 frame slot fence 대기가 포함되며 GPU 시간과 합산하지 않는다. 입력 시간은 UI에서 호출한 편집 함수의 실행 시간으로, 물리 마우스부터 모니터 표시까지의 지연이 아니다. profile CSV의 `rois`는 기존 일반 문서 개수라 이 시험에서는0이고, 반복 ROI의 실제 개수는 JSON의 `LogicalRoiCount`를 사용한다.

## 장비와 결과

장비: Ryzen5 5600H, RAM32GB, Windows x64. DX12 선택 장치는 NVIDIA GeForce RTX3060 Laptop GPU, 전용 메모리5.9GiB, 실행 시 budget5.1GiB. 드라이버32.0.15.8183. WMI에서 NVIDIA 연결 화면3840×2160/60Hz, AMD 연결 화면1920×1080/144Hz를 확인했다. ROI 시험 viewport는1171.33×698.15 DIP, DPI1.5. 영상은8192² 합성 PCB, 수정 셀1개. DXGI `Present(1)` 유지.

**전체 프레임16.67ms 이하/60FPS 이상을 보장하는 조건은 FAIL**이다. 평균이60에 근접하거나 GPU 계산 시간이16.67ms보다 짧다는 이유로 전체 목표 통과를 선언하지 않는다. 아래 GPU2/3은 같은 렌더 구현을 별도 프로세스에서 반복한 결과이며 debug OFF, WARP는 기능/자원 확인을 겸해 debug ON이다. WARP 수치를 GPU와 동일 조건의 속도 비교로 해석하지 않는다. [전체 집계 JSON](repeated-roi-results.json)에 제출 수·예산 초과 수·입력/메모리/GC도 보관했다.

| 연속 입력 구간 | GPU2 / GPU3 평균 FPS | GPU2 / GPU3 간격 p95(ms) | GPU2 / GPU3 최대 간격(ms) | WARP 평균 FPS |
|---|---:|---:|---:|---:|
| 화면 맞춤·수평 | 59.78 / 59.78 | 17.75 / 17.70 | 29.75 / 29.58 | 60.02 |
| 화면 맞춤·수직 | 59.99 / 59.99 | 17.66 / 17.90 | 19.81 / 20.31 | 59.96 |
| 화면 맞춤·대각 | 60.00 / 60.02 | 17.77 / 17.90 | 19.42 / 19.61 | 59.96 |
| 화면 맞춤·회전 | 60.01 / 60.00 | 17.74 / 18.04 | 21.83 / 20.15 | 60.08 |
| 확대·수평 | 59.98 / 60.01 | 18.10 / 18.12 | 19.38 / 19.54 | 35.68 |
| 확대·수직 | 60.01 / 59.99 | 17.88 / 18.15 | 21.21 / 19.48 | 36.06 |
| 확대·대각 | 59.99 / 59.99 | 17.90 / 17.75 | 20.09 / 19.11 | 36.08 |
| 확대·회전 | 60.00 / 59.98 | 17.75 / 17.87 | 20.74 / 19.43 | 36.07 |
| 개별 이동 | 60.02 / 60.16 | 17.99 / 17.88 | 19.85 / 25.37 | 35.75 |
| 개별 회전 | 59.99 / 59.99 | 18.06 / 17.76 | 20.15 / 19.20 | 35.37 |

GPU 두 실행의 입력 함수 p95는0.0173~0.0446ms, 최악1.2515ms였다. 구간별 프로세스 private bytes 표본은277~297MB,4초 구간 GC 누적 pause는0~1.849ms였다. GPU p95는1.788~5.596ms, 최대14.024ms로 확대 시 픽셀 부하 차이는 남는다. WARP는 private bytes206~217MB, 입력 p95 0.0133~0.0263ms, 최대 간격37.58ms였으며 GPU 시간은 null(미측정)이다. 메모리 표본을 최초 로드/장시간 전체 peak로 표현하지 않는다.

별도 GPU1 profile 실행의 측정 구간2,400프레임에서 CPU mesh 구성 평균0.029/max0.339ms, draw 제출 평균0.031/max0.234ms였다. frame slot fence 대기를 포함한 준비 구간은 평균16.107/max27.767ms였다. 따라서 현재 기록은1억 개 순회보다 표시 주기·대기가 남은 병목임을 시사한다. wait 세부 분해나 실제 모니터 표시를 측정한 것은 아니므로, 이를 OS만의 문제로 단정하지 않는다. 동기화를 풀어 제출 FPS만 높이는 변경은 하지 않았다.

원시 자료는 로컬 `artifacts/repeated-roi-final-gpu-{1,2,3}.*`, `artifacts/repeated-roi-final-warp.*`에 있다. GUI readback에서 중앙 요약 격자·확대 개별 윤곽·수정 전 자리 제거·선택 회전 핸들을 확인했다. GPU2/3/WARP 모두 native 선택 교체, 개별 이동/회전·Undo/Redo·snapshot 취소 검사와 exit0을 확인했다. 이후 중간 버튼 더블클릭의 pan 유지도 보강했고 일반 앱 smoke로 검증했다.

## 회귀 검증

- PASS: 전체 솔루션 Release 빌드 경고0/오류0. Core275, Canvas202, Imaging272(총749) 실패/건너뜀0. Core에는 완전한 더블클릭 메시지 순서 전후 기하/이력 무변경, sparse 격자의 전수 회전 제한 대조, 선택 후보 종료 반경의 사각 허용 거리 회귀 포함. Canvas는 셰이더64바이트 상수 배치와1024/8K/24K 상세 배율을 검증했다.
- PASS: 변경 C# 파일의 whitespace 및 명명/스타일 검사, `git diff --check`.
- PASS: 실제 메인 앱 `--smoke --gpu-debug` exit0. native 선택 교체/중간 버튼 더블클릭 pan, 기존 계층·복사·이력·편집·필터·8K 라이브·렌더 재시작 회귀와 DX12 로그 확인. 기존 검사 알고리즘 성능이나 물리 입력 지연의 수용 증거가 아니다. 로그: `artifacts/repeated-main-smoke/artifacts/smoke.log`.
- FAIL(기존): 전체 `scripts/Format-Code.ps1 -Verify`는 미수정 `PreparedSearchImage.cs`, `ShapeMatcher.cs`, `TrainingPixelMask.cs`, `MaskingTests.cs`의 혼합 줄바꿈14곳에서 실패했다. 이번 범위 밖 파일을 일괄 재포맷하지 않았다.
- 테스트 호스트/formatter의 로컬 named-pipe 접근이 sandbox에서 실패하여 같은 검사를 정상 로컬 권한으로 실행했다. 테스트를 제거하거나 기준을 완화하지 않았다.
- 검증력 확인: 수정된 셀의 원래 자리를 제외하는 조건을 임시로 뒤집자 해당 Core 회귀가 실패했고 원복 후 통과했다. 잘못된 변경은 남아 있지 않다.

## 남은 범위

전체 프레임 예산, 실제 사용자 파일24K에서의 동일 부하, 물리 입력→표시 지연, 장시간·다른 화면/장비, 많은 개별 수정과 여러 창 동시 부하는 미완료다. 반복 ROI 전체 이동/회전의 계산량이 총 셀 수에 비례하지 않는다는 구조적 특성과, 모든 상황에서 동일 FPS가 나온다는 보장은 구별한다. 기존 메인 문서60FPS 및 검사CPU125ms/CUDA80ms의 미달 상태도 유지한다.
