# GPU/CPU 자동 렌더링 검증

2026-10-05. 기준: [ADR-033](decisions/0033-automatic-software-rendering.md). Windows x64, .NET10 Release, Ryzen5 5600H/32GB/RTX3060 Laptop. 현재 브랜치에서 로컬 구현·검증하며 커밋·푸시는 하지 않는다.

## 기능 검증

증거 루트는 `artifacts/warp-rendering`이다.

- `tests/device-selection.trx`: 하드웨어 없음·소프트웨어 adapter만 있음에서 Automatic의 실제 WARP 생성, Software의 하드웨어 열거 생략, 잘못된 선택 거부4개 PASS. queue/fence 완료와 열거 adapter 해제를 확인했다.
- `regression.log`: 전체949개 PASS = Core185 + Canvas117 + 앱240 + Inspection107 + Algorithm248 + CUDA52. 실패/skip0. 동일 TRX 파일명을 사용해 일부 결과가 덮어써졌으므로 전체 결과 근거는 각 프로젝트의 결과가 남은 로그다.
- `sample-cpu-smoke/output/report.json`: 실제 `Microsoft Basic Render Driver`, CPU(WARP), DX12 debug on. 8192²·24576×8192 절차적 타일·회전/계층/Undo/Redo·파일 로더·실패 원자성·overlay fence·캡처 PASS/exit0. 24K 원본 전체 버퍼를 만드는 시험이 아니다.
- `app-cpu-smoke/artifacts/smoke.log`: CPU(WARP)에서 HWND 입력·속성·회전·계층/삭제·이력·도형·필터·8K 미니맵·8K 라이브 시작/종료·렌더러 재시작 PASS/exit0. 재시작 후에도 CPU 선택 유지.
- `format-style.log`: 명시한 변경 C#7개 명명/style 검증 PASS. 전체 `Format-Code.ps1 -Verify`는 기존6개 파일의 whitespace 위반으로 FAIL. `preexisting-format-findings.json`에서 해당 파일 모두 HEAD와 동일함을 확인했으며 기존 코드를 재포맷하지 않았다.
- `build-final.log`: 최종 전체 Release 빌드 경고0/오류0. `format-whitespace-verify.log`와 `git diff --check` PASS. 새 의존성·Core/검사 참조 변경 없음.

## 성능 측정 조건

CPU 모드는 GPU가 장착된 같은 PC에서 `--software-rendering`으로 Canvas의 WARP를 강제한다. WPF 패널과 Windows 합성기는 자체 장치 선택을 사용한다. 실제 GPU 없는 별도 PC, 다른 CPU·메모리·화면 크기·RDP 환경은 검증하지 않았다.

8K 라이브는7680×4320 BGRA32 합성 원본·ROI500개·1920×1080 표시, GPU/CPU 각각30초×3회 교차 실행이다. 앞10초를 제외한다. source-ready→Present는 원본 생성·카메라 획득·물리 화면 scan-out을 포함하지 않는다. CPU 모드 GPU timestamp는 미측정(null)이며0ms가 아니다.

회전은8192² 좌표의 PCB ROI40,000개·전체 선택이며 기존 실제 입력 경로에0.1°씩180개 측정 입력과2개 준비 입력을 보낸다. GPU/CPU 각각3회 실행하며 확정·Undo/Redo 정확성과 preview 렌더 간격을 구분한다. 화면 밀도 LOD를 유지하므로40,000개 모든 윤곽의 개별 동시 표시로 해석하지 않는다.

회전 표시 크기는877.33×540 DIP, DPI1.5(약1316×810 물리 픽셀)다. 원본은8192²이며 화면 해상도와 구별한다. Debug layer는 성능 비교에서 끈다. 첫 회전 실행만 측정 입력 루프 밖에서 캡처한다. 실행 명령은 `Compare-Rendering.ps1`, 측정 DLL의 SHA256은 `measured-binary.json`에 보존했다.

## 8K 라이브 결과

원시 CSV·실제 adapter·화면 크기는 `performance/artifacts/live-*.csv`와 각 `.csv.json`, 집계는 같은 폴더의 `summary.json`에 있다. GPU는 RTX3060 Laptop, CPU는 Microsoft Basic Render Driver다. 프레임 수는 Present 제출 기준이며 물리 모니터 FPS가 아니다.

