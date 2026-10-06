# ROI 역할 색상·독립 두께 검증

후속: 아래는 2026-10-02 기록이다. 색상 편집·단일/다중 선택 분리·ConfigPage의 최신 동작은 [2026-10-06 검증](configuration-validation.md)과 [사용 가이드](configuration-guide.md)를 따른다.

2026-10-02. [ADR-011](decisions/0011-roi-role-styles.md)의 최종 구현을 **Core 158개+Imaging/WPF 97개, 총 255개 테스트**, Release 빌드 경고·오류 0, 실제 RTX UI/GPU smoke로 검증했다. [최종 smoke 로그](../artifacts/roi-style-final-smoke.log), [진단 결과](../artifacts/roi-style-check.json), [회전 요약](../artifacts/roi-style-final-rotation-summary.json)을 근거로 한다. 원시 JSON/CSV·캡처·로그·해시 설명은 [검증 자료 묶음](data/roi-style-validation.zip)에 담았다.

## 실제 UI·GPU 확인

장치는 NVIDIA GeForce RTX 3060 Laptop GPU, VRAM 5.9GiB/budget 5.1GiB, DPI 150%다. 최종 native smoke는 DX12 debug on으로 종료 0이었다. `roi-style-check.json`은 `completed=true`, `error=null`, `newErrors=[]`를 기록한다. 드라이버 버전은 이 진단 JSON에 따로 기록되지 않았다.

기존 입력/속성 검증 후 빈 문서에 자연 포함 부모·자식·독립 ROI를 만든다. 부모는 `IsGroup=false`지만 실제 자식이 있어 부모 역할이다. 실제 `RoiStylePanel`의 ValueTextBox를 포커스하고 일반 2·자식 3·부모 4를 입력한 뒤 버튼 automation의 `두께 적용`을 실행했다. 설정 화면에서 세 값과 `두께 적용`/`기본값` 버튼, 오른쪽 패널의 정상 스크롤 배치를 확인했다. 설정은 문서 형상·선택·이력을 바꾸지 않는다.

| 캡처 | 확인 결과 |
|---|---|
| [설정 패널](../artifacts/roi-style-settings.png) | 부모 4 / 자식 3 / 일반 2 DIP 입력·적용 UI와 GPU 윤곽을 함께 확인 |
| [기본 캐시 준비](../artifacts/roi-style-baseline.png) | 두께 적용 전 같은 fixture를 먼저 그려 초기 GPU 데이터를 준비 |
| [미선택 역할](../artifacts/roi-style-roles.png) | 부모 분홍·자식/일반 회청. 150% DPI에서 4 DIP 부모는 6px, 3 DIP 자식은 경계 정렬에 따라 4–5px, 2 DIP 일반은 3px로 확인 |
| [전체 선택](../artifacts/roi-style-selected.png) | 부모는 진한 분홍, 자식/일반은 파랑. 각 역할 기본 두께에 선택 0.5 DIP 추가 |
| [부모 단일 선택](../artifacts/roi-style-parent-selected.png) | 부모 선택 색을 따른 윤곽·핸들 연결선·이름/각도 표시 |
| [자식 분리](../artifacts/roi-style-detached.png) | native HWND 드래그 확정 후 큰/작은 영역 모두 일반 회청 역할로 전환 |
| [Undo](../artifacts/roi-style-undo.png) | 정확한 값·부모/자식 역할 복원, 두께 설정 유지 |
| [Reset](../artifacts/roi-style-reset.png) | Redo 이후 기본 palette 복원, 문서·이력 유지 |

진단의 역할 판정과 Undo/Redo는 ID·형상·이름·관계·선택을 비교한다. 캡처 전에 같은 파일을 삭제해 과거 결과를 재사용하지 않는다. 종료 시 fixture를 지우고 palette·뷰를 복원했으며, 이후 미니맵·OpenCV 필터·이미지 교체/Undo·8K 라이브·renderer 재시작까지 기존 전체 smoke가 통과했다.

## 캐시·소유권 검증과 실측의 구분

자동 검증은 역할 우선순위, 동일 형상 중복 표시의 부모>자식>일반 우선순위, LOD 요약의 일반 역할, 계층 메타데이터만 변경한 역할 갱신, 세 두께의 독립성/범위/원자적 적용, 문서 이력 독립성을 확인한다. `NoInlining` 팩터리와 GC/KeepAlive 회귀는 역할 캐시가 살아 있어도 이전 Children 맵이 회수되어 강한 참조로 과거 문서 수명을 연장하지 않는 것을 확인했다.

구현은 32바이트 형상 instance와 별도 4바이트 역할 데이터를 같은 GPU 리소스 안에서 소유한다. 같은 가시 블록/LOD에서 palette 변경은 상수 변경 경로이고, 계층 버전이 달라져도 역할 배열이 같으면 복사하지 않는다. 역할 변경 시에는 역할 데이터만 갱신한다. 이는 구현·캐시 회귀의 검증 결과다.

