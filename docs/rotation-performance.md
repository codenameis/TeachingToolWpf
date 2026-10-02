# 대량 ROI 연속 회전 성능과 남은 지연

2026-10-02. 불변 회전 미리보기, BVH refit, 최초 변형 집합의 비동기 준비, 최신 포인터 병합, 준비/확정 실행 순서 보호, FrozenSet 기반 대상 집합을 적용했다. **반복 회전 입력은 크게 가벼워졌지만 최초 부분 선택 준비와 대량 확정·Undo/Redo까지 무지연이 된 것은 아니다.**

이 문서의 최종 근거는 [release 요약](../artifacts/rotation-release-summary.json)과 [원시 자료 묶음](data/rotation-measurements.zip)이다. 묶음에는 JSON/CSV/캡처, smoke 로그, 실행 스크립트와 바이너리 해시가 있다. `before`는 최적화 전, `after`는 불변 미리보기 적용 후·refit 전, `final`은 refit 완료·최초 준비 비동기화 전, `ready`는 첫 비동기 준비, **`release`는 이번 최종 suite**다. 파일 이름의 `final`을 최신 결과로 혼동하지 않는다. 요약의 `File`, `Render.Source`, `Metrics.Source`는 각 원본을 가리킨다.

## 조건과 통계의 의미

- Ryzen 5 5600H, RAM 32GB, 실제 NVIDIA GeForce RTX 3060 Laptop GPU, VRAM 5.9GiB/budget 5.1GiB, Windows x64/.NET 10 Release. 성능 실행은 DX12 debug off, 별도 debug 검증은 on이다. 노트북 전력·클럭은 고정하지 않았고 사용자 debug 앱이 별도로 실행 중이었다. 완전히 격리된 장비 측정이 아니다.
- 고정 8192×8192 합성 이미지, GPU 뷰포트 877.33×540 DIP, DPI 150%, 화면 밖 실제 HWND다. 8K 실시간 카메라 동시 부하나 물리 마우스→모니터 지연 시험은 아니다.
- 평면/PCB fixture의 `interior`는 크기와 중심 간격을 절반으로 줄여 이미지 중앙에 배치한다. `boundary`는 원래 전체 이미지 배치다. `parents`는 PCB 루트 22,223개를 선택해 자손 포함 20만 개를 변형하고, `half`는 4만 중 생성 순서 앞 2만 개를 선택한다.
- 실제 모델의 회전 핸들 `Down → Move → Up`을 호출한다. 시작점 기준 0.1°씩 요청하고 입력 사이에 `Task.Delay(16)`을 둔다. 일반 실행은 워밍업 2회+측정 180회, 최종 요청 18.2°다. 정확한 16ms 주기를 보장하지 않는다. release PCB 부모 선택은 독립 프로세스 3회, 나머지 배치는 각 1회다. 별도 RTX debug 실행은 4만/60회다.
- 기존 schema 1은 워밍업 2회+측정 10회, 최종 요청 1.2°이며 매 sample 뒤 JSON을 저장했다. schema 2는 루프 이후 저장한다. 각도 범위·표본 수·I/O 조건이 다르므로 전후 FPS/프레임 간격을 같은 조건의 개선 배율로 계산하지 않는다. 백분위는 nearest-rank이며 3회 범위는 실행별 백분위의 최솟값–최댓값이다.
- `Measured`는 측정 입력 전체, **`DisplayedMeasured`는 callback에서 모델 preview가 관측된 입력만** 포함한다. 이름과 달리 모니터 표시 완료를 뜻하지 않는다. `WaitingMeasuredSamples`는 준비 중인 경량 입력이다. 아래 반복 회전 비용 표는 `DisplayedMeasured`를 사용해 대기 중 요청을 빠른 회전으로 집계하지 않는다.

## 반복 회전 입력

