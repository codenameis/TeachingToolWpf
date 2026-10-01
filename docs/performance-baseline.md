# 성능 측정 기록 · 2026-10-02

## 조건과 지표

Ryzen 5 5600H / RAM 32 GB / RTX 3060 Laptop, NVIDIA 드라이버 32.0.15.8183(2025-11-04), 전원 연결, Windows 균형 조정 모드에서 측정했다. DXGI 전용 메모리 5.9 GiB, 당시 local budget 5.1 GiB였다. Release/.NET 10, 디버거 미연결, 성능 실행의 DX12 debug layer는 껐다. 내장 GPU는 측정하지 않았다. 온도와 실제 모니터 scanout 주기는 기록하지 못했다.

뷰포트는 **1920×1080 물리 px / DPI 150%**, 창은 화면 밖 진단 HWND다. 따라서 아래 FPS는 새 영상 프레임을 처리한 **Present 제출률**이며 실제 모니터에 표시된 FPS/입력 지연이 아니다. CPU 시간은 렌더 스레드의 fence 대기·프레임 준비·명령 생성까지, GPU 시간은 timestamp query이며 back buffer 재사용 때 이전 결과를 읽는다. `source_to_submit_ms`는 합성 CPU 프레임 작성 완료부터 Present 반환까지로, 카메라 노출/수집 및 합성 원본 복사 시간은 제외한다. 생산 FPS를 별도로 기록한다.

## 8K / ROI 500개 · 3회 반복

입력은 **7680×4320** 전체 프레임을 CPU에서 만드는 합성 소스, 목표 60 FPS다. ROI 500개를 생성하고 전체 Fit 상태로 시험했다. 실행당 70초, metrics clock 앞 10초 제외, 실제 유효 측정 구간은 시작/종료 타이밍에 따라 59.87–60.89초였다. 화면 축소용 area average와 가시 영역 업로드를 사용했다. 전형적인 출력 준비 크기는 1920×1080이며 전체 8K 원본은 CPU에 남는다. 원시 파일은 [benchmarks-final](../artifacts/benchmarks-final/summary.json)에 연결한 각 CSV/JSON이다.

| 형식 / 반복 | 제출 FPS | 생산 FPS | CPU p95 ms | GPU p95 ms | ready→Present p95 ms | 드롭 | private peak MiB |
|---|---:|---:|---:|---:|---:|---:|---:|
| Gray8 / 1 | 59.488 | 59.997 | 8.842 | 5.676 | 9.385 | 31 | 477.8 |
| Gray8 / 2 | 59.240 | 59.974 | 10.221 | 6.518 | 10.552 | 44 | 490.0 |
| Gray8 / 3 | 59.327 | 59.995 | 9.406 | 5.708 | 9.915 | 40 | 464.9 |
| Gray16 / 1 | 59.839 | 59.990 | 8.672 | 6.399 | 9.908 | 9 | 634.7 |
| Gray16 / 2 | 59.949 | 59.999 | 6.671 | 6.252 | 8.386 | 3 | 699.4 |
| Gray16 / 3 | 59.830 | 60.014 | 7.150 | 6.090 | 8.488 | 11 | 627.7 |
| BGRA32 / 1 | 59.969 | 60.002 | 9.930 | 7.060 | 10.826 | 2 | 1037.4 |
| BGRA32 / 2 | 59.725 | 59.858 | 14.904 | 7.088 | 16.164 | 8 | 1436.7 |
| BGRA32 / 3 | 59.300 | 59.968 | 16.062 | 7.008 | 17.139 | 40 | 1175.1 |

각 지표의 p50/p95/p99는 [summary.json](../artifacts/benchmarks-final/summary.json)에 모두 보관했다. 표시률이 가장 낮은 반복의 프레임 제출 간격은 다음과 같다. 단순 평균 FPS로 긴 간격을 숨기지 않는다.

| 최저 FPS 반복 | 간격 p50 / p95 / p99 ms | ready→Present p50 / p95 / p99 ms |
|---|---|---|
| Gray8 / 2 | 15.928 / 28.356 / 31.699 | 4.021 / 10.552 / 16.282 |
| Gray16 / 3 | 16.025 / 27.699 / 33.005 | 5.660 / 8.488 / 11.128 |
| BGRA32 / 3 | 15.843 / 24.705 / 29.126 | 10.364 / 17.139 / 21.409 |

