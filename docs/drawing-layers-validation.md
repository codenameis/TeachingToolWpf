# 이름 있는 표시 레이어 검증 — 2026-10-06

이 문서는 최초 레이어 구현 시점의 기록이다. 이미지 중심·전체 Canvas 크로스헤어와 ConfigPage 후속 검증은 [최신 기록](configuration-validation.md)을 따른다.

기준: `main`의 `742a87e`에서 만든 `codex/named-drawing-layers`. 사용자가 승인한 [ADR-034](decisions/0034-named-drawing-layers.md), [사용 가이드](drawing-layers-guide.md)를 따른다. 커밋·푸시 없이 로컬 검증한다.

## 기능과 현재 증거

- PASS: ID 기반 Add/Update/Remove/Clear, 추가 순서, 레이어 독립성, 실패 원자성, 이전 불변 스냅샷, UI 스레드 확인, 입력 검증.
- PASS: 이미지 좌표 변환, 화면 중앙 고정/크기 변경/DPI, 숨김, 윤곽 준비 재사용, 선 클리핑, 한 quad의 드래그 채우기, Mouse Up/취소와 단일 이력.
- PASS: 기존 검사 표시를 기본 Inspection 레이어로 연결, Clear 후 탐색 시 재표시 방지, 새 검사 결과 교체, 기존 ROI·이력 유지.
- PASS: RTX 3060 Laptop, DX12 debug True의 독립 샘플. 8K/24K 타일·원본 수명·실제 검사 overlay fence와 레이어 캡처, 중앙선의 픽셀 위치/숨김·pan/zoom/resize·재시작, PNG 파일 입력. exit 0, 렌더 오류 없음.
- PASS: CPU(WARP), Microsoft Basic Render Driver, DX12 debug True에서도 같은 독립 샘플 검증. exit 0, 렌더 오류 없음. 별도 저사양 PC 시험과 구별한다.
- PASS: 전체 Release 빌드 경고 0/오류 0. 전체 **1,061개** 통과, 실패/건너뜀 0(Core 234 / 앱 249 / Canvas 171 / Algorithm 248 / Inspection 107 / Compute 52).
- PASS: 전체 `scripts/Format-Code.ps1 -Verify`의 명명·서식·style 검사. 새 설계 원칙 예외나 분석기 억제 없음.
- PASS: 실제 TeachingToolWpf 앱의 RTX3060 debug smoke. ROI 도구·계층·그룹·이력·렌더 재시작·미니맵·OpenCV 필터·8K 라이브 시작/종료, exit 0.

로그: `artifacts/named-layers`, 보관본: [증거 ZIP](data/named-drawing-layers-20261006.zip). 레이어 기능은 새 테스트 15개와 기존 표시·수명 회귀로 확인한다. 예제 smoke는 합성 8192²/24576×8192와 작은 PNG를 사용한다. 실제 사용자 문서·카메라·가시창 물리 입력·모니터 scan-out·장시간·실제 저사양 PC 성능은 NOT_RUN이다. 선택형 .NET 분석기의 기존 [미완료 감사](release-quality-review.md)는 이번 일반 빌드/컨벤션 통과로 완료 처리하지 않는다.

## 성능 측정 방법

독립 샘플의 동일 장면에서 같은 드래그 테두리를 갱신하며 채우기 OFF/ON/OFF/ON을 교대로 실행한다. 각 구간 20회 입력 이후 120회 입력을 기록하고 `fill-phases.json`의 Stopwatch 구간으로 metrics/render CSV를 자른다. 16ms Task.Delay는 입력 생성 간격이며 정확한 60Hz나 모니터 표시를 의미하지 않는다. 정적 화면/캡처 대기 구간은 집계에 포함하지 않는다. debug 계층을 켠 기능 진단이며 장비별 전체 편집 성능 수용 시험은 아니다.

기준 장비는 Ryzen 5 5600H / RAM 32GB / RTX 3060 Laptop 6GB다. 캡처는 1724×1025 물리 픽셀이고, 측정 드래그 범위는 화면 가장자리 약 30 DIP를 제외한 넓은 사각형이다. 원본은 8192² 합성 이미지, 문서 ROI는 3개다. 아래는 **각 모드 한 프로세스 안의 두 반복**이며 독립 실행 통계가 아니다. `fill-summary.json`에 구간별 원시 집계가 있다.

