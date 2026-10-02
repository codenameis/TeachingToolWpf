# CPU 제약 조건의 선택·대량 편집 튜닝

2026-10-02. 기준: Ryzen 5 5600H, RAM 32GB, RTX 3060 Laptop, DX12 장치 예산 5.1GiB, Windows x64/.NET 10 Release. 설계와 14개 원칙 검토는 [ADR-009](decisions/0009-responsive-bulk-editing.md). [이전 렌더 튜닝](retained-roi-performance.md)의 GPU 버퍼 재사용은 유지한다.

## 측정 조건

- CPU 제약 시험은 **앱 프로세스 전체를 logical CPU 0 하나에 고정**했다. UI·렌더·파일·편집 작업이 같은 CPU를 경쟁한다. 실제 저사양 CPU의 캐시/메모리나 저사양 GPU를 재현한 것은 아니다. 전력/클럭은 고정하지 않았다.
- 배경은 8192×8192 합성 이미지, 창의 GPU 뷰포트 877.33×540 DIP, DPI 150%. 실제 카메라 연속 8K 영상 시험과 구분한다.
- 선택 시험은 32,940개 평면/PCB 계층, 분산 위치 순회, 각 작업 5회 워밍업+10회 측정, 독립 프로세스 3회. `fb97900` 배포본과 최종 목록/스냅샷 변경을 같은 입력으로 비교한다.
- 대량 편집 시험은 이미지 중앙 절반에 배치한 32,940개 평면 ROI, 첫 1주기 워밍업+3주기 측정, 프로세스 3회로 작업당 9표본이다. 16ms Dispatcher timer로 응답 간격을 기록하고 준비 중 wheel zoom을 실제 입력 경로에 보낸다.
- `dispatch`는 명령 호출이 반환하기까지, `complete`는 준비·게시·WPF layout 완료까지다. 각 작업의 `maxDispatcherGap`은 그 작업 중 관측한 최대 timer 간격이며 입력-광자 지연이나 GPU 시간과 다르다. 16ms timer의 OS 스케줄링 지연도 포함한다.
- 동기 대조군은 **이번 CPU 최적화를 포함한 동일 바이너리의 동기 Core 경로**다. 과거 배포본의 대량 편집 수치로 표시하지 않는다. 비동기 경로는 공간 인덱스와 목록 값을 미리 준비하고 대기 중 탐색/렌더도 계속하므로 총 완료 시간의 동등 작업량 비교가 아니다.

## 분산 선택: 페이지 표시 요소 재사용

| 32,940개 | 기존 p50, 3회 범위 | 변경 p50, 3회 범위 | 기존 p95 → 변경 p95 범위 | 선택당 UI 할당 |
|---|---:|---:|---:|---:|
| 평면 | 13.86–14.61ms | 4.04–4.78ms | 19.23–23.24 → 7.73–15.30ms | 약 785KB → 228KB |
| PCB 계층 | 17.23–20.35ms | 5.01–5.79ms | 26.03–26.35 → 8.08–11.41ms | 약 758–768KB → 232KB |

각 실행의 중앙값을 다시 비교하면 평면 약 3.4배, PCB 약 3.8배다. UI 할당은 약 69–71% 감소했다. 같은 크기의 페이지는 `ItemsSource`와 슬롯·컨테이너를 유지하고 레이블/깊이/ID 선택만 바꾼다. 페이지 항목 수가 바뀔 때는 수백 개 개별 알림을 단일 Reset으로 합친다. 도메인 ROI 수나 정확한 선택을 줄인 결과가 아니다.

## 대량 편집: 입력 반환과 계산 완료 분리

최종 경로는 worker 준비 동안 `BelowNormal` priority를 사용하고 callback 종료 시 공유 스레드의 원래 priority를 `finally`에서 복원한다. 표의 지연은 ms, 요청 반환·완료·작업별 최대 gap의 중앙값이다.

| 명령 | 동기 요청 반환 | 최종 요청 반환 | 최종 전체 완료 | 동기 최대 gap 중앙값 | 최종 최대 gap 중앙값 |
|---|---:|---:|---:|---:|---:|
| 전체 선택 | 117.73 | 0.79 | 101.94 | 120.71 | 33.57 |
| 전체 이동 확정 | 128.49 | 0.62 | 615.89 | 128.49 | 37.40 |
| 이동 Undo | 129.07 | 1.45 | 581.52 | 129.08 | 37.02 |
| 이동 Redo | 126.11 | 1.19 | 573.20 | 126.12 | 35.88 |
| 전체 삭제 | 85.29 | 1.15 | 131.70 | 89.11 | 33.34 |
| 삭제 Undo | 124.40 | 0.87 | 671.30 | 148.38 | 56.21 |