모든 반복에서 B2의 ready→Present p95 50ms 목표 안에 들어왔지만 **완전한 60 FPS/무드롭 보장은 아니다**. B1의 p95 20ms 목표를 B2에 그대로 적용하면 위 반복들은 만족하지 않는다. 8192×8192 실시간, B1의 연속 ROI 드래그, B4의 10,000 ROI GPU 조작, B5의 10분 연속 안정성은 별도 미검증이다. 실제 파일/카메라, 모니터 표시 및 열 조건을 고정한 제품 성능 인증으로 해석하지 않는다.

## 병목과 개선 근거

이전 전체 업로드 경로는 같은 8K 소스에서 ROI 없이 Gray8 약 58.3–59.2 FPS, Gray16 약 54.8–55.8 FPS, RGBA32 약 30 FPS였다. 컬러 GPU p95가 약 20ms였고 원본 126.56 MiB를 프레임마다 올리는 비용이 컸다. 원시는 [이전 측정](../artifacts/benchmarks/summary.json)에 있다. 이후 현재 배율/가시 영역 파생 프레임을 만들고 upload를 줄였다. 이전 RGBA/현재 BGRA 및 ROI 수 조건이 다르므로 이 표로 정밀한 배속 비율을 주장하지 않는다.

처음 시험한 매 프레임 NetVips shrink는 CPU 평균 약 111ms, 표시 약 8.9 FPS로 오히려 느려 채택하지 않았다. 최종 AVX2 area average는 동일한 정수 반올림 규칙의 scalar 구현과 바이트 단위 비교 테스트를 통과했다. 1:1은 factor 1 복사이므로 원본 픽셀을 변경하지 않는다. 이 변경을 위해 설계 원칙 예외를 사용하지 않았다.

순수 Core의 무작위 회전 ROI 계층 생성은 BVH 도입 전/후 1,000개 37.3→25.5ms, 10,000개 1566.0→139.2ms였다. 이 숫자는 단일 cold 실행이며 정식 반복 벤치마크가 아니다. 할당은 10,000개 기준 약 4.0→15.5 MB로 증가했다. [before](../artifacts/core-baseline.json), [after](../artifacts/core-optimized.json). 메모리와 후보 검색 시간의 교환이며 렌더 FPS 개선치로 읽지 않는다.

## 24K × 131072 완성 파일

512px tile / Deflate BigTIFF, 세로 반복 패턴을 libvips로 생성했다. 파일은 압축이 잘 되는 합성 데이터로 Gray8 약 25.7 MiB, Gray16 약 45.7 MiB다. native `.v` 캐시를 새로 생성한 cold와 같은 캐시의 warm open을 구분했다.

| 입력 | 디코드 크기 | cold import | warm open | private peak | working set peak | 마지막 타일 첫 값 |
|---|---:|---:|---:|---:|---:|---:|
| Gray8 | 3 GiB | 9.082 s | 11.25 ms | 278.4 MiB | 1.37 GiB | 224 |
| Gray16 | 6 GiB | 22.449 s | 5.50 ms | 337.3 MiB | 4.60 GiB | 57568 |

원시는 [large-image-results.json](../artifacts/large-image-results.json). 마지막 타일 `(level 0, x 47, y 255)`을 읽어 4 GiB를 넘는 주소의 값과 형식을 확인했고 실제 GPU 뷰포트에도 표시했다. 337 MiB만으로 6 GiB 파일을 처리했다고 표현하지 않는다. OS 매핑/파일 캐시 working set은 별도로 크게 잡힌다. 피라미드 전체 생성은 디스크 비용을 지불하며, 원본 전체 managed 배열을 만들지는 않는다. 최초 overview 생성이 import에 포함되므로 warm 개방 시간을 최초 화면 로드 시간으로 제시하지 않는다.

## 재현

README의 benchmark 명령을 형식별 3회 실행한 뒤 `scripts/Summarize-Benchmark.ps1`을 실행한다. 백분위는 nearest rank이다. `private_bytes`는 1초 주기 process private bytes, `texture_bytes`는 논리 이미지 texture 크기로 upload/back buffer/정렬/지연 해제분을 포함한 실제 VRAM 사용량이 아니다. managed allocation/GC, dispatcher 지연, 실제 모니터 scanout, cache hit ratio의 종합 계측은 아직 없다.
