# 첫 수직 구현 검증 — 2026-10-06

## 완료 범위

**PASS**: 독립 net10.0/x64 솔루션, CPU 전용 참조 그래프, CUDA 사전 smoke, Gray8/Gray16/Float32의 Flat-field → Gaussian → Black-hat → Threshold, 실제 연속 CUDA 실행, 사각 형태학 6종, 입력/보정/메모리 계약, 독립 정확도 테스트, ROI/타일, PGM/Raw adapter, 재현 benchmark.

**NOT_IMPLEMENTED**: 단계 A 나머지, B/C, 논문 Median 최적화. [필터 × 자료형 상태표](filter-status.csv)에 명시했다. 이 문서는 엔진 전체 완성/산업 현장 검증 완료 보고가 아니다.

## 장비와 실행 조건

- 현재 저장소/브랜치: TeachingToolWpf / main. 새 `IndustrialVision.Filters/` 안에서만 소스 변경. 커밋·푸시 없음.
- SDK 10.0.401, 런타임 .NET 10.0.12, Windows 10.0.26200 / x64.
- AMD Ryzen 5 5600H, 논리 프로세서 12. CPU 기본 병렬도 12.
- NVIDIA GeForce RTX 3060 Laptop GPU, driver 581.83, nvidia-smi 6144MiB, ILGPU 장치 메모리 6,441,926,656 bytes.
- ILGPU 1.5.3 CUDA, MathMode.Default. CPU accelerator/OpenCL은 사용하지 않았다.
- Release/x64. 벤치마크는 tiered compilation을 껐고, 첫 실행과 3회 warmup 후 표본을 분리했다. 전력 정책을 바꾸지 않았고 다른 OS 작업은 통제하지 않았다.
- [환경 JSON](../results/final/environment.json), [정확도 TRX](../results/final/accuracy.trx).

## 정확도

204 tests PASS, FAIL 0, SKIP 0. 실제 CUDA가 있는 이번 환경의 결과다. 없는 장비에서는 `CudaFact`가 사전 CUDA smoke의 부재 결과를 보고 Skip한다. 실제 GPU가 없는 별도 PC에서의 실행은 NOT_RUN이다.

CPU는 scalar 단일 스레드와 Vector256/4스레드를 각각 독립 참조와 비교했다. 참조는 직접 2D Gaussian과 전수 사각 extrema이다. 제품 코드의 분리 convolution, deque, CUDA 커널을 참조에 재사용하지 않았다. Gray16 정수 차이 1, 4095/16383/65535, 1×1/1×N/N×1, 큰 반경 대비 작은 영상, 홀수 크기, padded stride/비연속 ROI, 좌표/노출/맵 불변성, invalid 분모/마스크, NaN/Infinity 거부, in-place 거부, 취소, fallback와 구현 오류 전파를 검사했다.

상수·impulse·수평/수직 ramp·checkerboard·고정 seed 난수·salt/pepper·Gaussian noise·작은 점·끊긴 선·가까운 두 영역·배경 위 낮은 대비 결함을 CPU 참조 비교에 포함했다. CUDA는 세 자료형, 여러 퇴화 크기와 고정 seed 난수, invalid 마스크, tile/full, 원본 device 입력 재사용을 검증했다. CUDA의 모든 잡음 조합을 독립적으로 전수 검사했다는 뜻은 아니다.

정수 extrema/hat/threshold는 exact. Float32 경로의 사전 기준은 `abs(error) <= 0.06 + 1e-6*abs(reference)`이며 테스트 실패를 이유로 넓히지 않았다. 초기 실패 중 Float32 hat 참조가 Float32 저장 반올림을 빠뜨린 것을 바로잡았다. CUDA dynamic launcher의 인자 타입 접근 제한도 실기에서 찾아 수정했다. 이 허용 기준은 16비트 범위와 분수값의 시험 범위에 대한 수치 검증이며 임의의 Float32 동적 범위나 후단 판정 경계의 무조건 동일성을 보장하지 않는다.

타일은 Gaussian 2 + Closing 3×2 = halo 8을 누적해 전체 결과와 bit-exact 비교했다. SourceContext/Isolated가 실제로 다른 결과를 내는 사례, 예산에 맞춘 타일 축소, abort/complete 계약을 검사했다. **65536×65536 Gray16 가상 소스(8GiB 주소 범위)**에서 작은 ROI를 읽는 테스트도 통과했다. 실제 8GiB 파일 전체 처리 성능은 측정하지 않았다.

미세 결함 시험에서는 배경1000/한 픽셀900에 Gaussian(radius3,sigma1.5)을 적용했다. 같은 `<950` 후단 규칙의 결함 면적이 1→0, 대비가100→10미만으로 감소하여 FN 1이 발생한다. 이 사례를 정상 동작 검증과 함께 남겼다. 실제 SMT/반도체/X-ray 정답 데이터의 미검출·과검출·연결성 검증은 NOT_RUN이다.

## 성능

첫 recipe는 reciprocal gain1.02 → Gaussian(5×5,sigma1.1) → Black-hat(사각7×7) → threshold다. Gray16 threshold는500. 모든 실행은 실제 출력을 읽어 checksum을 확인하며 CPU/CUDA 최종 마스크를 bit-exact 비교했다. 단독 Float32 출력도 수치 오차를 확인했다. 4K Gray16 Gaussian 단독의 최대 CPU/CUDA 차이는0.01171875였다.

