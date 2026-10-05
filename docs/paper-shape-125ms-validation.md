# 논문 형상 125 ms 개선 — 구현 및 검증 기록

2026-10-05. **CPU 125 ms / CUDA 80 ms 미만 수용 결과는 FAIL이며 작업은 미완료다.** 아래 구현·검출 회귀 PASS와 성능 목표 FAIL을 구별한다. [계획](../tasks/paper-shape-125ms-plan.md), [ADR-027](decisions/0027-paper-shape-end-to-end-latency.md), [작업 상태](../tasks/todo.md)를 따른다.

## 적용한 변경

1. 앱/CLI 조립 지점에서 논문 `ShapeSearchPipeline.Paper`를 선택한다. 기하 모델이면 기존 경로로 실행하고 OpenCV는 기존 서비스를 유지한다. 논문에는 기하 모델을 만들지 않지만 원본 추가 윤곽 감점은 유지한다.
2. 3단계 이상 논문 모델의 작은 영상도 원본 면적 평균 기반 피라미드 검색을 사용한다. 2 Mpx 이하의 거친 창 내부를 64 px로 나누고 기존 halo·ownership·후보/검증 예산·최종 원본 검증을 적용한다. kensa는 순차 일반 4창의 44개/잘림에서 거친 24창의 48개/잘림 없음으로 바뀌었다. 논리 창 크기의 변화이며 순수 SIMD 동치 변경과 구별한다. 회전·배율·창 경계 및 독립 정답으로 검사했다.
3. 선택형 `IOriginalGrayTileSource`를 논문 reader에 연결했다. 합성 샘플은 동일한 행 단위 도형 정의에서 RGBA/Gray8을 만들며, 불필요한 RGBA 생성·Gray 변환을 생략한다. RGB→Gray8의 기존 정수 BT.601 반올림, 모든 LOD 원본 색상, 잡음, 타일 경계를 비교했다. 실파일 kensa는 기존 원본 I/O를 사용한다. 검사별 16 MiB 원본/4 MiB 축소 LRU와 단일 reader 소유권을 유지한다.
4. 후보의 첫 단계에서는 이미 학습한 정수 특징을 재사용한다. 9×9 국소 방향 응답을 특징별 연속 접근과 SSSE3 정수 합산으로 계산하며 scalar fallback을 둔다. 이후 연속 각도·배율이 바뀌는 단계는 기존 방식으로 특징을 만든다. 연속 방향 검증·point-to-line의 수식과 후보 한도는 유지한다. 일부 공통 hot method에 최적화 JIT 정책을 명시했으며 일반/기하 경로의 결과 회귀를 확인했다.
5. 논문 결과 라벨은 기존 글자 비트맵을 같은 위치에서 래스터화한 뒤 atlas에 복사하여 한 번 업로드한다. 일반 ROI/다른 방식의 지연 라벨 정책은 유지한다. CPU atlas는 최대 8 Mpx이며 이를 넘으면 기존 경로를 사용한다. GPU 할당 정렬·임시 래스터·프로세스 메모리는 이 수치에 포함되지 않는다. 마지막 atlas 하나를 유지하고 이전 GPU 자산은 frame fence 이후 반환한다. 글자 캐시는 검사 결과 캐시가 아니다.
6. 논문 전체 결과 목록은 가상화 ListBox로 표시한다. 모든 결과 문자열·점수·판정·실제 학습 ROI 경계는 보존한다. Canvas에 공급자 비의존 overlay identity와 Present/GPU fence 완료 통지를 추가했다. 진단은 명령 완료와 결과 표시 관측 시간을 별도 기록한다.

현재 구현은 `Shape_Match` 원본 포팅이 아니다. 기존 독립 C# LINE-2D 계열 후보 검색·연속 방향 검증·point-to-line 결합을 개선했다. 이번 변경에 CUDA kernel/알고리즘 범위 확장은 없다. CUDA는 여전히 거친 방향 응답·정수 후보 점수를 계산하고 입력·전처리·자세 보정·원본 판정은 CPU가 담당한다.