시간은 ms, 할당은 callback 하나의 UI 스레드 할당이다. 전체 프로세스/작업 스레드/GPU 메모리와 다르다. MB는 10⁶바이트다.

| 배치 | 기존 callback p50 / p95 | release callback p50 / p95 | 기존 → release 할당 p50 | preview 관측 / 대기 입력 |
|---|---:|---:|---:|---:|
| 4만 평면 interior | 202.599 / 248.068 | 0.246 / 0.351 | 38.535MB → 3,560B | 180 / 0 |
| 4만 평면 boundary | 2,971.331 / 3,129.146 | 0.005 / 0.022 | 1,367.375MB → 40B | 180 / 0 |
| 20만 평면 interior | 688.700 / 1,108.719 | 0.192 / 0.337 | 183.172MB → 2,832B | 180 / 0 |
| 40만 평면 interior | 미측정 | 0.191 / 0.288 | 2,832B | 180 / 0 |
| 20만 PCB 부모 선택, 3회 | 미측정 | 0.174–0.215 / 0.249–0.326 | 2,784–3,616B | 각 178 / 2 |
| 4만 중 2만 선택 | 미측정 | 0.220 / 0.363 | 3,656B | 171 / 9 |

boundary는 첫 이미지 경계에 닿은 각도로 제한되며 이후 같은 요청 결과에서는 형상/장면이 바뀌지 않는다. 따라서 180회 서로 다른 회전 화면을 그렸다고 해석하지 않는다. release callback 최대는 4만 interior 0.496ms, 20만 0.494ms, 40만 0.376ms, PCB 부모 선택 1.814ms였다. 측정 입력의 Dispatcher 간격 최대는 일반 20만에서 37.48ms까지 관측됐다. 짧은 callback을 같은 크기의 표시 지연으로 표현하지 않는다.

## 최초 회전 준비: UI 반환과 첫 preview 구분

첫 callback은 워밍업에 속하므로 위 표에 포함되지 않는다. 최초 부모 집합 준비를 작업 스레드로 옮기고 기다리는 동안 최신 포인터 하나를 보관한다. 결과는 문서/선택/preview 세대와 실행 순서를 확인한 뒤 게시한다.

| 20만 PCB 부모 선택 | refit 완료·동기 준비(`final`) | 첫 비동기 준비(`ready-r1`) | release, FrozenSet·실행 순서 보호 포함 3회 |
|---|---:|---:|---:|
| 첫 callback 반환 | 358.607ms | 3.231ms | 3.385–4.006ms |
| 첫 callback UI 할당 | 38.732MB | 1,464B | 2,376–2,440B |
| 첫 callback 뒤 WPF layout | 별도 원시 자료 | 4.823ms / 1.039MB | 3.980–5.315ms / 1.039–1.376MB |
| 해당 sample Dispatcher gap | 359.285ms | 20.745ms | 20.163–22.091ms |
| 첫 preview 프레임 CPU 기록 종료 | 회전 단계 timestamp 별도 자료 | 331.610ms | **108.022–112.024ms** |

첫 preview 시간의 원점은 첫 Move sample 시작이다. release PCB의 첫 프레임 시작은 103.244–107.555ms, CPU draw 기록 종료(`RenderPreparationEndMs`)는 108.022–112.024ms였다. 모델 preview를 sample에서 처음 관측한 시점은 index 5 callback 종료, 122.827–124.826ms다. 비동기 게시가 입력 사이에 발생하므로 렌더와 sample의 관측 시점은 다르다. **이 시간은 Present 완료·물리 화면 표시 지연이 아니다.** sample 수×16ms로 준비 시간을 추정하지 않는다.

`ready-r1`은 sample 1–13에서 preview가 없었고 첫 관측 index 14의 상한은 358.113ms였다. release는 sample 1–4에서 preview가 없어 측정 입력 중 2개가 대기 요청이다. FrozenSet으로 대상 집합을 구성하는 변경과 실행 순서 보호를 함께 적용한 비교이며, 작업 스레드 전체 할당을 별도로 측정하지 않아 UI 할당 수치로 그 메모리 감소율을 계산하지 않는다.

