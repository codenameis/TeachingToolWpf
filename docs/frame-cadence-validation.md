# 작업 중 2D60FPS 기준과 첫 병목 개선 — 2026-10-05

**목표 상태: FAIL.** 60FPS의 약16.67ms 예산을 우선한다. 형상검사 CPU125ms/CUDA80ms는 별도 목표이며, 이 작업에서는 검사 알고리즘을 수정하지 않았다. 이전 복붙 완료 시간의 개선을 프레임 유지로 해석하지 않는다.

## 범위와 구현

- 현재 브랜치 `codex/vision-canvas-boundaries`에서 로컬 수정·검증만 진행. 커밋·푸시 없음.
- 초기 GPU 업로드에서 `RoiInstance.Pack`과 `ResolveRoles`가 같은 공간 블록을 각각 열거하던 부분을 한 순회로 통합했다. 초기 역할 캐시도 함께 준비한다. 기존 역할만 갱신하는 경로와 약한 계층 참조를 유지한다.
- 중복 도형의 부모→자식→일반 역할 우선순위, 요약/실제 도형 순서, 원·삼각형·폴리곤 윤곽, GPU 버퍼 소유권과 셰이더 계약을 유지한다. 표시 도형을 생략하거나 Undo/Redo를 줄이지 않는다.
- `--frame-cadence-check`는 UI와 독립된 절대60Hz 일정으로 pan 입력을 만들고 Dispatcher에는 최신 입력 하나만 대기시킨다. 연속 탐색2초, 복사·붙여넣기, Undo, Redo를 실행하며 각 명령 뒤1초까지 탐색을 유지한다. 입력 생산 지연·합쳐진 주기·Dispatcher 대기·GC 누적 정지를 기록한다.
- `--metrics` CSV 끝에 공통 Stopwatch 시각/주파수를 추가했다. 기존 열은 유지한다. 집계 스크립트는 프레임 간격의 양 끝이 같은 활성 구간 안에 있는 경우만 포함한다.

## 측정 조건과 해석

Release, 디버거 미연결, 기준 장비 Ryzen5 5600H / RAM32GB / RTX3060 Laptop. 실제 렌더러 보고: NVIDIA GeForce RTX3060 Laptop GPU, VRAM5.9GiB / budget5.1GiB. WMI 조회: NVIDIA 드라이버32.0.15.8183, 표시60Hz; AMD 드라이버31.0.12002.1002, 표시144Hz. 창과 물리 모니터의 연결·전원/열 상태는 이 시험에서 확정하지 않았다.

8192×8192 합성 이미지와 PCB 계층 ROI4만개 전체 선택→8만개 붙여넣기. 동일 앱·Core에서 Canvas DLL만 바꿔 전후를 번갈아3프로세스씩 실행했다. 각 프로세스의 초기 fixture/선택/장치 준비는 활성 구간 밖이다. 명령 중 JIT·준비·GC·게시 및 이후 렌더는 포함하며, 명령 완료까지의 시간과 렌더 완료를 혼동하지 않는다. 모든 프로세스가 exit0, 복제 위치/형상/이름/순서/부모/그룹/선택 및 Undo/Redo exact 검증을 통과했다. 탐색으로 바뀌는 viewport는 ROI 이력 exact 비교에서 분리한다.

| 측정 바이너리 | SHA256 |
|---|---|
| Canvas 이전 | `9601C9E570A94F9B1FF7004A9D0437EC6AA5F0A296CC4599B1DF56EC23648F39` |
| Canvas 이후 | `F9C5F5058EB5BE6C738E69E3B788BE0452C2C26C4A7CA9EC90BBB32007A3DD1A` |
| 공통 앱/진단 | `49EF2EC677804C95972405D1ACEC3FEE7680239FFE7077414FD66D823D352458` |
| 공통 Core | `16DF55F28D08E13037435B3A578F1D7B3B65E3A920AEA42C52C05A2F4772698B` |

창은 기존 자동 진단과 같이 화면 밖에 놓는다. 이 기록은 **입력 처리와 렌더 제출의 정지 재현**이며 실제 모니터의 60FPS 달성 증거가 아니다. 입력 생산도 OS 타이머/스케줄러의 영향을 받으므로 평상시 프레임 간격에는 입력 공급 지터가 포함된다. 긴 렌더 CPU 준비 구간은 별도로 직접 측정한다.

`submit_ms`는 CPU의 draw 기록/ROI 패킹·자원 생성·업로드 명령 준비 구간이다. GPU 실행 시간이나 Present만의 시간이 아니다. `interval_ms`는 Present 반환 뒤 metrics 기록 사이 간격이며 물리 표시 시각이 아니다. GPU timestamp는 같은 back-buffer 슬롯의 이전 완료 프레임을 읽으므로 같은 CSV 행의 현재 장면과 바로 대응시키지 않는다. 정상 정적 화면의 렌더 생략은 유지한다.

최신 요청의 예정→실행 지연만 보면 합쳐진 오래된 입력이 빠져 지연이 작아 보일 수 있다. 따라서 입력 실행 간격, 대기 슬롯 시간, 생략 주기를 함께 기록한다. `inputSkippedPeriods`에는 producer의 생략도 포함되므로 `producerSkippedPeriods`와 더하지 않는다. 종료 직전 미적용 요청은 `unappliedAtStop`으로 구별한다.

## 결과

