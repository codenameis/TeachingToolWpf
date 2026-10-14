# 스트리밍 파일 엔진 검증

2026-10-13, Windows11 x64 / .NET10 Release. Ryzen5 5600H, RAM32GiB, RTX3060 Laptop. 로컬 구현·검증이며 커밋·푸시하지 않았다. [ADR-043](decisions/0043-streaming-file-engine.md), [실행법](../experiments/VisionImaging.Codecs/README.md), [계획](../tasks/streaming-image-loader-plan.md)을 따른다. Linux는 사용자 선택에 따라 환경 준비 후 실제 검증한다.

## 구현 결과와 현재 판정

TIFF/BigTIFF·PNG·JPEG를 행/타일 단위로 해제하는 native 코덱, C# raw 피라미드, 제한 매핑 reader, 소유 cache 회수/복구, 선택형 WPF·샘플·검사 CLI를 연결했다. 파일 경로에 NetVips/OpenCV 전체 Mat이 필요하지 않다. PNG Adam7은 scratch 파일, progressive JPEG는 예산 안의 계수 RAM을 사용한다. **외부 코덱 의존성이 없어진 것은 아니다.**

Windows 선택 경로의 기능·픽셀·검사 검증은 통과했다. 기본 로더는 NetVips를 유지한다. 엄격한60FPS와 전체 플랫폼/모든 입력 호환성 수용은 미완료다. 기존 코드의 성능 미달을 이번 파일 준비 개선으로 해결했다고 표현하지 않는다.

## 동일 조건5회 비교

입력마다 두 구현의 순서를 번갈아 각각5회, 새 프로세스·새 캐시로 실행했다. OS 파일 캐시/전원/열을 통제하지 않았으며 cold disk 수치가 아니다. 다른 빌드/성능 작업과 병렬 실행하지 않았다. 시간/메모리는 각 실행값의 중앙값이다. private는 프로세스 전용 할당, working set은 RAM 상주이며20ms 표본 피크다. native·managed·OS 매핑을 모두 포함하지만 시스템 전체 파일 캐시는 아니다.

| 입력 | 준비 NetVips → 자체(ms) | private NetVips → 자체(MiB) | working set NetVips → 자체(MiB) | warm 타일 p95 NetVips → 자체(ms) |
|---|---:|---:|---:|---:|
| 8192² Gray8 PNG | 261.7 → 228.0 | 31.1 → 18.7 | 73.5 → 51.2 | 1.35 → 0.81 |
| 8192² RGB JPEG | 896.2 → 791.6 | 76.9 → 32.0 | 381.0 → 63.7 | 2.25 → 1.13 |
| 24576×8192 Gray8 TIFF | 699.5 → 385.0 | 80.1 → 35.2 | 273.6 → 60.5 | 1.45 → 0.91 |
| 24576×8192 Gray16 TIFF | 1391.7 → 569.8 | 125.7 → 34.8 | 516.3 → 66.5 | 1.71 → 0.96 |

