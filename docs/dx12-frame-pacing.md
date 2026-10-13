# DX12 표시 대기와 최신 입력 — 국소 개선

2026-10-13, `main` 로컬 변경. 사용자는 DX12 활용을 우선하고 수정 범위를 작게 유지하며, 전체 구조 변경이 필요할 때 먼저 질문하도록 요청했다. 이번 변경은 기존 렌더 구조 안에서 수행했고 설계 원칙 예외·커밋·푸시는 없다. [1억 ROI 기능과 이전 실측](repeated-roi-validation.md)은 유지한다.

## 원인과 변경

같은 RTX3060 Laptop GPU에서 100,020,001개 반복 ROI의 확대 수평 이동을 계측했다. 변경 전 GPU 계산 평균은3.79ms, frame slot fence 대기는16.01ms였다. 장면을 대기 전에 가져와서 그리기 시작 시 평균21.68ms 전 상태를 사용했다. GPU 처리량의 한계라고 판단할 근거가 없었다.

생산 코드 변경은 `Dx12Surface`, `RenderWorker`, `FrameMetrics`의3파일이다. DXGI `FrameLatencyWaitableObject`와 최대 대기 프레임1을 사용해 표시 준비를 먼저 기다리고, 슬롯 fence 확인 후 최신 불변 장면을 가져온다. `Present(1)`, 두 back buffer, 슬롯별 allocator/업로드 수명은 유지한다. 정적 화면은 기존처럼 렌더를 생략한다. [Microsoft의 표시 지연 절차](https://learn.microsoft.com/en-us/windows/uwp/gaming/reduce-latency-with-dxgi-1-3-swap-chains)에 따른 순서다.

대기 후 종료와 최신 화면 크기를 재확인한다. 최소화 중 이미 얻은 표시 허가는 다음 유효 프레임까지 보존하고 `Present` 후 소비한다. ResizeBuffers에도 동일 flag를 유지한다. Surface 생성 실패 시 부분 생성 GPU 자원과 대기 핸들을 정리하며, DXGI가 반환한 핸들은 Surface가 소유·해제한다. [핸들 수명 계약](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject)을 따른다.

## 비교 조건과 결과

기존 반복 ROI 벤치마크의 입력·기하·워밍업1초/측정4초·10구간을 그대로 사용했다. 8192² PCB, viewport1757×1048 physical 상당(DIP1171.33×698.15, DPI1.5), 수정 셀1개, GPU debug OFF. 빌드·테스트와 성능 실행은 겹치지 않았다. `baseline`은 동일한 fence/장면 경과/DXGI 계측을 넣은 변경 전 루프, `waitable-1`은 첫 후보, `waitable-2`는 수명·최소화 보완 후 실행이다. 초기 빌드와 겹쳤던 `dx12-before` 실행은 비교에서 제외했다.

| 연속 입력 | 제출 간격 p95 전→후(ms) | 그리기 시작 시 장면 경과 평균 전→후(ms) | 변경 후 최대 제출 간격(ms) |
|---|---:|---:|---:|
| 화면 맞춤·수평 | 18.67 → 17.58 | 20.15 → 6.51 | 28.37 |
| 화면 맞춤·수직 | 18.31 → 17.20 | 20.16 → 3.88 | 20.51 |
| 화면 맞춤·대각 | 18.33 → 17.08 | 20.28 → 4.66 | 18.30 |
| 화면 맞춤·회전 | 18.24 → 17.06 | 20.12 → 3.88 | 18.35 |
| 확대·수평 | 18.07 → 17.16 | 21.68 → 4.40 | 17.73 |
| 확대·수직 | 18.56 → 17.25 | 22.29 → 3.49 | 17.70 |
| 확대·대각 | 17.86 → 17.19 | 20.87 → 3.58 | 18.04 |
| 확대·회전 | 18.22 → 17.29 | 22.23 → 5.10 | 17.97 |
| 개별 이동 | 18.10 → 17.20 | 21.45 → 3.92 | 17.56 |
| 개별 회전 | 18.44 → 17.20 | 22.20 → 5.01 | 17.59 |

표는 baseline2,401프레임과 waitable-2의2,402프레임이다. 변경 후 평균 제출 FPS는59.81~60.00, GPU 최대7.37ms, 입력 함수 p95 최대0.0439ms, private bytes 표본 최대294.5MB,4초 구간 GC pause 최대3.63ms였다. 첫 후보2,400프레임의 제출 간격 p95는17.15~17.25ms, 장면 경과 평균2.90~5.10ms, 최대 제출 간격30.86ms였다. 후보의 첫 화면 맞춤 구간에서 긴 제출 간격이 여전히 발생한다. 최악 시간이 항상 개선됐다고 주장하지 않는다.

기준을 바꾸지 않고 **엄격한 전체 프레임16.67ms 이내 조건은 FAIL**로 남긴다. 한편 세 실행 모두 DXGI 표시/refresh/QPC 카운터 기반 주기는 약59.997~59.999FPS였고, 연속 표시 표본 사이 `refresh 증가 - present 증가`의 양수 합은0이었다. 이는 계측 구간에서 표시 refresh 누락이 관측되지 않았다는 별도 증거다. 이전 루프도0이므로 이번 변경으로 표시 드롭이 제거됐다고 주장하지 않는다. 제출 시점의 흔들림과 표시 카운터를 구분하며, 물리 화면 촬영이나 마우스→픽셀 지연 측정으로 표현하지 않는다. [DXGI 통계 필드](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_frame_statistics).

`scene_age_ms`는 장면이 렌더러에 게시된 시점부터 그리기 시작까지다. UI 입력 생성의 간격도 반영하므로 물리 입력 지연이 아니다. `cpu_ms`에는 `fence_wait_ms`와 `pacing_wait_ms`가 포함된다. 대기가 fence에서 표시 준비로 이동했으므로 fence만 짧아진 것을 CPU 계산량 감소로 해석하지 않는다. 새 DXGI 통계 조회는 `--metrics` 실행에만 적용한다.

추가 `dx12-waitable-final`은 같은 대기 프레임1 코드의 최종 빌드로 profile 없이 metrics만 기록했다. 확대/개별 편집은59.99~60.01FPS, 전체 구간 제출 간격 p95 17.09~17.54ms, 장면 경과 평균3.38~4.77ms였다. 첫 화면 맞춤 이동에서는59.59FPS/최대32.11ms가 나왔고 DXGI 카운터 차이도1회 관측됐다. 다른9구간의 카운터 차이는0이다. 따라서 앞선 세 실행의0회를 모든 실행에서 누락이 없다는 보장으로 일반화하지 않는다. 최악 프레임의 GPU 계산0.58ms·표시 준비 대기30.18ms를 기록했다.

GPU도 최대 대기 프레임2로 추가 비교했다(`dx12-gpu-latency-2`, metrics만 기록). p95 17.06~17.48ms였지만 첫 이동 최대29.55ms와 양수 카운터 차이1회가 남았다. 이 단일 비교로 일관된 개선이 확인되지 않았고 대기열도 늘어나므로 적용하지 않았다. 최종 생산 코드는 다시 최대 대기 프레임1이며, 실험 수치를 보관한다.

인접 `TeachingToolDioxus/UPDATE_V5.md`, `UPDATE_V9.md`도 읽기 전용으로 확인했다. 웹은RAF, native는 별도 프레임 계측이며 viewport·DPR·기하가 다르다. V9 native의 대기 완화는 참고했지만 GPU 슬롯 안전 fence를 제거하지 않았다. 두 앱의 서로 다른 수치를 DX와 웹의 우열 비교로 사용하지 않는다.

## 재현과 검증

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore -m:1
& .\src\TeachingToolWpf\bin\Release\net10.0-windows\TeachingToolWpf.exe --repeated-roi-benchmark --roi-report artifacts/dx12-check.json --metrics artifacts/dx12-check.csv --render-profile artifacts/dx12-check-profile.csv
pwsh -File scripts/Measure-RepeatedRoi.ps1 -Prefix artifacts/dx12-check
dotnet test tests/VisionCanvas.Tests/VisionCanvas.Tests.csproj -c Release --no-restore
```

원시는 로컬 `artifacts/dx12-baseline.*`, `artifacts/dx12-waitable-{1,2}.*`. [보관 집계](dx12-frame-pacing-results.json)는 각 입력 구간의 FPS·간격·GPU·대기·장면 경과·메모리·GC와 카운터를 포함한다. 통계가 없거나 카운터가 역행하면 표시 주기를 null로 두고 추측하지 않는다. 과거 CSV의 추가 계측도 null이며 기존 Strict60Fps 판정은 유지한다.

- PASS: 전체 Release 빌드 경고0/오류0. Canvas203개 실패·건너뜀0. 기존 재시작/종료 테스트와 새 WARP 크기 변경→최소화→복원→캡처 크기→즉시 종료 회귀 포함.
- PASS: 변경4개 C# 파일 whitespace 및 명명/스타일 검사, `git diff --check`. 기존 전체 format 실패4파일/14줄은 이번에 수정하지 않았다.
- PASS: 메인 앱 `--smoke --gpu-debug` exit0. native 편집·계층·Undo/Redo·설정·미니맵·필터·8K 라이브·renderer 재시작/캡처 회귀. DX12 debug 오류 로그 없음. `artifacts/dx12-pacing-smoke/artifacts/smoke.log`.
- PASS: WARP `--software-rendering --gpu-debug`에서 반복 ROI 선택 교체·개별 편집·이력·취소와 종료. GPU 시간을 null로 유지한다. 캔버스 수명 테스트도 WARP로 실행했다.
- PASS: 집계기의 카운터98→99→100→101 경계, 역행 시 null, 추가 진단 누락 시 null 확인. CSV 문자열 정렬로 시간순서가 바뀌지 않도록 처리한다.

WARP에서는 화면 맞춤59.74~60.06FPS, 확대35.75~36.06FPS, 개별 편집32.64~33.49FPS였다. 과거 개별 편집35.37~35.75FPS보다 낮으므로 WARP 성능 퇴행이 없다고 선언하지 않는다. 최대 대기 프레임2도 실험했지만 개별 편집30.96~32.22FPS로 이득이 없어 적용하지 않았다(`artifacts/dx12-waitable-warp-2.*`). 최종 값은GPU/WARP 모두1이다. WARP 확대/개별 편집은60FPS 미달이며 하드웨어 목표와 구별한다. 이 실험은 GPU 드라이버를 바꾸거나 소프트웨어 fallback을 제거한 것이 아니다.

## 원칙 검토

| 원칙 | 검토 |
|---|---|
| SRP | Surface 자원·Worker 실행·Metrics 계측 책임 유지 |
| OCP | 기존 일반/반복 ROI draw 경로와 공개 계약 유지 |
| LSP | 대체 구현 계약 변경 없음; WARP 수명 회귀 확인 |
| ISP | 새 공개 소비자 인터페이스 없음 |
| DIP | Core에 DXGI 의존성 추가 없음 |
| Information Expert | swap chain 준비 신호/통계는 Surface에서 제공 |
| Creator | Surface가 GPU 자원과 신호 핸들 생성·실패 정리·해제 소유 |
| Controller | Worker가 최신 제출·크기·종료를 조정 |
| Low Coupling | 편집 모델·입력·검사로 변경 전파 없음 |
| High Cohesion | 기존 렌더 수명/진단 경계 안에서 수정 |
| Polymorphism | 새 대체 구현이나 플랫폼 분기 도입 없음 |
| Pure Fabrication | 대기/계측 기술 책임을 도메인 밖에 유지 |
| Indirection | 불변 RenderScene 게시 경계 유지 |
| Protected Variations | GPU 완료 fence·WARP·resize·종료 계약 보존 |

읽기 전용 독립 리뷰에서 생성 실패·동시 종료·resize 위험을 찾아 보완했고 재검토했다. 임의24K 사용자 파일, 장시간, 다른 모니터·장비, 많은 개별 수정, 메인 문서의 모든 부하에 대한60FPS 보장은 아직 검증하지 않았다. 전체 구조 변경 없이 확보한 입력 경과/제출 간격 개선과 남은 엄격한 프레임 예산을 구별한다.