## 입력·측정 경계

- 장비: Ryzen 5 5600H, 논리 CPU 12개/병렬도 12, RAM 32 GB, RTX 3060 Laptop 6 GB, Windows x64/.NET 10 Release. 서비스 benchmark는 TieredCompilation=false, 앱은 기존 `Publish-Sample.ps1 -InspectionLatency`의 ReadyToRun/AVX2 빌드다. 서비스와 앱 DLL 해시는 각 JSON에 기록하며 동일 binary라고 부르지 않는다.
- 8K: `sample:8192:8192`. 학습 중심 `(759.518,546.178)`, `105.827×129.459`, 각도 0. 회전 ±180°/10° 간격, 배율 1, 특징 최대 160, 최소 점수 .72, 지지 .6, 추가 윤곽 비중 1, 출력 256. 기존 후보 2048/원본 검증 64/거친 검증 256을 유지했다.
- kensa: 원본 `1024×1536`, SHA256 `7EEED204BFE88C79F285D4F0AE195F954880984C58F59282B501965080846E77`. 메모리 칩 ROI left 156/top 158/89×52, 위와 같은 검색 조건. 육안으로 확인한 6열×8행 중심 annotation과 12 px 대응 반경은 **개체 누락·중복 확인용이며 정밀 위치 오차 기준이 아니다.**
- 서비스 wall은 원본 입력·검사·자원 반환을 포함하고 UI/Present와 별도 장치 준비를 제외한다. 변경 전은 같은 측정 도구에서 첫 실행+반복 3회, 변경 후는 첫 실행+반복 5회, CPU/CUDA 교대다. 오래된 CLI의 약 2초 수치와 직접적인 배율 비교를 하지 않는다.
- 일반 Release 실행과 사전 컴파일 배포를 혼동하지 않는다. R2R 이전 앱 실험(`app-latency-jit-all.json`)에서는 hot method 정책만 적용해도 CPU 최초 서비스916.9 ms/표시 관측1212.8 ms, 후속 서비스464.8~665.9 ms였다. 아래 빠른 수치는 명시한 측정/배포 설정의 결과이며 기본 `dotnet run` 성능을 보장하지 않는다.
- 앱은 화면을 표시한 상태에서 VM 검사 명령 호출 직전부터 같은 overlay의 전체 필수 라벨 준비·Present 제출·GPU fence 완료, 이어서 WPF Rendering 이벤트와 Dispatcher ContextIdle까지 관측한다. 최초 CUDA 준비를 포함하며 학습·이미지 열기는 별도다. 명령 전 750 ms 화면 안정화가 있다. **물리 버튼 입력 수신·DWM 합성 완료·모니터 scan-out 측정은 아니다.** 현재 값만으로 계획의 T5−T0 PASS를 선언할 수 없다.
- 검색 결과 캐시, 클릭 전 검사 영상 준비, 점수/특징/각도 범위 축소, 시간 제한으로 결과 자르기는 적용하지 않았다. 병렬 진단 시간 합은 worker 누적이며 전체 wall을 분해한 수치가 아니다.

## 서비스 결과

단위 ms. 중앙값/최대는 최초 실행을 제외한 반복 표본이다. `sample-before/final.json`, `kensa-before/final.json`, `service-summary.json`에 원시 결과를 보관한다.

| 입력·모드 | 변경 전 첫 / 반복 중앙 / 최대 | 변경 후 첫 / 반복 중앙 / 최대 | 변경 후 결과 |
|---|---:|---:|---|
| 8192² CPU | 399.4 / 286.5 / 286.7 | 393.9 / 261.7 / 269.1 | 256개, 잘림 없음 |
| 8192² CUDA | 309.3 / 236.5 / 250.8 | 281.9 / 227.3 / 252.0 | 256개, 잘림 없음 |
| kensa CPU | 745.7 / 608.3 / 624.0 | 315.7 / 168.7 / 210.2 | 48개, 잘림 없음 |
| kensa CUDA | 642.4 / 596.6 / 619.2 | 229.4 / 158.8 / 228.9 | 48개, 잘림 없음 |

