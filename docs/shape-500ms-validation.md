# 8K 형상 검사 500 ms 실측과 검증

측정일: 2026-10-05. 기준 장비: AMD Ryzen 5 5600H(6코어/12논리 프로세서), RAM 32 GB, NVIDIA GeForce RTX 3060 Laptop GPU 6 GB, Windows x64, .NET 10.0.12.

**최종 v4 일반 앱에서 8K PCB의 256핀을 첫 CPU 459.0 ms, 준비 후 GPU 375.0 ms에 찾았다.** 서비스 benchmark 32/32회와 정상 표시 WPF 진단의 서비스 16/16회도 500 ms 이내였고 전체 테스트 878개를 통과했다. 이 결과는 아래 장비·입력·설정·배포 조건의 검증이다.

**최초 CUDA 준비를 포함한 GPU 명령과 숨김 창의 UI 명령 지연은 아직 500 ms를 넘는다.** 정상 표시 진단의 첫 GPU 명령도 907.7 ms/FAIL이며, 별도 파일·큰 ROI·회전·잡음은 이전 회귀에서 정확성 PASS/CPU 4 속도 FAIL이었다. v1/v2 수동 첫 522.5/536.8 ms 실패를 보존한다. 서비스, ViewModel 명령 완료, 실제 화면 Present 완료를 구분하며 일부 통과로 실패 범위를 덮어쓰지 않는다.

## v4 추가 실험과 최종 서비스 검증

일반 앱 v2 첫 실행은 256개를 찾았지만 536.8 ms(읽기/변환 192.7 ms)로 실패했다. `artifacts/shape-500ms-v2-user-state/paper-cpu-first.txt`와 `.jpg`에 실제 상태를 보존했다. v2 기능 smoke는 exit 0/PASS이며 성능 실패와 기능 검증을 구별한다.

다음 실험은 `GradientImage.SampleDirection`에 `AggressiveInlining` 한 줄을 추가하고 기존 샘플 생성의 X 마스크·잡음 계산을 재사용한다. bilinear 산술식·순서·float 반환, 모델·마스크·점수·임계값은 바꾸지 않았다. 별도 v3-probe에서 I/O 6개 `AggressiveOptimization`을 제거한 실험은 지연이 악화되어 모두 복원했다. v3-hoist와 v4-inline 비교에는 복원된 6개 표시가 공통으로 들어 있으며 CPU 매칭 hot method 5개의 과거 제거와 혼동하지 않는다.

| 고정 DLL 실험 | 회수 | 첫 CPU ms | 최대 ms | 500 ms 실행 | GPU 반복 중앙값이 두 방식 모두 더 빠름 |
|---|---:|---:|---:|---|---|
| v3-hoist | 16 | 543.1 | 543.1 | 15/16, FAIL | PASS |
| v3-hoist-confirm | 16 | 485.0 | 485.0 | 16/16 | FAIL — 논문 GPU가 느림 |
| v4-inline | 16 | 450.7 | 450.7 | 16/16 | PASS |
| v4-inline-confirm | 16 | 408.5 | 408.5 | 16/16 | PASS |

네 보고서 모두 정확성과 CPU/GPU 최종 record 동등성을 통과했다. v4 두 실험을 합한 서비스 32회는 모두 통과했으나 실행 간 편차와 일반 앱 부하는 별도다. 원본은 `artifacts/shape-500ms-v3-hoist{,-confirm}.json`, `artifacts/shape-500ms-v4-inline{,-confirm}.json`이며 증거 ZIP의 `experiments/`에 보관한다. 최종 배포는 `artifacts/publish-shape-500ms-v4`이며 전체 suite는 878 PASS/skip 0이다.

포맷·Release Rebuild 후 최종 게시물과 같은 DLL을 사용하는 `artifacts/shape-500ms-v4.json`에서 다음을 확인했다. 각 조합은 첫 1회+반복 7회이며 CPU/GPU 순서를 교대했다. 표의 시간은 서비스 호출 wall time이고 학습·최초 CUDA 준비는 별도다.

| 최종 v4 방식 | 모드 | 첫 실행 ms | 반복 최솟값 ms | 반복 중앙값 ms | 반복 최댓값 ms |
|---|---|---:|---:|---:|---:|
| 논문 | CPU 12 | 399.2 | 315.6 | 336.9 | 357.5 |
| 논문 | GPU 혼합 | 342.4 | 286.0 | 305.6 | 333.5 |
| 기하 | CPU 12 | 386.2 | 309.1 | 321.8 | 391.4 |
| 기하 | GPU 혼합 | 318.4 | 281.8 | 298.8 | 346.7 |