전체 선택의 첫 preview CPU 기록 종료는 4만 5.591ms, 20만 6.383ms, 40만 6.753ms였다. 반면 **4만 중 2만 선택은 306.407ms**가 걸렸고 측정 입력 중 9개가 준비 대기였다. 첫 callback 자체는 3.958ms였지만 layout 4.993ms/1.376MB가 추가됐다. 앱 시작·fixture 생성·선택/그룹 명령 자체는 모두 이 시간 밖이다.

## 실제 렌더 프레임

공통 Stopwatch ticks로 첫 측정 입력 시작부터 마지막 layout 종료+16ms까지 완전히 포함된 `transform_preview=1` 프레임만 집계한다. 초기 preview 관측은 이 구간 밖의 워밍업까지 포함해 따로 찾는다. schema 1은 phase 정보가 없으므로 회전 렌더 통계에서 제외한다. 입력/렌더 요청 합치기와 라벨 준비 때문에 프레임 수는 입력 수와 같지 않다.

| release 배치 | 집계 프레임 | paint p50 / p95 | prepare+paint+submit p50 / p95 | 프레임 시작 간격 p50 / p95 | 간격 최대 |
|---|---:|---:|---:|---:|---:|
| 4만 interior | 304 | 2.387 / 2.839 | 16.249 / 20.457 | 16.629 / 21.111 | 28.674 |
| 20만 interior | 318 | 2.365 / 3.016 | 16.142 / 20.436 | 16.612 / 20.957 | 32.665 |
| 40만 interior | 312 | 2.347 / 3.008 | 16.086 / 20.383 | 16.556 / 21.215 | 26.823 |
| 20만 PCB 부모, 3회 | 308–316 | 2.599–2.623 / 3.358–3.463 | 16.079–16.135 / 20.435–20.593 | 16.554–16.578 / 20.939–21.079 | 31.860 |
| 4만 중 2만 선택 | 303 | 2.407 / 2.926 | 16.197 / 20.057 | 16.680 / 21.212 | 29.336 |

전체 선택/PCB 부모 선택/경계/CPU 제한의 측정 구간 ROI 재업로드 합계는 **0B**였다. 부분 선택은 준비 대기 시간이 측정 구간에 들어가며, **첫 preview 프레임 하나(profile frame 20, 각도 1.1°)에서 210,080B**를 업로드했다. 이후 preview 프레임 업로드는 0B로, 매 Move마다 전체 형상을 재업로드한 결과가 아니다. 초기 생성·선택·확정 후 형상 업로드를 포함해 모든 상황에서 0이라고 일반화하지 않는다. boundary는 변형이 곧 멈춰 집계 프레임이 10개뿐이므로 연속 회전 표에서 제외했다.

`prepare`는 프레임 시작/리소스 준비의 벽시계 시간이며 fence 대기를 포함할 수 있다. `paint`는 CPU 화면 요소 준비, `submit`은 CPU 명령/instance 준비다. 합계를 순수 CPU 실행 시간으로 부르지 않는다. `Present`는 profile 기록 뒤 별도로 실행되고, 프레임 시작 간격에는 이전 Present·대기·스케줄링이 반영된다. GPU timestamp와도 다른 값이다.

기존 화면 밀도 LOD는 유지한다. instance 최대는 4만 interior 13,515개, 20만 10,470개, 40만 10,892개, PCB 부모 선택 34,498–34,543개였다. 화면에 약 1만 개를 그린 결과를 전체 20만/40만 윤곽의 개별 동시 표시로 표현하지 않는다. 정확한 도메인 형상·히트·부모 관계·선택·Undo/Redo 수를 줄인 것은 아니다.