| 모드·실행 | 제출 FPS | CPU 구간 p95 ms | ready→Present p95 ms | 표시 드롭 | private 최대 MiB |
|---|---:|---:|---:|---:|---:|
| GPU1 | 59.972 | 8.648 | 10.652 | 0 | 967.7 |
| CPU1 | 53.072 | 24.173 | 21.798 | 142 | 1299.2 |
| GPU2 | 59.917 | 9.192 | 11.190 | 1 | 1098.0 |
| CPU2 | 52.363 | 24.490 | 22.289 | 157 | 1555.8 |
| GPU3 | 59.972 | 8.584 | 9.968 | 0 | 967.8 |
| CPU3 | 53.557 | 24.184 | 19.052 | 133 | 1556.5 |

집계 구간의 원본 공급은 양쪽 모두 약60FPS다. CPU는 처리보다 빨리 도착한 프레임을 기존 최신 프레임 mailbox에서 교체하므로 드롭이 발생했다. CPU로8K 표시가 동작하지만60FPS 무손실 목표를 달성한 결과는 아니다. GPU timestamp p95는7.267–7.299ms, CPU에서는null이다. 두 모드의 texture 논리 최대16.8MiB와 프로세스 private 최대는 서로 다른 값이다.

## 4만 ROI 회전 결과

원시 JSON/CSV는 `performance/artifacts/rotation-*`, 집계는 `performance/rotation-summary.json`과 `rotation-comparison.json`이다. 프레임 간격은 측정 입력 구간에 완전히 들어간 preview 렌더 프레임의 시작 간격이다. 입력 callback 시간과 구별하며 GPU/WARP 완료 대기·Present·스케줄링이 영향을 준다.

| 모드·실행 | 입력 callback p95 ms | 프레임 간격 p50 / p95 ms | 확정 완료 ms | Undo / Redo 완료 ms |
|---|---:|---:|---:|---:|
| GPU1 | 0.472 | 16.641 / 21.311 | 218.410 | 144.610 / 116.190 |
| CPU1 | 0.645 | 48.987 / 57.154 | 267.173 | 176.227 / 159.899 |
| GPU2 | 0.605 | 16.594 / 21.315 | 200.987 | 161.257 / 125.379 |
| CPU2 | 0.485 | 48.778 / 57.023 | 306.170 | 189.039 / 152.389 |
| GPU3 | 0.469 | 16.605 / 21.251 | 200.440 | 164.568 / 120.496 |
| CPU3 | 0.551 | 49.060 / 55.144 | 312.395 | 193.183 / 150.278 |

6개 실행 모두40,000개 선택/변형, 오류없음, `UndoExact`/`RedoExact=true`다. 측정 preview 중 ROI 재업로드는 모두0bytes였다. 표시된 instance 최대는22,737–23,248개로 기존 화면 밀도 표시를 유지했다. 확정·이력 완료는 CPU 작업과 UI 게시까지이며 별도 layout·최종 화면 표시를 포함하지 않는다.

CPU 회전 표시의 중앙 간격은 약49ms로 GPU의 약16.6ms보다 길다. 빠른 입력 callback만 보고 CPU에서도60FPS 회전이라고 해석하면 안 된다. 이미지 크기·화면 픽셀 수·ROI 수와 겹침에 따라 비용이 달라지며 이번 작업에서 표시 내용을 생략하거나 해상도를 낮추는 최적화는 추가하지 않았다.

## 남은 범위

실제 GPU 없는 별도 PC의 FPS 보장, 장시간8K 카메라, 24K 실파일 반복, 실행 중 장치 제거의 자동 복구, 검사125/80ms 목표는 이번 수용에 포함하지 않는다. 기능은 자동 CPU 전환을 지원하지만, 모든 구간60FPS나 GPU와 같은 성능을 보장하지 않는다.

## 실행

기본 실행에는 옵션이 필요 없다. 같은 PC에서 Canvas의 CPU 경로를 확인할 때만 다음처럼 지정한다.

```powershell
dotnet run --project samples/VisionCanvas.Sample -c Release -- --software-rendering
dotnet run --project src/TeachingToolWpf -c Release -- --software-rendering
```

코드에서 재사용할 때는 화면 표시 전에 `viewport.DevicePreference = RenderDevicePreference.Software`를 설정한다. 기본값 `Automatic`은 하드웨어 우선과 CPU fallback을 수행한다.