32회 전체 최댓값은 399.1698 ms다. 매회 256개 일대일 검출, 위치 최대 오차 .20502 px, CPU/GPU 최종 record 일치, 잘림·출력 초과 없음, 깨끗한 192핀/사선 64핀의 최소 점수 간격 .0294648을 유지했다. 최초 CUDA 준비는 496.2538 ms이며 준비 후 첫 GPU 검색 342.4179 ms와 합하면 838.6717 ms다. 준비 포함 최초 GPU 사용이 500 ms라는 주장은 하지 않는다. v2와 v4-inline-confirm의 전체 match record 비교도 16회 정확히 일치했다.

## 최종 v4 일반 앱과 정상 표시 WPF 검증

진단 명령 없이 일반 앱을 실행하여 핀 하나를 학습한 뒤 전체 이미지를 검사하고 실제 창을 캡처했다. 아래 시간은 앱에 표시된 검사 서비스 시간이며 클릭부터 최종 화면 Present까지를 잰 시간이 아니다. 두 검사 모두 256개를 찾았고 159특징/3단계/37템플릿을 사용했다.

| 일반 앱 수동 검사 | 서비스 ms | 읽기/변환 ms | 최초 장치 준비 ms | 결과 |
|---|---:|---:|---:|---|
| 논문 CPU 첫 검사 | **459.0** | 182.3 | 해당 없음 | 256개 / PASS |
| 논문 GPU 첫 검사 | **375.0** | 185.4 | **470.6 / 별도** | 256개 / 준비 후 PASS |

실제 화면과 접근성 기록은 `artifacts/shape-500ms-v4-user-state/paper-cpu-first.jpg`, `paper-gpu-first.jpg`, `paper-gpu-detail.jpg` 및 각각의 `.txt`에 보관했다. 확대 화면에서 깨끗한 핀은 1.000, 사선이 있는 핀은 .957로 표시되었다. 이는 선택한 두 핀의 표시값이며 전체 그룹의 최소 간격 .0294648과 구별한다.

별도 프로세스에서 `--shape-target-smoke`를 `WindowStyle=Normal`로 실행하여 실제 ViewModel과 앱 기본값을 검사했다. 논문 CPU 4회 → 논문 GPU 4회 → 기하 CPU 4회 → 기하 GPU 4회 순서다. 사전 검사는 없으며 프로세스의 첫 검사는 첫 논문 CPU 하나다. 학습·최초 화면 로딩은 별도이고 각 명령 전 공개 렌더 통지의 750 ms 정지를 관찰했다. 초기 로딩·안정 관찰 합계는 1026.8 ms다. 이 관찰은 GPU fence idle 보장이 아니며 렌더러·캐시·검사를 중지하지 않는다.

| 정상 표시 WPF | 첫 서비스 ms | 첫 명령 wall ms | 반복 서비스 중앙값 ms | 반복 서비스 최댓값 ms |
|---|---:|---:|---:|---:|
| 논문 CPU | 440.4 | **455.7 / PASS** | 356.0 | 367.4 |
| 논문 GPU | 347.9 | **907.7 / FAIL** | 324.5 | 332.1 |
| 기하 CPU | 347.7 | 354.3 | 402.1 | 408.1 |
| 기하 GPU | 394.6 | 400.4 | 332.2 | 343.7 |

16/16회 정확성·서비스 시간 PASS, CPU 8/8명령 PASS, 최초 GPU 준비를 제외한 전체 명령 15/15 PASS다. 256개 일대일·표시 순서/X/Y/점수의 CPU/GPU 동등성과 렌더 오류 0을 확인했다. 첫 CPU 명령의 정확한 값은 455.7433 ms다. 첫 GPU 명령 907.6588 ms는 내부 CUDA 준비 550.6731 ms를 포함하므로 보고서의 종합 `Status=FAIL`을 유지한다. 준비 후 검사 347.9 ms만으로 첫 GPU 사용 전체를 통과 처리하지 않는다.

명령 wall은 ViewModel 호출 직전부터 await 완료까지이며 결과 게시와 최초 GPU 준비를 포함한다. 표시 서비스 시간은 0.1 ms 정밀도다. 명령 완료가 최종 화면 Present 완료는 아니다. 명령 측정 중 캡처·접근성 탐색·JSON 직렬화를 하지 않았다. 정상 표시 결과는 `artifacts/shape-500ms-v4-wpf-visible/artifacts/shape-target-smoke.json`에 있다.

### 숨김 창 진단의 별도 실패

동일 DLL과 `--shape-target-smoke` 인자를 `WindowStyle=Hidden`으로 실행한 진단에서는 서비스 16/16회와 정확성은 PASS였지만 명령 16/16회가 500 ms를 초과했다. 첫 CPU는 서비스 432.7 ms/명령 1740.3706 ms, 전체 명령 범위는 **1028.5295~2280.9960 ms**였다. 렌더 오류는 0이었다. `artifacts/shape-500ms-v4-wpf/artifacts/shape-target-smoke.json`과 종합 FAIL을 보존했다.