8K 반복 원본 읽기·변환 중앙값은 CPU 119.4→64.7 ms, CUDA 126.9→55.2 ms다. 이 구간은 다른 worker 계산과 겹치므로 감소분을 wall 절감분에 그대로 더하지 않는다. kensa 읽기 중앙값은 CPU 10.3→16.1 ms로 늘었지만 검색 구조 변경으로 전체 시간이 감소했다. 원본 타일 읽기는 8K 512회/kensa 6회다. 변경 후 반복 한 표본의 관리 할당량은 8K CPU 약445 MB/CUDA 약421 MB이며 peak working set이나 cache 예산이 아니다. 할당 부담도 남아 있다.

변경 후 서비스 CUDA 최초 준비는 8K 약1101.5 ms, kensa 약1094.7 ms이며 위 첫 검색에 포함하지 않았다. 준비 전 첫 클릭의 전체 시간으로 위 첫 검색을 제시해서는 안 된다.

두 fixture 각 12회 결과에서 CPU/CUDA의 전체 `ShapeMatch` 레코드·순서가 정확히 일치한다. 8K는 변경 전/후 결과도 정확히 일치하고 256개 일대일, 최대 위치 오차 약0.205 px, 사선/깨끗한 핀 점수 분리를 유지했다. kensa는 48개 일대일이며 중복·미할당·후보 잘림·출력 상한 초과가 없다. 이 두 fixture 통과를 임의 현장 영상의 검출률로 확대하지 않는다.

## 앱 표시 검증

실제 표시 앱에서도 8K 256개, kensa 48개 일대일 대응과 CPU/CUDA 표시 결과 동등성을 확인했다. JSON의 `PresentationMilliseconds`는 명령 진입→해당 결과의 renderer fence 완료이고 `CommandThroughDisplayMilliseconds`는 뒤따른 WPF 표시 관측까지다. `ServiceMilliseconds`는 기존 앱 상태의 0.1 ms 표시값이므로 외부 wall과 구별한다.

최종 정상 표시 앱에서 각 모드 최초1회+반복3회, 8K→kensa 프로세스를 순차 실행했다. 단위 ms. GPU 최초 표시에는 장치 준비가 포함된다.

| 입력·모드 | 최초 서비스 / 표시 관측 | 반복 서비스 중앙 | 반복 renderer 완료 중앙 | 반복 표시 관측 중앙 / 최대 |
|---|---:|---:|---:|---:|
| 8192² CPU | 394.5 / 774.9 | 293.6 | 387.9 | 640.1 / 647.6 |
| 8192² CUDA | 276.6 / 802.3 | 261.8 | 269.8 | 513.0 / 555.0 |
| kensa CPU | 275.6 / 672.0 | 179.6 | 348.3 | 601.0 / 615.7 |
| kensa CUDA | 220.8 / 849.7 | 169.6 | 176.1 | 257.2 / 257.9 |

16회 모두 renderer 오류/진단 예외는 없다. 변동이 큰 UI 관측 지연을 서비스 또는 GPU 계산 시간이라고 부르지 않는다. 앞선 PowerShell GUI 비동기 실행으로 두 앱이 겹친 표본은 `overlap-invalid`로 분리하고 위 표에서 제외했다. 최종 실행은 프로세스 종료를 기다려 순차 측정했다.

**kensa의 한 개 검출은 정밀화 `Rejected`다.** 기존 판정 규칙대로 CHECK/Gold를 표시한다. 48개 검출 대응 통과를 위치 정밀화 성공/제품 OK로 바꾸지 않았다. 진단의 `Complete`/`AllVerdictsPass`는 Pass 판정도 요구하므로 이 fixture에서 false다. 별도 `DetectionCorrespondencePassed`로 개체 대응과 공개 CPU/CUDA 동등성만 구별한다.