대량 편집은 완료까지 기다리는 동안 pan·zoom·미니맵을 사용할 수 있다. 편집/선택을 추가로 겹쳐 실행하지 않고 한 작업만 준비한다. Esc 취소, 오래된 문서/preview/clipboard 결과 거부, 최신 뷰 보존, 한 번의 Undo/Redo는 유지한다. 배열·이력·공간 인덱스 생성량 자체가 사라진 것은 아니다.

**긴 지연이 완전히 사라지지는 않았다.** 최종 9표본의 작업별 최대 gap 최댓값은 이동 119.62ms, 삭제 복원 104.77ms였다. 요청 반환 p95도 삭제에서 13.27ms까지 관측했다. 우선순위를 낮추기 전 준비 경로는 이동 Undo gap 최대 189.21ms·Redo 127.09ms였으며 최종은 각각 50.62ms·44.21ms였다. 전체 최악값은 줄었지만 이동은 이전 52.88ms보다 큰 단일 표본이 있어 모든 꼬리 지연이 개선되었다고 주장하지 않는다. GC·OS 스케줄링·렌더 경합이 남아 있다.

## 검증과 남은 범위

Core 117개, Imaging/WPF 46개가 통과했다. Release 빌드 경고/오류 0. 준비 revision 검사를 일시 제거한 변이 시험에서 stale 문서 회귀 4개가 실패했고 원복 후 통과했다. 실제 RTX의 최종 배포본 DX12 debug smoke로 HWND 입력, 버튼/단축키, 그룹/연쇄 삭제, Undo/Redo, 페이지 목록/한글 속성, OpenCV 필터, 8K/24K/라이브 미니맵, renderer 재시작을 확인했다. 스크린 캡처로 선택 핸들과 목록 표시를 확인했다. 물리 마우스 전체 수동 E2E를 새로 수행한 것은 아니다.

PCB 300개부터 19,200개까지 반복 복사/붙여넣기·마퀴·취소·Undo/Redo도 통과했다. 별도 원시 파일에는 32,940/100,000개 전체 편집 정확성 검사와 CPU 제약 100,000개 검사를 보관한다. 100,000개 검사는 독립 3회 정식 벤치마크와 구분한다.

최종 단일 코어·100,000개 실행의 워밍업 이후 요청 반환 최댓값은 2.33ms였다. 전체 이동 완료는 967–1,537ms, 이동 Undo 688–840ms, 삭제 Undo 804–1,067ms였다. 처리 중 탐색을 유지했지만 전체 작업의 최대 Dispatcher 간격은 138.60ms였다. 빠른 요청 반환을 빠른 연산 완료로 표시하지 않는다.

일반 복사, 대규모 생성/그룹화/필터 등록, 회전·resize 중 반복 형상 생성, 최초 공간 인덱스/계층 구축은 추가 최적화 대상이다. 대량 이동 확정은 256개 초과, 마퀴 선택·방향키 이동은 문서 4,096개 초과에서 준비 경로를 사용한다. 파일 교체의 모든 대량 이력 생성 경로를 이번에 비동기로 바꾸지는 않았다. BVH 구축/정렬 내부의 한 단계가 실행 중이면 취소를 다음 검사 지점에서 관찰한다. 수백만 개의 모든 명령이 일정 시간 또는 60FPS라는 보장은 하지 않는다. 실제 저사양 GPU·현장 PCB·최소 8K 실시간 영상 동시 지속 부하는 미검증이다.

## 재현과 원시 자료

[선택 CSV](data/lowcore-selection.csv), [대량 편집 CSV](data/lowcore-bulk-editing.csv), [원시 자료](data/low-spec-measurements.zip). `async`는 일반 worker priority 중간 측정, `priority`는 최종 정책, `sync`는 같은 최적화 코드의 동기 대조군이다. 원시 JSON을 `artifacts`에 풀면 아래 집계 스크립트로 표를 재계산할 수 있다.

```powershell
dotnet test TeachingToolWpf.slnx -c Release --no-restore
dotnet publish TeachingToolWpf.csproj -c Release --no-restore -o artifacts/publish
$app = Join-Path (Get-Location) 'artifacts/publish/TeachingToolWpf.exe'
$p = Start-Process $app -ArgumentList '--responsive-editing --rois 32940 --report artifacts/responsive-priority-1.json' -WindowStyle Hidden -PassThru
$p.ProcessorAffinity = [IntPtr]1
$p.WaitForExit()
# 위 실행을 파일명 1/2/3으로 반복. 동기 대조군은 --synchronous-edits 추가.
# 선택 시험: --roi-benchmark --roi-counts 32940 --roi-samples 10 --roi-single-batch --roi-distributed --roi-bulk --roi-report artifacts/lowcore-final-1.json
./scripts/Summarize-ResponsiveEditing.ps1
```