정상 표시에서는 준비된 명령이 통과하고 숨김 창에서는 서비스 밖 대기가 커진 차이를 확인했다. 숨김 창의 스케줄링 영향은 추정이며 원인을 확정하거나 해결한 상태는 아니다. `--shape-target-show`는 완료 후 창을 유지하는 옵션이므로 검사 구현의 차이로 해석하지 않는다. 이 결과를 숨김/최소화 실행이나 모든 UI 부하에서의 500 ms 보장으로 확대하지 않는다.

## 측정 조건과 시간 경계

- 입력은 앱 원본 `sample:8192:8192`다. 표시 LOD나 화면 캡처가 아닌 독립 원본 reader로 검사한다. ROI 중심 `(759.518, 546.178)`, 크기 `105.827×129.459`, 학습 각도 0°인 핀 하나를 사용한다.
- 회전 ±180°/간격 10°, 배율 1, 피라미드 3단계, 특징 상한 160, 최소 gradient 8, 상대 특징 대비 .2를 유지했다. 최소 점수 .72, 지지 .6, 추가 윤곽 가중치 1, 후보 2048/원본 검증 64/거친 검증 256, 출력 256이다. 검색·정밀화·검증을 생략하지 않았다.
- 앱 기본 CPU 병렬도는 논리 프로세서 수를 1~16으로 제한한다. 이번 실측은 12다. 활성 작업 영상 합 2 Mpx, 원본 단일 crop 8 Mpx, 검사별 원본 LRU 16 MiB/축소 LRU 4 MiB를 유지했다. 이 수치는 프로세스 전체 메모리 상한이 아니다.
- 측정값은 `SearchAsync` 호출 직전부터 반환까지의 `WallMilliseconds`다. 원본 reader 개방, 원본 픽셀 생성·변환·읽기, 전처리, 후보 검색, 원본 검증, 결과 조립, GPU lease 반환이 포함된다. 학습, 앱 시작, 파일 가져오기, 최초 CUDA 장치 준비는 별도다. 검사 결과나 전체 입력 영상을 실행 간 캐시하지 않았다.
- CPU/GPU 순서를 반복마다 교대했다. 각 방식/모드의 첫 실행 1회와 반복 7회, 합계 32회를 기록했다. 첫 논문 CPU 실행만 해당 프로세스의 첫 검사다. 다른 조합의 `FIRST_SEARCH`는 같은 프로세스에서 앞선 검사가 끝난 뒤이므로 모두 프로세스 cold라고 부르지 않는다. 학습 후 첫 검사이며 OS 부팅 직후의 cold I/O 시험도 아니다.
- 이전 v1에는 GPU를 먼저 실행한 독립 프로세스도 있다. 최종 v4는 서비스 benchmark, 일반 앱 수동 검사, 정상 표시/숨김 창 WPF 진단을 구별하고 최초 장치 준비·준비 후 검색·전체 명령을 따로 공개한다. 최초 GPU 선택부터 결과까지는 준비 비용도 봐야 한다.
- 단계 6부터 큰 JSON 결과를 각 검사 사이에 직렬화하지 않고, 모든 시간 측정이 끝난 뒤 한 번 저장했다. 보고서 할당·GC가 다음 검사에 섞이는 측정 오염을 줄이기 위한 변경이다. 이전 단계와의 수치 차이 전부를 코드 최적화 효과로 해석하지 않는다.
- reader와 worker가 겹쳐 실행되므로 읽기 시간과 병렬 전처리·검색·정밀화의 진단 시간 합은 전체 wall time의 분해가 아니다.

## 최종 v4 배포 설정과 해시

Release, `win-x64`, ReadyToRun, Crossgen2 `--instruction-set=avx2`, `TieredCompilation=false`를 사용했다. 측정 장비에서 AVX2와 32-byte SIMD가 활성화되어 있었다. 일반 `dotnet build` 산출물의 첫 JIT 지연까지 같은 성능으로 검증한 것은 아니다.

앱 게시 명령은 `scripts/Publish-Sample.ps1 -InspectionLatency -OutputDirectory artifacts/publish-shape-500ms-v4`다. 이 옵션은 해당 게시물에만 적용하며 시스템 환경변수나 일반 라이브러리 빌드 설정을 바꾸지 않는다. 다른 장비·런타임·전원/열 상태의 500 ms 보장은 별도 실측이 필요하다.

최종 benchmark 기록의 DLL SHA256:

| DLL | SHA256 |
|---|---|
| VisionAlgorithm | `1DAFB2D24D9051E5C00CAFC2963DDCBAE22EE0EF930A33061C8C0B0DA33112B9` |
| VisionInspection | `E20304BFDC4B20D480E826B623BF662DE1A49F795A5BCC40B82A11D51FC57793` |
| VisionImaging | `224AA57A661FFC03C3667E4EBDCA3378B2992015355E46AECAD42945772B3B46` |

세 DLL의 앱/benchmark SHA256 일치는 `artifacts/shape-500ms-v4-dll-hashes.json`에 기록했다. 실험 후보와 포맷 후 최종 산출물의 해시·측정값은 구별한다.

## v2 앱 PCB 샘플의 서비스 benchmark 결과

시간 단위는 ms다. 첫 실행은 위에서 구분한 각 조합의 첫 서비스 호출이다. 반복은 조합별 7회다.

| 방식 | 실행 | 첫 실행 | 반복 최솟값 | 반복 중앙값 | 반복 최댓값 | 500 ms |
|---|---|---:|---:|---:|---:|---|
| 논문 기반 | CPU 12 | 473.9 | 364.9 | 374.6 | 397.6 | PASS |
| 논문 기반 | GPU 혼합 | 398.2 | 332.3 | 349.0 | 365.0 | PASS |
| 자체 기하 | CPU 12 | 358.6 | 351.9 | 356.2 | 374.3 | PASS |
| 자체 기하 | GPU 혼합 | 324.6 | 323.1 | 336.6 | 372.5 | PASS |

v2의 32회 모두 500 ms 이내, 전체 최댓값 473.8817 ms다. 반복 중앙값 기준 GPU의 시간 감소는 논문 약 6.8%, 기하 약 5.5%다. GPU가 매번 모든 CPU 개별 실행보다 빠르다는 의미는 아니다. 앞선 v1보다 모든 반복 중앙값이 개선된 것은 아니므로 부분 연산 개선을 그대로 전체 시간 감소로 표현하지 않는다.

모든 실행에서 정답 256개와 일대일 대응했으며 누락·중복·미할당·위치 오차 기준 초과는 0이었다. 최대 위치 오차는 0.20502 px였다. `Truncated=false`, `MatchLimitExceeded=false`이고 CPU/GPU의 최종 match record 동등성은 32회 모두 PASS다.

사선 그룹은 점수로 나누지 않고 샘플 기하의 독립 정답 열 1·6으로 정의했다. 각 실행의 깨끗한 192핀 최저 점수는 0.9995903, 사선 64핀 최고 점수는 0.9701255, 두 그룹 사이 간격은 0.0294648이다. .02 이상이라는 수용 기준을 통과했다. 이 사례는 추가 윤곽 감점의 검증이며 실제 결함 전체의 분류 성능을 뜻하지 않는다.

## GPU 역할과 최초 준비 비용

현재 형상 GPU 모드는 **CPU 전처리 + CUDA 방향 확산·템플릿 후보 점수 배치 + CPU 연속 검증·원본 최종 판정**이다. DX12는 검사에 쓰지 않는다. 방향 정수 점수는 CPU와 동일하며 임계값, 후보 예산, NMS 순서와 최종 점수는 공통 코드가 유지한다.

작은 crop의 반복 전송을 피하려고 이 실행 정책을 고정했다. 예외 발생 후 CPU로 조용히 재검사하는 폴백이 아니다. JSON의 `GpuPreprocessing`은 기존 직렬화 enum 이름이며, 실제 이번 실행에서 GPU 전처리 호출은 0회, GPU 후보 점수 호출은 검사당 64회다. 기존 CUDA 평활화·Sobel 공급자는 별도 계약과 테스트를 유지한다.

| 실행 문맥 | 최초 CUDA 장치 준비 | 준비 후 검사 | 준비 포함 전체 명령 wall |
|---|---:|---:|---:|
| 최종 v4 서비스 benchmark | 496.3 | 342.4 | 별도 명령 없음; 준비+검색 합 838.7 |
| 최종 v4 정상 표시 WPF | 550.7 | 347.9 | **907.7 / FAIL** |
| 최종 v4 일반 앱 수동 | 표시 470.6 | 375.0 | 별도 명령 wall 미측정 |
| v2 서비스 benchmark | 492.4 | 398.2 | 별도 명령 없음; 준비+검색 합 890.6 |
| v2 WPF 프로세스 1 | 600.9 | 396.9 | **1006.9 / FAIL** |
| v2 WPF 프로세스 2 | 514.2 | 423.4 | **946.7 / FAIL** |