**스타일 적용 순간의 GPU 업로드 0B 실측은 확보하지 못했다.** JSON에 `baselineCapturedEndTicks`, `styleApplyStartTicks`, `styleCapturedEndTicks`, `stopwatchFrequency`를 남겼지만 최종 `roi-style-final-smoke-render.csv`는 후속 GPU 재시작 때 재생성되어 마지막 한 프레임만 남았다. 따라서 Apply timestamp와 CSV를 결합할 수 없다. 성공 캡처나 아래 회전 중 0B 결과를 스타일 변경 순간의 업로드 실측으로 대체하지 않는다. 이 구간을 계측하려면 재시작까지 로그를 보존하는 별도 실행이 필요하다.

## 20만 ROI 회전 회귀 측정

약한 버전 참조 수정까지 포함한 최종 바이너리에서 평면 전체 선택과 PCB 부모 선택을 각각 한 번 측정했다. Release, DX12 debug off, 고정 8192×8192 합성 이미지, 뷰포트 877.33×540 DIP, DPI 150%, 워밍업 2회+측정 180회다. 요청 각도는 시작점 기준 0.1°씩 증가하며 입력 사이에 `Task.Delay(16)`을 둔다. PCB는 부모 22,223개 선택·자손 포함 20만 개를 변형한다. 반복 3회 수용 시험이나 실제 8K 라이브 동시 부하가 아니다.

| 최종 배치 | preview 관측 / 준비 대기 입력 | callback p50 / p95 | 렌더 프레임 시작 간격 p95 | 회전 구간 ROI 업로드 | 정확한 Undo/Redo |
|---|---:|---:|---:|---:|---|
| 평면 20만 전체 선택 | 180 / 0 | 0.2285 / 0.3346ms | 22.1965ms | 0B | 통과 |
| PCB 20만 부모 선택 | 176 / 4 | 0.2032 / 0.3378ms | 22.1679ms | 0B | 통과 |

callback 통계는 `DisplayedMeasured`, 즉 모델 preview가 존재한 입력만 사용한다. 준비 대기 요청을 빠른 회전으로 집계하지 않는다. 렌더 통계는 첫 측정 입력부터 마지막 layout 종료+16ms 안에 완전히 들어간 preview 프레임만 사용한다. ROI 업로드는 이 반복 회전 구간의 합계이며 초기 생성·선택·palette 적용·확정 후 업로드는 뜻하지 않는다.

PCB의 첫 preview 프레임 시작은 첫 입력으로부터 113.0661ms, **CPU draw 기록 종료는 117.5709ms**였다. 모델 sample의 첫 preview 관측은 index 7, callback 종료 상한 116.6933ms다. 비동기 모델 게시와 렌더 완료 시점은 다르며 둘 다 모니터 표시 지연을 뜻하지 않는다. 평면의 첫 프레임 CPU 기록 종료는 5.9167ms였다.

대량 확정 비용도 남았다. 평면 확정 complete는 742.14ms, PCB는 1,237.59ms였고 작업 중 최대 Dispatcher gap은 각각 62.24/66.90ms였다. 평면 Undo/Redo 완료는 537.86/526.50ms, PCB는 889.17/896.69ms다. 빠른 반복 callback을 편집 전체의 무지연 완료로 해석하지 않는다. 프레임 간격에는 입력 대기·스케줄링·이전 Present가, render 준비 단계에는 fence 대기가 포함될 수 있다. 자세한 측정 정의와 기존 병목은 [회전 성능 기록](rotation-performance.md)을 따른다.

## 재현과 범위 한계

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
dotnet test TeachingToolWpf.slnx -c Release --no-restore
dotnet publish TeachingToolWpf.csproj -c Release --no-restore -o artifacts/roi-style-release
$app = Join-Path (Get-Location) 'artifacts/roi-style-release/TeachingToolWpf.exe'
$process = Start-Process $app -ArgumentList '--smoke --gpu-debug' -WindowStyle Hidden -PassThru
$process.WaitForExit()
# artifacts/smoke.log, roi-style-check.json, roi-style-*.png를 함께 확인한다.
./scripts/Summarize-RoiRotation.ps1 -Path 'artifacts/roi-style-final-rotation-200k-flat.json','artifacts/roi-style-final-rotation-200k-pcb.json' -Format Json
```

LOD 요약은 중립 일반 역할이며 개별 도메인 ROI·히트·선택은 유지한다. 색상 편집기나 세션 간 설정 저장은 이번 범위가 아니다. 물리 마우스 수동 E2E, 혼합 DPI 모니터 이동, 실제 저사양 GPU, 현장 PCB+8K 라이브 장시간 부하를 이번 시나리오만으로 검증했다고 표시하지 않는다. palette-only 실제 업로드 계측 공백과 최초 준비·대량 확정 지연도 남아 있다.