GPU metrics는 공통 구간 timestamp가 없어 다음처럼 **초기화·워밍업·회전·확정·이력을 포함한 전체 실행**으로 별도 표시한다.

| 전체 실행 metrics | 프레임 수 | GPU p50 / p95 | Present p95 | dropped counter 최대 |
|---|---:|---:|---:|---:|
| release 4만 interior | 349 | 0.938 / 2.430ms | 0.390ms | 0 |
| release 20만 interior | 359 | 1.846 / 4.521ms | 0.391ms | 0 |
| release 40만 interior | 357 | 1.784 / 4.918ms | 0.440ms | 0 |

고정 이미지에서 dropped=0은 라이브 무손실 처리를 검증하지 않는다. 별도 [4만 완전 중첩 pan 진단](../artifacts/rotation-overlap-40k.json)은 회전 시험이 아니다. UI callback p50은 미선택/전체 선택 0.170/0.130ms였고 전체 실행 GPU p50은 1.058ms, 프레임 간격 p50은 30.864ms였다. 당시 입력 간 30ms 대기가 포함된 값이므로 약 31ms 간격을 GPU 연산 병목으로 해석하지 않는다.

## 확정·Undo/Redo와 CPU 제한

`dispatch`는 요청 반환, `complete`는 비동기 준비와 UI 게시 완료까지이며 이후 별도 WPF layout은 포함하지 않는다. 작업당 한 번의 실행이고 PCB만 3회 범위다. 연속 회전 callback p95와 섞지 않는다.

| release 배치 | 확정 dispatch / complete | Undo complete / Redo complete | 확정 / Undo / Redo 최대 Dispatcher gap |
|---|---:|---:|---:|
| 4만 interior | 3.48 / 228.64ms | 169.71 / 149.07ms | 34.40 / 31.04 / 31.00ms |
| 20만 interior | 3.56 / 725.82ms | 471.17 / 449.34ms | 79.84 / 87.58 / 57.49ms |
| 40만 interior | 3.64 / 1,636.66ms | 984.40 / 828.13ms | 126.21 / 113.64 / 111.50ms |
| 20만 PCB 부모, 3회 | 5.86–6.50 / 1,188–1,265ms | 811–837 / 733–841ms | 50.98 / 51.50 / 40.96ms |
| 4만 중 2만 선택 | 7.03 / 1,039.04ms | 838.53 / 284.98ms | 41.25 / 32.37 / 32.45ms |

40만 Undo dispatch는 16.19ms였다. PCB 확정 완료는 refit 직후 중간 실행 1,012.70ms보다 release에서 길었다. 일반 20만 Undo gap도 그 중간 최대 64.81ms에서 release 87.58ms로 커졌다. 모든 작업과 꼬리 지연이 개선되었다고 주장하지 않는다.

프로세스 전체를 logical CPU 0 하나로 제한한 **release 20만 단일 실행**의 preview callback p50/p95는 0.308/0.503ms, 할당 p50 4,400B, 회전 렌더 간격 p95 21.737ms였다. 확정 dispatch/complete는 3.65/1,212.30ms, Undo/Redo 완료는 1,082.43/873.85ms다. **최대 Dispatcher gap은 확정 111.15ms, Undo 139.63ms, Redo 103.19ms**였다. 같은 RTX를 사용하므로 저사양 CPU의 캐시/메모리나 저사양 GPU를 재현한 것은 아니다. refit 전 4만 단일 CPU 결과는 [중간 요약](../artifacts/rotation-onecpu-summary.json)에 남기고 최종 표와 합치지 않았다.

## 중간 측정으로 확인한 병목

[refit 전](../artifacts/rotation-commit-200k-before-refit.json)/[refit 후](../artifacts/rotation-commit-200k-after-refit.json) CPU 프로파일은 같은 미리 준비된 20만 PCB 소스에 18° 회전, 워밍업 2회+측정 3회다. 평균 합계는 **1,191.71→545.04ms**, 공간 인덱스는 **757.45→153.66ms**, 형상 map/snapshot 적용은 339.68→296.56ms였다. UI 게시·GPU·Undo를 제외한 별도 CPU 진단이므로 release 앱 complete와 직접 비교하지 않는다.