**장치 준비까지 포함한 최초 GPU 사용은 500 ms를 넘는다.** 표의 장치 준비는 내부 세션 초기화 시간이다. WPF가 사용자에게 표시한 준비 시간은 프로세스 1에서 601.9 ms로, 호출 경계 차이가 있다. 준비한 `CudaInspectionSession`은 앱이 소유하고 이후 검사는 장치·커널·제한된 작업 버퍼를 재사용한다. 준비 비용은 학습이나 검사 시간에 숨기지 않는다. 이전 v1의 독립 GPU-first 준비 475.3 ms+첫 검색 404.4 ms 기록도 증거 ZIP에 보존했다.

## v2 실제 WPF 두 프로세스 검증 이력

당시 v2 게시물에 `--shape-target-smoke --shape-target-show`를 주어 실제 ViewModel과 앱 기본값으로 실행했다. 각 프로세스는 논문 CPU 첫 1회+반복 3회 → 논문 GPU 4회 → 기하 CPU 4회 → 기하 GPU 4회 순서다. 프로세스의 첫 검사는 첫 논문 CPU 하나이며 사전 검사를 하지 않았다. 학습과 최초 화면 로딩은 별도다.

각 명령 직전 공개 TileReady/HasReady·렌더 통지의 750 ms 정지를 관찰했고, 초기 화면 준비/정지 관찰 합계는 약 1.04초였다. 이는 GPU fence idle 보장이 아니며 렌더러·캐시·검사를 중지하지 않는다. 측정 중 화면 캡처·접근성 탐색·로그 직렬화를 하지 않았다. 화면 준비 전 즉시 클릭하거나 다른 작업이 겹치는 조건까지 통과한 것은 아니다.

| 프로세스 | 방식 / 모드 | 첫 서비스 ms | 첫 명령 wall ms | 반복 서비스 중앙값 ms | 반복 서비스 최댓값 ms |
|---|---|---:|---:|---:|---:|
| 1 | 논문 / CPU | 482.8 | 497.6 | 394.9 | 412.1 |
| 1 | 논문 / GPU | 396.9 | 1006.9 | 374.7 | 374.9 |
| 1 | 기하 / CPU | 397.2 | 403.4 | 411.3 | 432.5 |
| 1 | 기하 / GPU | 414.3 | 420.6 | 384.1 | 411.1 |
| 2 | 논문 / CPU | 483.0 | 498.8 | 407.6 | 427.3 |
| 2 | 논문 / GPU | 423.4 | 946.7 | 371.5 | 375.0 |
| 2 | 기하 / CPU | 418.8 | 424.7 | 458.7 | 461.0 |
| 2 | 기하 / GPU | 388.4 | 394.4 | 391.3 | 410.5 |

서비스 32/32 PASS, CPU 명령 16/16 PASS, CUDA 최초 준비 2개를 제외한 전체 명령 30/30 PASS다. 준비 포함 GPU 첫 명령 2개는 FAIL이므로 두 JSON의 종합 상태도 FAIL이다. 첫 CPU 명령의 정확한 값은 497.5790/498.8380 ms로 500 ms까지 여유가 2.4210/1.1620 ms다. 두 번째 프로세스의 기하 CPU 반복이 더 느렸던 차이도 표에 그대로 공개한다.

32회 모두 완전한 256개 일대일 대응, 최대 위치 오차 약 .20502 px, 표시된 CPU/GPU 순서·X/Y·점수 동등성과 렌더 오류 없음이 확인되었다. WPF의 점수 표시는 소수점 셋째 자리여서 깨끗한 그룹 최저 1.000/사선 그룹 최고 .970/차이 .030이다. 정밀 점수 차이 .0294648은 별도 서비스 보고서의 원래 값이다.

명령 wall은 VM 명령 호출 직전부터 await 완료까지이며 최초 GPU 준비·결과 게시를 포함한다. 표시 서비스 시간은 0.1 ms 정밀도다. 명령 완료가 최종 화면 Present 완료를 뜻하지 않는다. 서비스 benchmark의 교대 순서와 WPF의 모드별 묶음 순서는 다르므로 합쳐 하나의 중앙값을 만들거나 차이를 모두 구현 효과로 돌리지 않는다.

## 구현한 개선과 보존한 계약