최종 작업 버퍼 계측을 포함한40회는 모두 PASS. 재개방/p99 및 숫자 원본은 [요약 JSON](streaming-image-loader-results.json), 원시40개는 `artifacts/streaming-codecs/paired-20261013-224805/reports.json`이다. 자체 작업 배열 피크의5회 중 최대는 표 순서로2.31/9.25/8.00/10.50MiB였다. 각 원시 기록에 입력 SHA256·managed/native 해시·로드 모듈이 있으며 로드된 비교용 managed assembl0-=['y의 Release 최적화를 확인했다. native SHA256은 `803334FDEDC60405C82DEB9CDB7CF80ED171CB198A1687DB67437A34548E4C40`이며 최종 빌드의 LERC 비활성화 고정 후에도 동일했다. 앞선 계측 추가 전40회 PASS 기록 `paired-20261013-221159`도 보존한다.

앞선 `paired-20261013-214441`에서는 NetVips JPEG1회가 기존 DiskPyramidSource의 Directory.Move 접근 오류로 실패했다. 작은 fixture의 이전 Deflate 비교도 같은 기존 경로에서1회 실패 후 새 캐시 재실행은 통과했다. 이 실패를 자체 코덱 실패나 성공 시간에 섞지 않는다. 원인 해결을 주장하지 않으며 로그를 보존했다. 최종 반복은 실패를 숨긴 재시도가 아니라 전 입력의 새5회 세션이다.

## 초대형 TIFF와 메모리 변경

24576×131072, 원본 Gray8=3GiB / Gray16=6GiB, tiled Deflate BigTIFF. 이 단락은 **각1회**이며5회 중앙값과 구별한다.

| 입력 | NetVips → 자체 준비(s) | private(MiB) | working set(MiB) | warm tile p95(ms) |
|---|---:|---:|---:|---:|
| Gray8 | 8.00 → 7.63 | 90.4 → 46.6 | 1120.5 → 77.9 | 1.44 → 1.04 |
| Gray16 | 20.75 → 19.22 | 134.5 → 45.3 | 3721.6 → 76.2 | 1.96 → 1.20 |

원본과 모든 축소 단계의 결정적 타일 표본은 차이0이며, 마지막 타일과4GiB 이후 offset을 포함한다. 전체3/6GiB의 모든 픽셀 동등성을 증명한 것은 아니다. 기록은 `artifacts/streaming-codecs/large-results`에 있다. 사용자는 **캐시를 보존하고 초대형은1회 결과로 기록**하도록 확정했다. `large-repeated-caches`와 원본·결과를 삭제하지 않았으며 추가 반복은 이번 범위에서 제외한다. 이 초대형 실행은 마지막 작업 배열 계측 추가 전의 실행물이며 원시 기록의 해시로 구별한다.

같은 Gray16의 개발 중1회씩 관찰한 working set은 전체 매핑6115MiB → 준비 행 묶음865.7MiB → 타일 매핑 창까지 제한78.5MiB였다. 준비 시간16.56/17.12/16.66s로 비슷했으나 반복/OS 조건이 통제된 최종 비교가 아니다. 이 수치만으로 전체 매핑 대비 타일 지연 개선을 주장하지 않는다.

## 형식·오류·수명

- 공통 피라미드 테스트24 PASS: 기존 원본/홀수 크기/Gray16/반올림, 창 경계 왕복, 동시 준비, 취소, 손상 복구, 캐시 예산/살아 있는 reader/임의 파일 보존. 추가6조건은 Gray8/Gray16/RGBA의 최대4MiB 행 및 배열 풀 크기 경계 직후 너비를 실제 준비하고 가장자리 원본·재열기를 검사한다.
- native/로더 테스트26 PASS: LE/BE·white-is-zero TIFF, PNG6개 일반/Adam7 조합 전 픽셀, palette/저비트/tRNS, progressive, 잘린 입력/CRC, 작은 버퍼/순서/취소/중복 Dispose, 파일 version, 독립 reader, 생성/live의 코덱 미호출.
- 29개 실제 fixture/대표 파일의 원본·각 피라미드 단계 비교 표본이 모두 EXACT_SAMPLED. TIFF4압축의 Gray8/16 strip/tile, RGB/straight RGBA, JPEG gray/sequential/progressive, Adam7, 홀수 RGBA/Gray16, 8K/24K. `artifacts/streaming-codecs/quality-final-20261013-220156`.
- 큰 무압축 TIFF strip은 LibTIFF 자체 strip chopping으로 제한 읽기가 가능했다.256MiB 압축 버퍼를 요구하는 유효 PackBits strip은8MiB native 예산에서 오류,512MiB에서 동일 첫 행 읽기 성공. 최초 테스트의 “큰 무압축 strip이면 반드시 거부” 가정을 소스의 strip chopping 동작으로 바로잡았다. 네이티브 allocator 오류는 현재 InvalidDataException에 원문 이유로 전달된다.
- 독립4프로세스가 같은 캐시를 열어 완료 항목1개만 생성. 준비 프로세스를0.raw 기록 중 중단한 뒤 재열어 orphan partial1→0, 정상 게시/읽기 PASS. `artifacts/streaming-codecs/concurrency-20261013-221839/summary.json`.

코덱만 분리한8K probe1회 결과:

| 입력/조건 | 결과 | 전용 할당 피크 | 비고 |
|---|---|---:|---|
| 8192² progressive RGB JPEG /512MiB 예산 | PASS,434.0ms | 400.6MiB | 계수 RAM 필요, 고정 저메모리 처리 아님 |
| 같은 JPEG /64MiB 예산 | BUDGET_REJECTED,4.3ms | 7.4MiB | 계수 생성 전 거부 |
| 8192² Adam7 RGB PNG /64MiB 예산 | PASS,1267.4ms | 16.3MiB | scratch 사용, 해제 후 임시 파일0 |

취소 요청부터 관찰까지 JPEG18.2ms, Adam7 5.1ms, 초대형 TIFF6.7ms였다. 각각1회이며 최대 지연 보장이 아니다. 모두 파일 exclusive 재열기 성공, 해당 Windows 임시 폴더의 잔여 파일0. `artifacts/streaming-codecs/probes`. Linux tmpfile 수명 검증으로 확대 해석하지 않는다.

예산은 프로세스 총량의 하드 상한이 아니다. TIFF native allocator/행 묶음 각각의 한도, JPEG 작은 보조 할당, 풀에 반환된 managed 배열, OS 페이지 캐시를 구별한다. `PreparationBufferPeakBytes`는 실제 대여 배열 길이로 원본 준비와 축소의 입력/출력 행 묶음·동시 worker scratch를 합산한다. 최대 지원 너비와 배열 풀 크기 경계6조건에서64MiB 이하를 확인했다. 반환 직후 집계 감소 사이의 짧은 겹침은 보수적으로 포함하며 native·FileStream 버퍼·반환된 풀 배열·OS 캐시는 제외한다. 재열기는0이다. 모든 가능한 입력/OS/스케줄링 조합의 증명은 아니다. 디스크 여유/소유 캐시 예산·오류 정리는 구현했지만 실제 볼륨을 가득 채우는 ENOSPC fault injection과 전원 손실 fsync 검증은 하지 않았다.

## 앱·원본 검사·프레임

- 기본 솔루션 Release 빌드 PASS, NetVips 없는 선택형 앱/샘플/CLI publish PASS. 새 세 배포 폴더의 deps/native 바이너리에 NetVips/libvips 없음. 공통 라이선스 폴더에는 기존 경로의 고지도 함께 남긴다.
- 검사111, 이미지 이력/검사 경계/매칭 모드67, Canvas 경계·frame/tile 수명·기존 디스크 로더20의 관련 테스트 PASS. 전체 테스트 실행을 의미하지 않는다.
- 독립 Canvas GPU(RTX3060)/CPU(WARP) 각각 PASS. 8K/24K 절차적 소스, 작은 PNG 실파일, ROI·부모/자식 회전·확대·Undo/Redo·실패 원자성·reader 해제·overlay fence. `artifacts/streaming-codecs/app-validation/{gpu,warp}`. 실제 GPU 없는 저사양 PC 시험은 아니다.
- 논문/기하/OpenCV template/일반 도형 CLI 실파일 레시피4종 PASS. NetVips와 검출 개수2/2/1/4, pose·점수·윤곽 JSON이 완전히 일치. `artifacts/streaming-codecs/cli-validation`. 현장 검출률 또는8K 검사125ms 목표 검증이 아니다.
- Streaming 앱에서8K PNG/24K Gray16을20사이클·40회 교체하고 ROI 필터 실행 PASS.10→20사이클 private334.9→342.4MiB, handle1292→1293, 관리 메모리55.6→55.7MiB. 이력이 계속 추가되며 강제GC가 있는 짧은 진단이다. 장시간 무누수 증명으로 보지 않는다. `app-validation/replacement.json`.

24K Gray16 파일 새 준비 중 탐색 및4만 ROI 편집/Undo/Redo를 실제 앱에서 측정했다. 두 로더×GPU/WARP 각1회, 화면 밖 창·합성60Hz 입력이다. 정확한 문서 Undo/Redo와 렌더 오류 없음은 모두 PASS다.

| 모드 | 준비 중 frame p95 NetVips → 자체(ms) | 준비 중 최대 간격(ms) | 대기 입력 p95(ms) |
|---|---:|---:|---:|
| GPU | 42.20 → 32.11 | 126.61 → 50.69 | 24.21 → 9.24 |
| WARP | 29.23 → 27.78 | 247.91 → 49.73 | 21.24 → 15.21 |

**엄격한16.67ms 기준은 양쪽 모두 FAIL.** 자체 경로의 steady 구간 p95도 GPU24.70ms/WARP93.88ms였다. 단일 실행의 차이를 전 구간 회귀 없음으로 확정하지 않는다. 물리 scan-out FPS가 아니며 더 큰 렌더/편집 최적화는 이 파일 엔진의 완료로 포함하지 않는다. `artifacts/streaming-codecs/cadence/summary.json`에 전체 단계, 입력 생략, GC, 원본 CSV 연결이 있다. 이 때문에 기본 로더 전환 수용은 보류한다.

## 남은 검증

Linux 실제 native 빌드/로드/형식·오류·대용량·검사·성능, 현장 파일 corpus/장시간 안정성, 디스크 full/전원 손실, 엄격한60FPS가 남는다. 지원하지 않는 형식을 자동 수용하거나 정확성/성능 기준을 낮추지 않았다.