다음 표는 Standard의30표본, 8K는 Large의10표본이다. p95는 nearest-rank이며 특히10표본의 p95는 표본 최대값으로 해석해야 한다. p99는 충분한 표본이 없어 보고하지 않는다.

| Gray16 정사각 크기 | CPU Host→Host p50/p95 ms | CUDA Host→Host p50/p95 ms | CUDA 상주·완료동기화 p50/p95 ms |
|---|---:|---:|---:|
| 256 | 1.237 / 1.379 | 0.832 / 0.973 | 0.100 / 0.111 |
| 1024 | 17.332 / 20.430 | 10.234 / 10.679 | 0.516 / 0.861 |
| 4096 | 290.941 / 337.327 | 252.007 / 307.912 | 10.198 / 11.314 |
| 8192 | 1475.412 / 1837.456 | 636.197 / 649.132 | 27.053 / 27.876 |

GPU가 모든 연산에서 빠르지 않다. 같은 Standard의4K Gray16 Gaussian 단독 p50은 CPU129.199ms / CUDA242.094ms였다. 따라서 이 결과를 해상도 하나로 만든 보편적 crossover나 모든 필터 가속 근거로 사용하지 않는다. CpuGpu 기본 정책은 단일 단계 CPU, 연속 다단계 CUDA이며, 단독 GPU benchmark만 PreferGpu로 고정한다.

8K 파이프라인은 매 실행 upload268,435,456B + download268,435,456B다. 현재 Float32 staging/Gray8 host 변환 비용을 모두 Host→Host 시간에 포함한다. 상주27.053ms를 Host→Host636.197ms 대신 인용하면 안 된다. 상수 맵/커널은 compile에 준비하며 그 비용은 CompileMs/ColdBackendMs로 따로 남겼다. 매 입력 영상 준비·검사 처리를 compile에 숨기지 않았다.

8K Gray16 plan의 계상 메모리는 CPU host1,477,181,460B, CUDA host671,088,640B + device1,610,612,756B이다. CPU 실행별 managed allocation p50은36,992B, CUDA는304B였다. borrowed 입력, benchmark 기준 출력, pool 잔류 배열, CLR/driver는 이 수치 밖이다. 이는 프로세스 working set이나 VRAM 드라이버 실제 peak 측정값이 아니다. device 값은 해당 program이 소유한 동시 생존 배열의 정확한 payload 합이다. 원본 map의 compile upload는 warm 실행 전송량에 포함하지 않고 compile 시간과 GPU 작업 메모리에 포함한다.

원시 표본, cold/compile, 장치·이유, MPix/s, 메모리, 전송량, allocation, checksum, 최대 오차:

- [Smoke JSON](../results/final/Smoke.json) / [CSV](../results/final/Smoke.csv)
- [Standard JSON](../results/final/Standard.json) / [CSV](../results/final/Standard.csv)
- [Large JSON](../results/final/Large.json) / [CSV](../results/final/Large.csv)

성능 실행기는 외부 라이브러리를 실행하지 않았다. OpenCV CPU/CUDA·HALCON, R1/R2/R3 공식 구현 비교는 NOT_RUN이다. 15/31/63 커널 성능, 다른 세 가지 요구 파이프라인, 여러 CPU/GPU 혼합 구간, 전송 overlap, 실제 수GB 파일의 전체 처리 성능도 남아 있다.

## 배포·남은 작업

CPU 기본 콘솔을 별도로 빌드/실행하고 `.deps.json`에 Console/Core/Cpu/Pipeline 네 프로젝트만 있음을 확인했다. GPU/ILGPU runtime package 항목은 없다. GPU 옵션 예제는 RTX3060 실기로 실행했다. 비CUDA 장치와 Linux, GPU 드라이버가 설치되지 않은 별도 물리 PC는 이 환경에서 실기 확인하지 않았다.

CPU 전용 publish 폴더(`artifacts/cpu-only`)에도 ILGPU 파일이 없는 것을 확인하고 그 폴더의 DLL로 실행했다. [CPU 의존성](../results/final/cpu-only.deps.json), [publish 로그](../results/final/cpu-publish.log), [CPU 콘솔](../results/final/cpu-only-console.log), [CUDA 콘솔](../results/final/gpu-console.log)을 보관한다.

최종 독립 Release 빌드는 경고0/오류0, 독립 솔루션 whitespace/style/name 검사 PASS다. 저장소 지침의 `scripts/Format-Code.ps1 -Verify`도 Windows SDK 경로 접근 제한을 해소하여 PASS를 확인했다. 기존 소스는 수정하지 않았다. [빌드 로그](../results/final/build.log), [테스트 로그](../results/final/test.log), [기존 저장소 형식 검사 로그](../results/final/existing-repo-format.log)를 보관했다.

문헌 R1/R2/R4/R5 원문과 R3 저자 자료·공식 코드를 조사했지만 논문 최적화 포팅은 하지 않았다. 라이선스 확인 상태와 코드 미복사를 [출처 기록](sources.md)에 구별했다. 다음 작업은 A의 나머지부터 정확도 참조→CPU→CUDA/혼합→실측 순서로 이어간다.