1. 원본 명암 변환, 면적 평균, 샘플 픽셀 생성, 평활화·Sobel·방향맵에 정확한 SIMD 경로를 적용했다. scalar fallback과 독립 계산을 비교한다. 샘플 정답 좌표를 검색에 전달하는 특례는 없다.
2. Gaussian 전처리의 전체 float 임시 영상 2개를 수평 5행/평활화 3행 ring buffer로 바꿨다. 경계 clamp, 산술 순서, gradient·magnitude·방향 값을 유지하며 영상 높이에 비례하던 임시 저장을 행 단위로 줄였다.
3. 한 소유자가 원본을 읽고 제한된 worker가 crop을 처리한다. 오래 걸리는 작업을 기다리는 동안 빈 슬롯을 채우되 작업 수·활성 픽셀·미게시 결과를 제한한다. 게시 순서를 복원하고 취소·실패에서 모든 worker 종료를 관찰한 뒤 reader/device를 반환한다.
4. 현재 최선 점수를 수학적 상한으로 넘을 수 없는 pose만 나머지 합산을 생략한다. 부동소수점 여유를 포함한 상한과 독립 전체 계산의 최종 pose/score를 비교한다. 후보 수·회전·배율·정밀화 반복·판정 기준을 낮추지 않았다.
5. CUDA에 여러 템플릿 후보 점수를 묶어 전송하고 GPU 경로가 사용하지 않는 CPU 선형 응답 plane은 만들지 않는다. 원본 판정에 필요한 방향 자료와 CPU 경로의 자료는 유지한다. 외부 공급자의 null·잘못된 grid·불가능한 정수 점수는 오류로 처리한다.
6. 앱/CLI 소유 CUDA 세션에 최초 준비·단일 검사 lease·종료 대기·오류 후 재사용 차단을 모았다. 전역 장치나 실행 결과 캐시는 추가하지 않았다.

CPU의 5개 hot method에 `AggressiveOptimization`을 붙인 실험은 반복 성능이 악화되어 제거했다. 원본 I/O의 6개 메서드(`ProceduralTileSource.Read`, `InspectionGrayConverter.ConvertRow`, `MatchingImageReader.Copy`, `ReadReducedTile`, `InspectionTileReducer`의 2배/4배 축소)에 적용한 표시는 유지했다. GPU 전체 정규화는 약 1 ULP 차이가 최종 결과에 전파되어 폐기했다. 최종 수치는 정확성 완화나 이 두 실험을 포함한 결과가 아니다.

## 실제 앱 첫 실패 후 추가 I/O 개선

v1의 실제 앱 첫 522.5 ms 실패 이후 RGBA/BGRA 변환과 타일 축소를 추가로 최적화했다. v2와 최종 v4 수치는 이 변경을 포함한다. 아래 값은 부분 연산 마이크로벤치이며 전체 검사가 이만큼 빨라졌다는 의미가 아니다. v1 benchmark 32회/865개 테스트와 이전 실패·DLL 해시는 원본 증거에 보존하고 v2의 877개 전체 회귀와 앱 재측정으로 확인했다.

`InspectionGrayConverter`는 AVX2의 인접 byte 곱셈·합산으로 한 번에 16픽셀을 변환한다. BT.601 정수 가중치 `77R+150G+29B`와 `+128` 반올림은 유지한다. 150G를 22G+128G로 나눠 signed byte 가중치의 범위를 지키며 알파는 기존처럼 무시한다. AVX2 lane 순서를 복원한 뒤 기존 8픽셀/스칼라 tail과 미지원 CPU 경로로 이어진다.

`InspectionTileReducer`는 2배/4배 축소를 독립 소유 `PixelBuffer`에 직접 쓴다. 4배 축소도 2×2 평균의 반올림을 두 번 적용하며 단일 4×4 평균으로 바꾸지 않는다. 1배와 다른 배율, 좁은 가장자리, 비지원 CPU는 기존 반복 축소와 행 단위 복사를 사용한다. 원본 LRU view를 다음 원본 읽기 전에 소비하고 취소·실패 시 새 버퍼를 반환한다. 성공한 버퍼만 기존 4 MiB 축소 cache에 넘긴다.

각 수치는 512픽셀 폭에서 총 67,108,864픽셀을 처리한 9회 중앙값이다. SIMD를 지원하는 동일 장비에서 연산 자체를 비교했으며 원본 생성·파일 I/O·검색·UI를 포함하지 않는다.

| 부분 연산 | 이전 ms | 변경 후 ms | 측정 반복당 managed 할당 이전 → 변경 후 |
|---|---:|---:|---|
| RGBA32 → Gray8 | 18.6430 | 7.6267 | 별도 기록 없음 |
| BGRA32 → Gray8 | 18.5128 | 7.8156 | 별도 기록 없음 |
| 타일 2배 축소 | 6.2095 | 1.4358 | 16,805,888 → 10,240 byte |
| 타일 4배 축소 | 7.6939 | 1.4727 | 21,018,624 → 10,240 byte |