표의 평균은 **각 프로세스 해당 구간의 최대값을 구한 뒤3회의 평균**이다. 평상시 프레임 평균으로 긴 멈춤을 희석하지 않는다.

| 활성 작업 | 가장 긴 CPU 렌더 제출 준비의 평균, 전→후 | 가장 긴 Present 반환 간격의 평균, 전→후 | 개선 후 최악 Present 반환 간격 |
|---|---:|---:|---:|
| 복사·붙여넣기 | 205.69→176.52ms | 229.43→194.60ms | 205.08ms |
| Undo | 81.44→61.46ms | 100.10→78.67ms | 85.44ms |
| Redo | 212.38→185.29ms | 226.76→206.47ms | 218.32ms |

복붙 렌더 준비 피크 평균은 약14% 줄었다. 여전히16.67ms를 크게 넘으며, 개선 후 복붙 구간의 GC 정지 누적은115.17–117.23ms, 최대 Dispatcher 대기 슬롯은36.20–40.99ms였다. GC 누적은 한 번의 최대 정지 길이가 아니며 CPU 단계 시간과 중복될 수 있어 더하지 않는다. 렌더 작업 감소가 UI 정지를 해결했다고 주장하지 않는다.

개선 후 steady 구간의 Present 간격p95도25.78–27.14ms였다. 짧은 자동 시험/타이머 입력/비가시창 조건이므로 이를 모니터 FPS나 장시간 수용 결과로 변환하지 않는다. 초기 fixture 생성의 큰 정지도 제외해 성공으로 처리하지 않으며 이번 비교 범위 밖이다.

## 검증과 잔여 작업

- PASS: 전체 솔루션 Release 빌드, 경고0/오류0. Canvas124개와 앱 통합240개 테스트 통과. 형상/역할 한 순회, 실제 윤곽, 중복 형상, 요약, 빈 블록, 계층 메타데이터 변경, 약한 참조 수명을 검증했다.
- PASS: 수정7개 C# 파일의 whitespace·명명·style 검사 및 diff 검사. 이전 전체 형식 검사에서 발견한 무관한6개 기존 파일은 수정하지 않았다.
- PASS: 전후6프로세스에서 정확한 복제·Undo/Redo와 명령 중 연속 탐색. 원시 입력·렌더 로그와 요약은 [증거 ZIP](data/frame-cadence-20261005.zip)에 보관한다.
- PASS: 실제 RTX의 DX12 debug layer에서4만PCB 확대·축소/선택 전후 시험 exit0, 렌더 오류 없음. 두 GPU 캡처에서 윤곽·역할 색상·이름표·선택 표시·미니맵을 확인했다. 성능 비교는 debug를 끈 실행만 사용했다.
- FAIL: 60FPS 유지. 첫 새 공간 블록들의 CPU 패킹·DX12 자원 생성·업로드를 여전히 한 프레임에서 처리한다. worker의 대량 할당에 따른 process GC, UI의 동기 Copy도 남는다.
- 미완료: 작업의 정확성과 새 장면의 일관성을 유지하면서 렌더 준비/업로드를 프레임 예산 안으로 나누는 후속 개선. 일부 ROI만 누락해 빠르게 보이게 하지 않는다.
- NOT_RUN: 가시창의 실제 표시 간격/물리 입력 지연, 장시간 편집, 사용자 ROI 데이터, 이 변경의 실제 GPU 없는 PC 성능. 이번 수치를8K 라이브/24K 일반 파일/모든 ROI 도형/모든 장비의60FPS로 일반화하지 않는다.

## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
& ./src/TeachingToolWpf/bin/Release/net10.0-windows/TeachingToolWpf.exe --frame-cadence-check --rois 40000 --cadence-report artifacts/frame-cadence/run.json --render-profile artifacts/frame-cadence/run-render.csv --metrics artifacts/frame-cadence/run-metrics.csv
# GUI 프로세스가 종료되고 run.json의 stage=complete/error=null을 확인한 뒤:
./scripts/Summarize-FrameCadence.ps1 -Path artifacts/frame-cadence/run.json
```

## 원칙 검토

| 원칙 | 이번 변경의 판단 |
|---|---|
| SRP | 패킹은 인스턴스 타입, 업로드는 렌더러, 재현/집계는 진단이 담당 |
| OCP | 기존 내부 패킹 경로를 확장하며 외부 소비자 변경 없음 |
| LSP | 형상·역할 배열 순서와 의미를 기존/결합 경로 테스트로 비교 |
| ISP | Canvas 공개 계약을 늘리지 않음 |
| DIP | Core·앱·GPU 참조 방향 유지 |
| Information Expert | 형상 인스턴스 타입이 패킹과 역할의 인덱스를 함께 결정 |
| Creator | 기존 렌더 소유자가 GPU 자원을 생성·해제 |
| Controller | UI 편집 명령/이력의 책임 변경 없음 |
| Low Coupling | 기존 불변 문서와 렌더 내부 경계 유지 |
| High Cohesion | 같은 GPU 업로드 자료를 만드는 중복 순회만 통합 |
| Polymorphism | 새로운 변형 지점이 없어 추가 다형성 불필요 |
| Pure Fabrication | 역할 캐시의 기술 책임과 약한 참조 유지 |
| Indirection | 추가 중계 계층 없이 기존 앱 조립/진단 연결 사용 |
| Protected Variations | 셰이더·Core·입력·이력 계약 유지 |

설계 원칙 예외 없음. 독립 리뷰에서도 수정이 필요한 결함은 발견되지 않았다.
