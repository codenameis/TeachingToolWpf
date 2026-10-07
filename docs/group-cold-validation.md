# 20만 ROI 그룹화 후 확대·회전 프리징 검증

날짜: 2026-10-06. 결정: [ADR-037](decisions/0037-progressive-roi-detail-preparation.md).

## 조건과 판정

현재 `main`의 기존 검색 ROI/그룹 이름 변경을 보존한 작업 트리에서 비교했다. 로컬 수정·검증만 수행했다. Ryzen5 5600H,32GB,RTX3060 Laptop6GB,Windows x64,.NET10 Release,8K 합성 이미지 중앙의20만 ROI다. 그룹화 후200,001개가 된다. GPU 성능 비교는 debug=false, 실제 D3D12 검증/취소·WARP 시험은 debug=true로 분리했다.

**연속 확대의 표시 데이터 준비 정지는 크게 감소했다. 모든 조작의60FPS 목표는 여전히 FAIL이다.** CPU 제출 시간과 Present 반환 간격은 실제 모니터 표시 FPS가 아니다. 진단 창은 화면 밖에 있고 입력은 합성60Hz다. 물리 마우스와 사용자 실제 ROI 배치의 체감 검증은 NOT_RUN이다.

## 원인과 변경

첫 확대에서 새 LOD의 형상·역할 패킹을 렌더 스레드가 한꺼번에 수행했다. 기준 측정의 최대 제출183.91ms 중176.84ms가 패킹이며 해당 프레임의 GC 정지 증분은0ms다. 준비 후 탐색이 빨라진다는 현상과 일치한다. GC만의 문제나 언어의 한계로 볼 근거가 아니다.

새 LOD 준비를 프레임별4ms 예산으로 나누며 같은 불변 블록의 기존 표시를 최신 view/preview로 유지한다. 포인터가 멈춰도 상세화는 완료된다. ROI 역할은 ID 조회 표와 최근 두 계층의 배열로 재사용하고, 검색 경계 색인은 기존 편집 worker에서 준비한다. 초기 블록이나 단일 작업을 중간에 끊지는 않는다. 정책·소유권·설계 원칙 검토는 ADR을 따른다.

## GPU 실측

`before`와 최종 `final`을 동일한20만 ROI,6.5초 pan/zoom 조건으로 실행했다. 중간 반복`after-1/2`에서도 확대 제출 최대9.21/8.69ms였다. 최종판은 역할 조회 표를 실제 패킹이 필요할 때만 갱신한다. 입력 생산 스케줄과 임의 GUID 때문에 프레임별 샘플/가시 블록 수는 완전히 같지 않다.

| 측정(ms) | 변경 전 | 최종 |
|---|---:|---:|
| 그룹화 명령 dispatch | 2.68 | 2.42 |
| 그룹화 전체 준비/게시 | 1336.01 | 853.79 |
| 그룹화 중 최대 렌더 제출 | 35.46 | 23.02 |
| 그룹화 뒤 pan 최대 렌더 제출 | 1.61 | 0.72 |
| 첫 확대 포함 zoom 최대 렌더 제출 | 183.91 | 8.82 |
| zoom 최대 Present 반환 간격 | 195.03 | 37.89 |
| zoom 최대 입력 대기 슬롯 | 25.00 | 23.96 |
| Undo 최대 렌더 제출 | 121.51 | 2.53 |
| Redo 최대 렌더 제출 | 178.02 | 13.63 |

최종 zoom 입력 생략 주기20개(기준30개), 그룹화의 누적 GC 정지107.18ms(기준126.73ms)다. 이 누적값을 한 번의 정지 길이로 해석하지 않는다. 최종 Redo 최대 제출13.63ms 중 GC 정지 증분13.38ms가 포함됐다. 그룹화 최대 Present 간격45.10ms, Undo 입력 대기33.54ms도 남았다.

최종 실행에서 상세화 보류 프레임35개, 한 프레임의 최대 보류 블록125개를 기록했고 종료 시`roi_detail_pending=0`이었다. 마지막30프레임의 ROI 업로드 합은0바이트다. 캐시의 보수적 최대 계산량69.17MiB에는 역할 조회 표와 역할 배열 두 벌을 포함한다. 프로세스 전체 메모리나 활성 데이터의 절대 상한으로 표현하지 않는다.

## 그룹 회전과 취소

실제 회전 핸들 Down→Move120회(+warmup2)→Up을 실행했다.

| 측정(ms) | 변경 전 | 최종 |
|---|---:|---:|
| Down handler | 2.82 | 2.86 |
| Move handler 최대(warmup 포함) | 3.05 | 3.10 |
| 확정 구간과 그 후0.5초 최대 렌더 제출 | 37.68 | 6.23 |
| 회전 확정 전체 완료 | 1125.67 | 1249.90 |
| 확정 중 UI probe 최대 간격 | 34.74 | 44.66 |

표시 제출 개선과 별개로 확정 전체 시간·UI probe는 개선되지 않았다. 회전 진단의16ms Delay는 UI 진행에 의존하므로 독립60Hz 입력이나 물리 표시 FPS로 해석하지 않는다. 모든 ROI의 형상·계층·선택과 Undo/Redo 복원이 일치했다.