529픽셀 폭의 tail 포함 측정도 기록했다. 변경 후 RGBA/BGRA 중앙값은 8.4111/8.4727 ms, 2배/4배 축소는 2.0111/2.0681 ms였다. 할당 10,240 byte는 측정 구간의 managed 신규 할당량이며 ArrayPool이 보유한 배열이나 작업 메모리 전체를 뜻하지 않는다. 원본 증거는 `artifacts/gray-avx2-micro/results.jsonl`, `artifacts/tile-reducer-micro/results.jsonl`이고 해당 파일에 실제 DLL 해시와 실행별 원시 수치가 있다.

추가 집중 테스트 16개는 PASS다. 전수 RGB 정수 oracle, RGBA/BGRA·알파·비정렬·tail, 축소 배율 1/2/4/8/16/32와 작은/홀수/stride 영상, 중간 반올림 반례, 최대 명암과 취소를 검증하며 SIMD/스칼라 경로를 확인했다. 최종 전체 suite는 아래 878개이며 집중 실행을 중복해 합산하지 않는다.

## 별도 파일·큰 ROI·회전·잡음 회귀

추가 I/O 개선 전 v1에서 8192×8192 Gray8 PNG 파일을 가져온 뒤 학습하고 CPU 병렬도 1/4, 논문/기하를 각각 첫 1회+반복 3회 실행했다. 합계 48회다. 아래는 CPU 4의 반복 중앙값/최댓값이다. 최종 v4에서 이 전체 파일 benchmark는 재실행하지 않았으므로 기존 정확성 증거와 미달 이력으로 보관한다. 파일 생성·가져오기·학습은 시간에서 제외하며 프로세스/OS-cache cold 시험은 아니다. 앱 샘플의 CPU 12 조건과 직접 같은 조건으로 비교하지 않는다.

| 입력 / 학습 ROI | 방식 | 검출 수 | 중앙값 ms | 최댓값 ms | 정확성 | 500 ms |
|---|---|---:|---:|---:|---|---|
| Gray8 핀 / 약 106×129 | 논문 | 256 | 792.6 | 798.3 | PASS | FAIL |
| Gray8 핀 / 약 106×129 | 기하 | 256 | 788.2 | 802.0 | PASS | FAIL |
| 큰 chip / 1600×1600 | 논문 | 16 | 1817.6 | 1917.3 | 알려진 위치 PASS | FAIL |
| 큰 chip / 1600×1600 | 기하 | 16 | 1858.3 | 1883.6 | 알려진 위치 PASS | FAIL |
| 회전·배율·잡음 / 512×512 | 논문 | 16 | 600.2 | 605.3 | PASS | FAIL |
| 회전·배율·잡음 / 512×512 | 기하 | 16 | 603.3 | 608.0 | PASS | FAIL |

핀은 전체 256개 일대일 위치 기준, 회전·잡음은 전체 16개 위치 1 px/각도 1°/배율 오차 .01 기준을 유지했다. 큰 chip은 알려진 중심 16개 대응을 검사하며 대칭 형상 전체의 false-positive oracle로 표현하지 않는다. 기록된 48회 모두 첫 실행과 최종 결과가 정확히 일치하고 잘림이 없었다. 이 회귀에 GPU 성능 수치는 없다. 파일 경로와 큰 ROI의 추가 속도 개선, 실제 촬영 데이터, Linux 실실행, 이번 변경 후 24K 전체 검사 500 ms는 미검증 또는 남은 과제다.

## 최종 v4 자동 검증과 실제 앱 이력