| 표시 장치 / 구간 | 채우기 | 렌더 CPU 중앙값 / p95(ms) | GPU 중앙값 / p95(ms) | 프레임 간격 중앙값 / p95(ms) |
|---|---|---|---|---|
| RTX / 1 | OFF | 0.559 / 0.936 | 0.050 / 1.436 | 17.141 / 31.669 |
| RTX / 2 | ON | 0.476 / 0.726 | 2.082 / 6.061 | 21.509 / 30.074 |
| RTX / 3 | OFF | 0.466 / 0.651 | 1.382 / 3.510 | 16.854 / 30.477 |
| RTX / 4 | ON | 0.458 / 0.709 | 3.658 / 6.429 | 16.875 / 31.413 |
| WARP / 1 | OFF | 0.474 / 0.742 | 미측정 | 22.882 / 27.723 |
| WARP / 2 | ON | 0.398 / 0.559 | 미측정 | 17.628 / 30.147 |
| WARP / 3 | OFF | 0.392 / 0.590 | 미측정 | 17.670 / 31.232 |
| WARP / 4 | ON | 0.381 / 0.628 | 미측정 | 17.142 / 31.970 |

모든 구간에서 입력 120회/렌더 기록 120개다. 채우기 ON은 정점 중앙값 420→426으로 quad 한 개를 추가한다. 안정된 후반 OFF/ON의 프레임별 CPU 할당 중앙값은 모두 5,448 bytes이며 전체 렌더 경로의 값이다. 채우기 자체가 이 전체 할당을 만들었다는 뜻은 아니다. CPU 시간의 뚜렷한 증가를 관찰하지 못했지만 **GPU 시간은 ON 구간에서 증가했으므로 비용이 없다고 할 수 없다.** WARP의 CPU 제출 시간은 소프트웨어 래스터화 전체 시간도 아니다.

프레임 간격 p95는 양쪽 모두 16.67ms를 넘으며 입력 생성 스케줄링과 Present 대기를 포함한다. 따라서 이번 결과는 60FPS 통과 근거가 아니다. 이전 대량 편집 FAIL을 유지하고, 채우기 면적과 실제 입력/표시 부하에 따른 비용을 별도로 판단한다.

## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
dotnet test TeachingToolWpf.slnx -c Release --no-build --no-restore
pwsh -NoProfile -File scripts/Format-Code.ps1 -Verify
dotnet run --project samples/VisionCanvas.Sample -c Release --no-build -- --smoke --gpu-debug --output artifacts/layers-gpu --metrics artifacts/layers-gpu/metrics.csv --render-profile artifacts/layers-gpu/render.csv
dotnet run --project samples/VisionCanvas.Sample -c Release --no-build -- --smoke --gpu-debug --software-rendering --output artifacts/layers-warp --metrics artifacts/layers-warp/metrics.csv --render-profile artifacts/layers-warp/render.csv
```

각 smoke는 새 출력 폴더를 사용한다. GPU와 WARP 측정은 동시에 실행하지 않는다. `metrics.csv`는 `stopwatch_record_ticks`, `render.csv`는 `stopwatch_start_ticks`/`stopwatch_end_ticks`가 phase 시작/끝 범위 안인 행만 집계한다. GPU timestamp는 기존 비동기 회수 계측이므로 구간 경계의 엄밀한 동일 프레임 대응이나 물리 표시 완료로 해석하지 않는다.

## SOLID·GRASP 검토

| 원칙 | 판단 |
|---|---|
| SRP | 레이어 내용/검증, 표시 변환, Canvas 소유권, 입력 초안을 기존 책임 안에서 분리 |
| OCP | 기존 호스트/검사 계약을 유지하고 명시적 그래픽 타입을 추가. 사용처 없는 플러그인 인터페이스 없음 |
| LSP | 동일 입력의 UI 스레드·유효성·오류 계약, WARP 대체 경로 검증 |
| ISP | 기존 IViewportSurface의 구현자에게 새 필수 멤버를 강제하지 않음 |
| DIP | Core에 WPF/DX12/검사 참조 없음. 표시 데이터는 공급자 중립 |
| Information Expert | 좌표 변환은 표시기, 형상 불변식은 RoiGeometry, 항목 ID/순서는 레이어가 소유 |
| Creator | Canvas가 레이어를 생성. native 자원 생성/수명은 기존 GPU 소유자가 유지 |
| Controller | UI는 레이어 요청만 전달. 판정은 호스트, ROI 편집은 기존 EditorSession |
| Low Coupling | 검사 알고리즘/Core/원본 reader의 새 참조나 복사 없음 |
| High Cohesion | 레이어 자료와 표시를 나누고 앱에는 중앙선 조작만 연결 |
| Polymorphism | 검증된 불변 그래픽 변형을 처리. 외부 draw callback을 요구하지 않음 |
| Pure Fabrication | 표시 목록/캐시 책임을 ROI 도메인에 넣지 않음 |
| Indirection | 기존 RenderScene.Inspection을 제어 경계에서 레이어로 연결해 호스트 호환 유지 |
| Protected Variations | UI→렌더 불변 게시와 GPU 소유권 유지, 이미지/화면 좌표 명시 |

원칙 예외 없음. 전체 대량 편집 60FPS와 검사 CPU125/CUDA80ms의 기존 FAIL은 이번 표시 기능으로 변경하지 않는다.