GPU debug=true에서20만 ROI 회전 확정350ms 후 Esc를 요청했다. 실제 요청364.97ms, handler1.63ms, 취소 종료3.79ms였다. preview/문서 즉시 복원, 이력 보존, 후속 편집 복구가 모두true다. 이는 CPU 편집 복원이며 GPU 완료 시간은 포함하지 않는다. 취소 후 다시 회전·확정·Undo/Redo 및 preview/확정 화면 캡처도 성공했다.

## WARP와 회귀

- PASS(정확성): GPU debug=true,20만 ROI 다중 선택의 실제 이동 드래그100회·확정·Undo/Redo·화면 캡처. Move+Layout p95는0.61ms,최대31.09ms,확정 전체839.20ms였다. 그룹 선택 회전과 별도의 다중 선택 이동 진단이며 이동60FPS 달성으로 표현하지 않는다.
- PASS: WARP debug=true,20만 ROI 그룹화·pan·zoom·Undo/Redo.150ms 후 그룹 취소에서 문서/선택/이력이 정확히 복원됐다. 모든 진단은`stage=complete`,오류null,exit0이다.
- FAIL(성능): WARP zoom 제출 최대10.11ms라도 Present 반환 최대190.43ms다. CPU 준비의 분할만으로 소프트웨어 래스터 성능을60FPS라고 선언하지 않는다.
- PASS: GPU/WARP 앱 스모크. 설정 페이지·색상·크로스헤어·도형·검색 점선 픽셀·부모 이름·Undo/Redo·8K 미니맵·필터·라이브 시작/정지·렌더 재시작, D3D12 오류 없음.
- PASS: 6개 테스트 프로젝트 합1093개. Canvas182,Core247,앱/Imaging257,Algorithm248,Inspection107,Compute52. 기존 PNG 캐시 디렉터리 이동에서 일시적인 접근 오류1건이 있었으며, 소스/기준 수정 없이 Inspection 프로젝트 독립 재실행107개가 통과했다.
- PASS: 새 역할 조회와 기존 문서 기반 결과의 동등성(4종 도형·중복 윤곽·중첩 부모·계층 변경/복원), 역할 캐시의 재순회 없는 Undo/Redo와 약한 참조 수명, 그룹 게시 전 검색 경계 준비. 부모 조건을 반전하는 mutation에서4개 테스트 실패를 확인한 뒤 원복·재빌드해 모두 통과했다. 원복 파일의 옛 타임스탬프로 남은 증분 바이너리는 갱신 후 다시 검증했다.
- PASS: Release 빌드 경고0/오류0,`scripts/Format-Code.ps1 -Verify`, `git diff --check`. 새 테스트의 초기 빈 Dictionary 용량0 가정은 런타임의 최소 bucket을 허용하는 작은 보관량 검사로 정정했다. 테스트 제거/skip이나 기능 기준 완화는 없다.

## 재현과 증거

원시 CSV/JSON, 요약, 빌드·테스트·컨벤션 로그는`artifacts/group-cold-20261006`에 있다. [보관 자료](data/group-cold-20261006.zip)도 참고한다. 성능 진단을 다른 빌드/테스트와 동시에 실행하지 않았다.

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
$app = '.\src\TeachingToolWpf\bin\Release\net10.0-windows\TeachingToolWpf.exe'
& $app --frame-cadence-check --cadence-group --rois 200000 --cadence-navigation-ms 6500 --cadence-gc-events --cadence-report artifacts/group-cold-20261006/final.json --render-profile artifacts/group-cold-20261006/final-render.csv --metrics artifacts/group-cold-20261006/final-metrics.csv
./scripts/Summarize-FrameCadence.ps1 -Path artifacts/group-cold-20261006/final.json
& $app --roi-rotation-benchmark --rois 200000 --rotation-selection group --rotation-samples 120 --rotation-warmup 2 --report artifacts/group-cold-20261006/rotation-final.json --render-profile artifacts/group-cold-20261006/rotation-final-render.csv --metrics artifacts/group-cold-20261006/rotation-final-metrics.csv
```

취소는 회전에`--rotation-cancel-ms 350`, 그룹 cadence에`--cadence-cancel-ms 150`을 추가한다. 화면 캡처는`--capture-rotation`, WARP는`--software-rendering`, D3D12 검증은`--gpu-debug`다. 성능 CSV를 덮어쓰지 않도록 다른 report/profile/metrics 경로를 사용한다.

최종 SHA256: VisionCanvas `C2C96C49B1C944E074A126084B9AB70651BBE688CC6760B9AC074D04E711389B`, Canvas.Core `25548F539B0458F47668DA7B84415681256758941816964F298D54D55C81DCD9`, 앱 `49F7641C4B1A223953757DD8F76AE20D66F68D366C0618B076E85D07330CC3AF`다. 기준 VisionCanvas는 `DC8DA48C22CA2505A770D8BAADA54165A7461C5E5EAC2A4A877E0412076EA4E2`다.

## 남은 작업

최초 문서 표시·처음 보는 공간 블록·큰 단일 폴리곤·대량 계층 표 작성은 동기 작업이 남는다. 회전 확정의 불변 이력 준비/게시와 GC·UI 대기를 더 줄여야 하며, 사용자 실제20만 ROI 배치와 물리 입력에서도 별도 측정해야 한다. 최초 로드의 수초짜리 설정/라벨 준비와 정적 화면의 렌더 생략을 이번 zoom 개선으로 해결했다고 표현하지 않는다.