| 항목 | 결과 | 증거 |
|---|---|---|
| Core | 182 PASS | `artifacts/shape-500ms-v4-tests/TeachingToolWpf.Core.Tests.trx` |
| Algorithm | 236 PASS | `artifacts/shape-500ms-v4-tests/VisionAlgorithm.Tests.trx` |
| Inspection | 98 PASS | `artifacts/shape-500ms-v4-tests/VisionInspection.Tests.trx` |
| CUDA | 52 PASS | `artifacts/shape-500ms-v4-tests/VisionCompute.Tests.trx` |
| Canvas | 100 PASS | `artifacts/shape-500ms-v4-tests/VisionCanvas.Tests.trx` |
| 앱 | 210 PASS | `artifacts/shape-500ms-v4-tests/TeachingToolWpf.Imaging.Tests.trx` |
| 합계 | **878 PASS, 실패 0, skip 0** | 집중 재실행을 합계에 중복 포함하지 않음 |
| SIMD 미사용 독립 회귀 | 26 PASS | `artifacts/cpu-stage6-ring-scalar-tests.log` |
| 포맷 검증 | PASS | `artifacts/shape-500ms-v4-format-verify.log` |
| Release 전체 Rebuild | 경고 0 / 오류 0 | `artifacts/shape-500ms-v4-build.log` |
| 배포 | PASS | `artifacts/shape-500ms-v4-app-publish.log` |
| 최종 v4 일반 앱·실제 화면 | CPU 459.0 ms / 준비 후 GPU 375.0 ms / 각각 256개 PASS | `artifacts/shape-500ms-v4-user-state/`의 화면 3개와 접근성 기록 |
| 최종 v4 정상 표시 WPF | 서비스 16회·CPU 명령 8회 PASS / 최초 GPU 준비 포함 명령 FAIL | `artifacts/shape-500ms-v4-wpf-visible/artifacts/shape-target-smoke.json` |
| 최종 v4 숨김 창 WPF | 서비스 16회·정확성 PASS / 명령 16회 FAIL | `artifacts/shape-500ms-v4-wpf/artifacts/shape-target-smoke.json` |
| 최종 v4 네이티브 앱 기능 smoke | PASS, exit 0 | `artifacts/shape-500ms-v4-runtime/artifacts/inspection-smoke.log` |
| 최종 v4 SIMD 미사용 샘플 생성 회귀 | 3 PASS / `DOTNET_EnableHWIntrinsic=0` | `artifacts/shape-500ms-v4-scalar-generator.log`; 878개에 중복 합산하지 않음 |
| v1 네이티브 앱 기능 smoke | PASS, exit 0 | `artifacts/shape-500ms-runtime/artifacts/inspection-smoke.log` |
| v2 네이티브 앱 기능 smoke | PASS, exit 0 | `artifacts/shape-500ms-v2-runtime/artifacts/inspection-smoke.log` |
| v2 일반 앱 수동 첫 CPU | **256개 / 536.8 ms / FAIL** | `artifacts/shape-500ms-v2-user-state/paper-cpu-first.txt`와 `.jpg` |
| v2 실제 WPF 프로세스 1 | 정확성·서비스 PASS / GPU 최초 준비 포함 명령 FAIL | `artifacts/shape-500ms-v2-wpf/artifacts/shape-target-smoke.json` |
| v2 실제 WPF 프로세스 2 | 정확성·서비스 PASS / GPU 최초 준비 포함 명령 FAIL | `artifacts/shape-500ms-v2-wpf-second/artifacts/shape-target-smoke.json` |

이전 실패 이력: v1 실제 앱 첫 논문 CPU 검사는 522.5 ms(읽기/변환 275.4 ms), 후속은 458.7/462.2 ms였다. 당시 첫 실패를 반복 통과로 대체하지 않고 `artifacts/shape-500ms-user-state/paper-cpu-first.txt`와 `.jpg`에 보존했다. 학습 159특징/3단계/37템플릿과 결과는 동일했다. 추가 I/O 개선 뒤의 v2 첫 서비스 482.8/483.0 ms와 명령 497.6/498.8 ms를 별도 측정했다. v2의 화면 안정 관찰과 v1 수동 실행의 조건 차이가 있으므로 그 시간 차이 전부를 코드 개선의 인과 효과로 단정하지 않는다.

독립 산술 회귀는 ring 경계/작은 영상/stride, 방향 LUT, 면적 평균, bilinear 방향 표본, 상한 생략 전후 pose/score를 다룬다. CUDA는 정수 점수, 공급자 오류, 준비·검사·종료 경쟁과 취소·실패 수명을 검증한다. I/O는 단일 reader, 활성 예산, 느린 선행 작업, 결과 순서, native 읽기 실패 후 worker 종료를 검증한다.

## 원본 증거와 설계 검토

원본 보고서는 [압축 증거](data/shape-500ms-20261005.zip)에 보관했다. 최종 서비스 32회는 `v4/shape-500ms-v4.json`, DLL 비교는 `v4/shape-500ms-v4-dll-hashes.json`이다. `v4/wpf-visible/`과 `v4/wpf-hidden/`에 각각의 종합 FAIL도 그대로 보존했다. `v4/manual/`에는 실제 화면 3개와 접근성 기록, `v4/inspection-smoke.log`와 `v4/scalar-generator.log`에는 기능·scalar 회귀 증거를 넣었다. v2의 교대 32회/WPF 두 프로세스/수동 실패 화면, v1의 32회·독립 GPU-first·파일 회귀도 삭제하지 않았다. `experiments/`에는 v3/v4 후보 실험, `micro/`에는 추가 I/O 부분 측정을 넣었다. 각 보고서에 실행별 설정, 시간, 해시, 최종 match, 정답 대응이 들어 있다.

수용 계약은 [계획](../tasks/shape-500ms-plan.md), 구현 결정과 SOLID 5개/GRASP 9개 전체 검토는 [ADR-026](decisions/0026-8k-shape-search-latency.md), 완료 상태는 [작업 목록](../tasks/todo.md)을 따른다. 설계 원칙 예외를 적용하지 않았다.
