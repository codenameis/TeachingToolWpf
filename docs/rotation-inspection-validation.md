# 2026-10-05 회전 불량 검사 검증

## 입력·변경

사용 중이던 Debug 앱을 읽어 확인한 8192×8192 PCB 샘플, 사각형ROI `(760.793,598.841),110.787×245.01,0°`, 검색0~10°. 원래 앱의 입력·티칭·VS 디버깅 프로세스는 변경하지 않았다. 구현은 [ADR-030](decisions/0030-rotation-inspection-acceptance.md).

논문 모드에서 회전 불량 검사를 켜고 동일 ROI로 재학습하면 전체 방향 탐색 후 별도의 정상 편차로 NG를 판정한다. 기본은 꺼짐이며 정상 구간 초기값은0~10°다. 기존 검색 각도 값은 보존한다. OpenCV/자체 기하와 라이브러리의 점수 계산은 그대로다.

## 정확도

- **PASS**: 독립 8K WPF 진단 CPU/CUDA × 정상범위2종 × 각2회, 총8회 모두256개 검출, 후보 잘림/출력 상한 없음.
- **PASS**: 같은 정상 범위 안의 CPU/CUDA와 반복 실행 사이에서 표시 위치·각도·경계·문구·색이 모두 동일하다.
- **PASS**: 역방향128개 모두 약±180°·NG/Tomato. 정상 범위0~10°일 때 총192개NG: 정방향 중64개의 작은 음수 측정각도 실제 설정 밖이다.
- **PASS**: 별도 비교인 정상 범위-10~10°에서 역방향128개NG와 정방향128개OK. 이 구간으로 사용자 현재 조건을 몰래 바꾸지 않았고 검증 창의 마지막 결과는0~10°로 복원했다.
- **PASS**: WPF 앱 테스트240개(기존218+추가22). 회전 주기/학습 기준각, 경계·유효성, NG/WARN/CHECK 우선순위, 재학습/결과 무효화, 다른 모드 보존, 로그·색·라벨, 숫자 실제 편집, 현재ROI의 한주기 실제 서비스 포함.
- **PASS**: main 병합 전 Release 솔루션 전체936개: Core190, 앱240, Algorithm248, Canvas103, Compute52, Inspection103. 실패·건너뜀0. 별도 `artifacts/kensa-text-tests` 출력 경로로 실행 중인 앱을 보존했다. 정확성 회귀 결과이며125/80ms 달성을 의미하지 않는다.
- **PASS**: 회전 NG 조건을 반전하는 mutation은 NG 기대값을 OK로 바꾸어 회귀 테스트가 실패했다. 원본 복원 후 전체240개가 다시 통과했다.
- **NOT_RUN**: 신규 검증 창에 대한 컴퓨터 사용 도구의 자동 화면 판독. 앱 승인 시간 초과로 완료하지 못했다. 진단은 실제 WPF 명령·표시 마커·Present/fence 이벤트를 기록하지만 사람이 본 최종 화면의 독립 판독을 대신하지 않는다.

## 시간: 목표 미달

별도 Release/ReadyToRun AVX2 실행본. 첫 명령은 장치 초기화를 포함하며 서비스 시간과 구분한다. 기존 Debug 앱이 함께 열려 있었다. 아래는 정상 범위0~10°의 첫/반복1회이며 충분한 통계 표본이 아니다.

| 실행 | 서비스 ms | 명령 await 완료 ms | 같은 결과의 Present/fence ms |
|---|---:|---:|---:|
| CPU 첫 | 510.1 | 538.7 | 692.1 |
| CPU 반복 | 420.1 | 562.4 | 452.3 |
| CUDA 첫 | 387.1 | 909.9 | 799.5 |
| CUDA 반복 | 346.7 | 412.1 | 354.1 |

명령 await 이후 UI 스케줄링 지연과 별도 렌더 스레드 때문에 Present/fence 관측이 명령 완료보다 먼저일 수 있다. 전체 완료 하한은 두 시간의 최댓값이며 물리 클릭·WPF 최종 합성·scan-out은 측정하지 않았다. 서비스 자체가 목표를 초과하므로 **CPU125/CUDA80 수용은 FAIL**이다. 입력 ROI가 과거 짧은핀 fixture와 다르므로 기존 성능 기록과 직접 속도 비교하지 않는다.

## 재현

```powershell
dotnet run --project benchmarks/TeachingToolWpf.Matching.Benchmarks -c Release -- artifacts/rotation-diagnosis/before.json rotation-diagnosis
dotnet artifacts/publish-rotation-inspection/TeachingToolWpf.dll --rotation-inspection-smoke --rotation-inspection-show
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --artifacts-path artifacts/kensa-text-tests
```

첫 명령은2048² 한주기에서0~10/±180 검색 및 특징 대비 .2/0을 비교하는 재현 도구다. 두번째는8192²에서 실제 학습·검사 명령·색·로그를 검증한다. `--rotation-inspection-show` 생략 시 진단 후 그 검증 창만 종료한다. 원시 JSON과 mutation/test 결과, 실행 DLL 해시는 [증거 ZIP](data/rotation-inspection-20261005.zip)에 보존한다.