앱 시간의 최종 표본과 화면은 증거 ZIP의 `app-latency.json`, `app-kensa.json`, `app-results.png`, `kensa-results.png`를 따른다. 캡처는 측정 후 결과 목록을 스크롤한 WPF/DX12 합성 진단 이미지이며 OS 창 스크린샷이 아니다. 이전 화면 캡처를 완료로 오인하지 않도록 기존 진단 캡처 파일을 지운 뒤 새 파일 생성을 기다린다. kensa 첫 진단의 AppData cache 권한 오류는 `app-kensa-cache-denied.json`으로 보존하고 명시적 진단 모드만 workspace cache를 사용하도록 수정했다.

전체 화면 맞춤에서는 인접 검출 라벨이 기존 배치 규칙대로 겹친다. 번호/점수를 생략하지 않았으며 atlas 구현은 표시 자산 준비를 개선한다. 라벨 충돌 회피를 완료한 것으로 표현하지 않는다. 결과 목록의 모든 항목은 유지하며 스크롤로 접근한다.

## 회귀·폐기 실험

관련 suite는 Algorithm 241개, Inspection 103개, 앱 Imaging 217개, Canvas 103개를 검증했다. 주요 새 검증은 다음과 같다.

최종 재실행 총664개 PASS/실패0/skip0, `Format-Code.ps1 -Verify` PASS, `git diff --check` PASS다. 원칙 검토와 별도로 성능 수용은 FAIL로 유지한다.

- 최대512특징·8방향·경계 crop의 scalar/SIMD 국소 정수 점수 일치, 기존/새 후보 pose·점수·순서·한도 동등성.
- 회전/배율이 다른 세 물체의 256 px 창 경계 검색, 기하 모델의 기존 경로 동등성. 기존 마스크·잡음·점수·취소·예산·독립 모드 회귀.
- 모든 샘플 LOD·24K 경계 타일의 기존 RGBA 픽셀 일치, 직접 Gray8/기존 변환 일치, 잘못된 공급자 버퍼 크기 반환 시 해제.
- DPI 1/1.5/2에서 기존 글자와 atlas 영역의 alpha 정확 일치, 기존 결과 overlay 회귀, 논문 전체 결과 목록과 다른 방식 분리.
- mutation 시험: 국소 점수의 `from >= to` 경계 조건을 반대로 바꾸면 집중 테스트 3개가 실패했다. 원상 복구 후 Algorithm 241개 PASS를 확인했다. 테스트/허용 오차를 완화하지 않았다.

| 실험 | 결과와 처리 |
|---|---|
| 연속 점수 SIMD/gather, 회전 변환 미리 계산 | 측정상 개선 없음/악화. 원상 복구 |
| 동일 pose 또는 정수 보정 후 pose 캐시 | dictionary 비용 포함 이득 없음. 원상 복구 |
| 거친 단계 연속 검증 생략 | 8K 약318~377 ms로 악화, kensa 47개/불완전. 폐기 |
| atlas 전체를 하나의 Drawing으로 한 번에 래스터화 | 일부 alpha가 ±1 달라짐. 폐기하고 기존 원점 개별 래스터→복사 사용 |

교차 코드 리뷰에서 이번 변경의 확정 P1/P2 문제는 없었다. 실제 버튼/DWM 완료 미측정을 PASS로 표시하지 말라는 지적을 반영했다. 다음 원칙을 모두 검토했으며 설계 원칙 예외는 없다.