refit 전 `after`의 확정 complete는 4만 548–563ms, 20만 1,328–1,413ms, 40만 2,986ms였다. refit 직후 `final`의 4만/20만 평면 3회 기록은 [중간 최종 요약](../artifacts/rotation-final-summary.json)에 보관한다. 최초 준비를 미리 데워 둔 과거 수치, 새 집합을 만드는 수치, 비동기 준비 후 반복 입력을 분리해야 한다.

## 검증과 재현

통합 테스트 **Core 158개+Imaging/WPF 69개, 총 227개**와 Release 솔루션 빌드(경고·오류 0)가 통과했다. release suite 10개는 모두 종료 0, 오류 null, `UndoExact`/`RedoExact` true였다. 기록된 ID·형상·이름·부모·필터·선택 복원 검사다. 실제 RTX의 release 4만 ROI DX12 debug 60회와 캡처도 통과했다. 최종 Release `--smoke --gpu-debug` 역시 종료 0으로 native 입력·툴바 ±15°와 Undo·그룹·이력·미니맵·OpenCV 회전 미리보기/취소·이미지/라이브·renderer 재시작을 확인했다. [로그](../artifacts/smoke.log)는 원시 묶음에도 있다. 물리 입력 E2E나 현장 카메라 장시간 수용 시험을 대신하지 않는다.

[40만 preview](../artifacts/rotation-release-400k-interior.preview.png), [40만 확정](../artifacts/rotation-release-400k-interior.committed.png), [4만 DX12 debug](../artifacts/rotation-release-40k-debug.preview.png)는 정지 화면 확인 자료다. 캡처는 입력 루프 밖에서 실행했고 release 40만/debug에만 사용했다.

```powershell
$app = Join-Path (Get-Location) 'artifacts/rotation-release/TeachingToolWpf.exe'
$runArgs = '--roi-rotation-benchmark --rois 200000 --rotation-samples 180 --rotation-warmup 2 --rotation-step .1 --rotation-layout pcb --rotation-mode interior --rotation-selection parents --report artifacts/rotation-release-200k-pcb-parents-1.json --metrics artifacts/rotation-release-200k-pcb-parents-1.csv --render-profile artifacts/rotation-release-200k-pcb-parents-1-render.csv'
$process = Start-Process $app -ArgumentList $runArgs -WindowStyle Hidden -PassThru
# 별도 CPU 제한 실행에서만 초기 준비가 끝나기 전에 설정:
# $process.ProcessorAffinity = [IntPtr]1
$process.WaitForExit()

./scripts/Summarize-RoiRotation.ps1 -Path 'artifacts/rotation-release-*k-*.json' -IncludeMetrics -Format Json
# 빠르게 입력 통계만 확인:
./scripts/Summarize-RoiRotation.ps1 -Path 'artifacts/rotation-release-*k-*.json' -SkipRender
```

PCB 실행 번호를 1/2/3으로 바꾸어 반복한다. `--rois 40000|400000`, `--rotation-layout flat --rotation-selection all|half`, `--rotation-mode boundary`, `--capture-rotation`으로 다른 배치를 만든다. 재배포 시 바이너리와 원시 자료를 별도 보관한다.

남은 범위는 첫 부모/부분 선택 preview 준비 약 0.11/0.31초, 큰 확정·이력의 100ms 이상 UI 간격, 최초 그룹/목록 준비, 장시간 반복, 실제 8K 라이브 동시 부하, 현장 PCB, 전원 조건을 통제한 저사양 장비와 물리 입력 E2E다. 현재 측정으로 무지연·모든 구간 60FPS 달성을 선언하지 않는다.