| 원칙 | 검토 근거 |
|---|---|
| SRP / OCP | 입력·검색·표시 책임 분리, 논문 실행 전략과 선택형 원본 Gray 계약 |
| LSP / ISP / DIP | 버퍼 크기·소유·취소 검사, 좁은 영상/텍스트 배치 계약, 앱 조립과 Algorithm의 플랫폼 독립성 |
| Information Expert / Creator | 도형 원본 생성기가 공통 색상 정의 소유, renderer가 atlas/fence 수명 소유 |
| Controller / Low Coupling / High Cohesion | UI는 명령/결과 게시, 검색 수식·영상 생성·텍스트 준비를 별도 책임에 배치 |
| Polymorphism / Pure Fabrication | 선택형 입력/배치 구현과 전용 기술 캐시, 사용하지 않는 범용 엔진 없음 |
| Indirection / Protected Variations | 공급자 비의존 표시 통지, SIMD fallback, 기하/OpenCV의 실행 경로 보호 |

## 남은 작업과 재현

**서비스 자체가 이미 125/80 ms를 넘으므로 수용 시험은 FAIL이다.** 독립 첫 실행 10회·반복100회로 늘려 합격률을 계산할 단계가 아니다. CPU 우선 목표는 유지하며 다음 병목을 해결한 뒤 계획의 전체 수용 시험을 한다.

1. 후보 자세 보정·원본 crop 전처리의 반복 계산을 정확하게 줄여야 한다. 8K 반복 한 표본의 worker 누적 후보 자세 보정 약1084 ms, 전처리 약544 ms가 남는다. 이는 프로파일 우선순위의 근거이며 각각의 wall 기여분은 아니다. 정확성 회귀 없이 거친 검증을 생략하는 실험은 실패했다.
2. 앱 명령/renderer 이후 WPF 표시 관측에 추가 지연이 남는다. 실제 입력 이벤트부터 DWM 표시까지의 trace와 layout/queue 분석이 필요하다. 현재 관측 대기 자체가 보수적으로 늦을 수 있지만 서비스 초과를 뒤집지는 않는다.
3. CUDA P5는 미구현이다. 현재 CPU 잔여 경로와 창별64회 왕복 때문에 GTX1070/MIL 비교 목표를 충족하지 못한다. 장치 초기 준비를 명시적 사전 준비로 분리하는 UX와 전송·배치·필요한 검증 단계 이관을 별도로 구현·검증해야 한다.
4. 큰 ROI/광범위 배율의 실제 입력 성능, 실제 수동 첫 버튼/화면 탐색 직후, 메모리 peak trace, Linux 실실행은 이번 기록의 PASS 범위가 아니다.

```powershell
dotnet build benchmarks/TeachingToolWpf.Matching.Benchmarks/TeachingToolWpf.Matching.Benchmarks.csproj -c Release --no-restore -p:TieredCompilation=false
& ./benchmarks/TeachingToolWpf.Matching.Benchmarks/bin/Release/net10.0-windows/TeachingToolWpf.Matching.Benchmarks.exe artifacts/paper-125ms-work/sample-final.json paper-latency sample 5 both paper
& ./benchmarks/TeachingToolWpf.Matching.Benchmarks/bin/Release/net10.0-windows/TeachingToolWpf.Matching.Benchmarks.exe artifacts/paper-125ms-work/kensa-final.json paper-latency kensa 5 both paper
& ./scripts/Publish-Sample.ps1 -InspectionLatency -OutputDirectory artifacts/publish-paper125
& ./artifacts/publish-paper125/TeachingToolWpf.exe --paper-latency-smoke --paper-latency-capture
& ./artifacts/publish-paper125/TeachingToolWpf.exe --paper-latency-smoke --paper-latency-capture --paper-kensa
& ./scripts/Format-Code.ps1 -Verify
```

서비스 benchmark는 원시 계측 도구이며 exit0 자체가 수용 PASS를 뜻하지 않는다. 앱 진단은 성능/품질 FAIL을 JSON에 남긴다. 원시 JSON·실험·검증 로그는 [증거 ZIP](data/paper-125ms-implementation-20261005.zip)에 보관한다. 시작 당시 미커밋 변경을 포함한 `source-before.zip`과 현재 변경 목록으로 이번 수정 범위를 구별하며 사용자의 기존 변경을 되돌리지 않았다.
