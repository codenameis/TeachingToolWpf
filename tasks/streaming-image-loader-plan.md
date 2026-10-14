# TIFF·PNG·JPEG 자체 파일 엔진 후속 계획

작성일: 2026-10-13. 아래 견적은 구현 전 계획이며, 후속 사용자 승인으로 **Windows 선택 경로를 구현·검증했다**. Linux 실제 검증은 환경 준비 후로 사용자가 지정했다. [실측](../docs/streaming-image-loader-validation.md), [ADR-043](../docs/decisions/0043-streaming-file-engine.md)을 따른다. `main`과 기존 미커밋 변경을 유지하며 로컬 작업만 진행한다. 커밋·푸시는 하지 않는다. 실행 상태는 [작업 목록](todo.md) 한 곳에서 관리한다.

## 판단과 산정 범위

현재 앱이 사용하는 **파일 → 디스크 원본/피라미드 → 필요한 타일 읽기** 방식은 자체 구현 가능하다. 이미 실험에 피라미드 계산·raw 저장·타일 읽기가 있다. 남은 핵심은 전체 원본 Mat을 만들지 않는 디코더, 제한된 작업 메모리, 캐시의 실패·동시 접근 처리, 실제 앱/검사 연결과 양 플랫폼 검증이다.

제안은 C#으로 피라미드·캐시·수명·예산을 소유하고, 압축 해제에는 LibTIFF/libpng/libjpeg-turbo를 좁은 native 어댑터로 사용하는 것이다. **NetVips/libvips를 제거할 수 있는 경로이지 외부 의존성이 없는 구현은 아니다.** 코덱 버전 고정·빌드·배포·보안 수정 추적을 우리가 관리하므로 유지보수 책임은 늘어난다. PNG에는 zlib 등 전이 의존성도 남는다. 압축 코덱 자체의 재작성과 libvips 전체 연산 엔진 복제는 산정에 포함하지 않는다.

libvips는 필요 영역 계산, 파일 접근 방식 선택, 버퍼·스레드 제어까지 제공한다. 그러나 현재 `DiskPyramidSource`는 처음 열 때 원본과 모든 축소 단계를 `.v`로 미리 기록한다. 이 앱의 대체에는 범용 지연 연산 그래프나 `.v` 바이너리 호환 구현이 필요하지 않다. 자체 versioned raw/manifest를 유지하면 된다. [libvips 구조](https://www.libvips.org/API/current/how-it-works.html), [파일 읽기 전략](https://www.libvips.org/API/current/how-it-opens-files.html).

## 현재 재사용할 것과 빈 부분

| 영역 | 현재 증거 | 남은 일 |
|---|---|---|
| Canvas 의존 경계 | Canvas의 필수 NetVips 참조는 이미 제거됨 | 기존 영상 계약 유지. ROI/회전/확대/이력 재구현 불필요 |
| 공통 실험 엔진 | `VisionImaging.Pyramid`: Gray8/Gray16/RGBA32, 512px 타일, 삼각 축소, raw 캐시 | 전체 매핑을 사용하는 준비/읽기 경로의 메모리 제어 |
| 디코더 경계 | `IRasterDecoder/IRasterReader` + OpenCV 어댑터 | native 행/strip/tile 어댑터. 순차 읽기 조건·취소 단위 명문화 |
| 정확성 | 공통 테스트14개, 기존6입력의 결정적 타일 표본 오차0 | 형식 변형·손상·취소·동시 import·실제 파일 확대 |
| 메모리 | 24K×8K Gray16의 전체 Mat384MiB, 해제 전후 전용 할당 약403→18MiB | 원본 전체 Mat 제거. OS 파일 페이지와 native 작업 메모리 따로 측정 |
| 초대형 | 자체 OpenCV는24576×131072에서 픽셀 제한으로 실패 | 원본3/6GiB Gray8/16 실제 완주, 64비트 파일 offset |
| 플랫폼 | Windows 실행, Linux x64 publish 성공 | Linux 실제 실행·native 의존성·성능·오류 검증 |
| 제품 연결 | `IImageLoader`, 원본 검사 reader, 교체 실패 원자성 존재 | 앱 초기 Vips 설정·파일 버전 helper·배포 참조 제거/선택형 분리 |

수치는 기존 [비교](../docs/pyramid-decoder-comparison.md)와 [메모리 검토](../docs/pyramid-memory-review.md)의 결과다. 이번에 다시 벤치마크한 결과가 아니다. 기능 수를 세어 “몇% 완성”으로 환산하지 않는다. 어려운 부분인 호환성·수명·운영 검증의 비중이 크다.

## 1차 지원 행렬과 제한

다음 세부 범위는 **견적 가정**이다. 확정된 것은 세 확장자 지원이며, 모든 변형 지원을 의미하지 않는다. F01에서 실제 파일 헤더와 대조해 범위 차이를 기록한다. 현재 NetVips가 처리하는 파일을 새 경로가 거부하면 호환성 미완료로 남긴다.

| 형식 | 기본 견적에 포함하는 변형 | 메모리/호환성 주의점 |
|---|---|---|
| TIFF/BigTIFF | 첫 이미지, Gray8/Gray16·RGB8/RGBA8, strip/tile, little/big endian, 무압축·LZW·Deflate·PackBits | 거대한 압축 strip, TIFF JPEG/YCbCr, planar separate는 별도 조사 후 추가 산정. 작은 행 요청도 코덱 내부 큰 블록 할당을 만들 수 있음 |
| PNG | Gray8/16·RGB8/RGBA8, palette/저비트 gray 확장, tRNS, 일반 및 Adam7 | Adam7은 여러 pass의 같은 행을 결합해야 함. 제한 버퍼 + 디스크 scratch 방식 검증 |
| JPEG | 8bit Gray/RGB, 일반 sequential 및 progressive, 일반적인 색차 subsampling | progressive/multi-scan은 전체 계수 버퍼가 필요할 수 있음. 형식 지원과 일정 RAM 상한 보장은 구별 |

전체 페이지 탐색/애니메이션, CMYK/별색/float/16bit RGB, 전체 ICC 색 관리, JPEG의 다른 정밀도/특수 부호화는 기본 견적 밖이다. EXIF/TIFF orientation·alpha·gamma 처리 정책은 기존 경로와 비교해 고정한다. 디코더 교체를 이유로 자동 회전·명암 보정·알파 합성을 새로 적용하지 않는다. 지원하지 않는 변형은 식별 가능한 오류로 반환한다.

JPEG의 주의점은 구현 선택만의 문제가 아니다. libjpeg-turbo 문서상 progressive 등 multi-scan의 계수 버퍼는 일반적인8bit 컬러에서 약3~6byte/pixel이다. 8192²이면 계수만192~384MiB이며 출력 행 버퍼 등은 별도다. 기본 메모리 구현은 자동 임시 파일 spill을 제공하지 않는다. 따라서 기본 견적의 progressive 지원은 **계수 메모리를 산정·제한하고 예산 안에서 디코딩하는 것**이다. 초대형 progressive도 낮은 고정 RAM으로 반드시 읽어야 한다면 별도 디스크 backing 구현/검증이 필요하다. [libjpeg-turbo 메모리 설명](https://raw.githubusercontent.com/libjpeg-turbo/libjpeg-turbo/main/doc/libjpeg.txt).

TIFF의 타일/행 API와 PNG의 행 결합 API는 활용할 수 있다. TIFF의 압축 strip 비순차 읽기, scanline의 YCbCr 제한, PNG 인터레이스의 이전 행 보존 조건을 검증한다. 단순히 `ImRead`를 행 루프로 감싸는 작업이 아니다. [LibTIFF tile](https://libtiff.gitlab.io/libtiff/functions/TIFFReadEncodedTile.html), [scanline](https://libtiff.gitlab.io/libtiff/functions/TIFFReadScanline.html), [libpng 매뉴얼](https://libpng.org/pub/png/libpng-manual.txt).

## 제안 경계와 데이터 흐름

```text
TIFF / PNG / JPEG
      ↓
VisionImaging.Codecs (제안: .NET adapter + 작은 C ABI shim)
      ↓ IRasterDecoder / IRasterReader, Gray8·Gray16 LE·RGBA32
VisionImaging.Pyramid (기존 실험 엔진 확장)
      ↓ 원본 raw → 축소 raw → 검증된 manifest 게시
      ↓ ITileSource / PixelBuffer
IImageLoader를 주입받는 Canvas 호스트 및 원본 검사 reader
```

- native handle·오류 처리는 어댑터 내부에 둔다. `setjmp/longjmp`가 managed 프레임을 넘어가지 않게 C shim 안에서 오류 코드로 변환한다. 공개 계약에 코덱 포인터·OpenCV Mat을 노출하지 않는다.
- TIFF/libpng/libjpeg API를 포괄하는 범용 플러그인 시스템은 만들지 않는다. 기존 실제 변형 경계를 이용하고 필요한 세 구현만 만든다. native 구조체 ABI/가변 인자 호출을 C# 전역으로 퍼뜨리지 않는다.
- 행 요청은 현재 원본 저장 소비자의 단조 증가 읽기를 기준으로 한다. 시작 행·stride·출력 길이·소유권·취소 계약을 공통 테스트로 고정한다. arbitrary row 요청을 지원한다고 선언한 후 순차 코덱에서 조용히 다른 동작을 하지 않는다.
- 기본 경로는 원본 기록 후 축소 단계 생성이다. 원본과 모든 하위 단계의 동시 파이프라인 생성은 첫 교체의 필수 작업에서 제외한다. 먼저 제한된 row band로 전체 파일 매핑을 제거한다.
- Adam7의 scratch 수명은 디코더 어댑터 내부에서 소유한다. 최종 행 인터페이스를 유지하되 중복 원본 파일/I/O 비용을 측정한다. 공통 엔진에 PNG 분기를 넣지 않는다.
- 타일용 제한 매핑 창과 명시적 파일 읽기를 비교해 한 방식을 선택한다. 현재 `RawLevel.Row()`의 Span을 병렬 축소 worker가 사용하므로 공유 매핑 창을 이동시켜 유효한 Span을 무효화하면 안 된다. 준비용 행 묶음과 표시용 단일 소유 reader의 수명을 분리한다.
- 이미지를 전부 RAM에 올리지 않는다고 프로세스 Working Set이나 OS 파일 캐시가 자동으로 제한되는 것은 아니다. LibTIFF의 기본 파일 매핑도 관찰하고 필요하면 `m` 옵션을 비교한다. [TIFFOpen 옵션](https://libtiff.gitlab.io/libtiff/functions/TIFFOpen.html).
- 기존 NetVips 비교 호스트를 보존한다. 새 경로 실패 시 NetVips로 몰래 재시도하여 성공으로 집계하지 않는다. 제품 기본값 전환은 성능·호환성 결과로 판단한다.
- 카메라는 기존 메모리 프레임 경로를 유지한다. Linux 대상은 파일 엔진/검사 실행이며 WPF·DX12 화면 이식은 이 계획에 포함하지 않는다.

## 남은 작업량

숙련 개발자1명, 하루 실작업일 기준의 **추정 인일**이다. 기존 실험 구현은 재사용하며 기본 견적은 구현·테스트·리뷰·문서화를 포함한다. 장비 제공/승인/외부 대기 시간은 포함하지 않는다. AI 실행 시간이나 납기 약속이 아니다.

| 단계 | 작업 ID | 완료 시 관찰 가능한 결과 | 추정 인일 |
|---|---|---|---:|
| P0 범위·위험 확인 | F01–F02 | 지원 행렬, 동일 조건 기준선, BigTIFF/Adam7/progressive의 예산 가능성 판단 | 2–3 |
| P1 공통 native 경계 | F03–F05 | 안전한 C ABI, managed 수명, Windows/Linux x64 native 로드 | 3–5 |
| P2 TIFF/BigTIFF | F06–F08 | strip/tile 입력부터 raw/피라미드/타일까지 연결, 초대형3/6GiB 완주 | 4–6 |
| P3 PNG | F09–F10 | 일반·Adam7 PNG → 동일 엔진, Gray16 보존 | 3–5 |
| P4 JPEG | F11–F12 | sequential·예산 내 progressive JPEG → 동일 엔진 | 3–5 |
| P5 메모리 제어 | F13–F14 | 제한 행 묶음으로 축소, 제한된 타일 I/O, 단계별 메모리 보고 | 3–5 |
| P6 캐시 안정성 | F15–F16 | 중복 import 조정, 원자적 완료, 손상/취소/디스크 부족 대응 | 2–4 |
| P7 제품 연결·양 플랫폼 검증 | F17–F24 | NetVips 없는 선택 경로, Canvas/원본 검사 회귀, 실제 Linux 실행 결과 | 6–9 |
| **기본 합계** | | | **26–42** |
| **불확실성 여유 약25%** | | native 배포·실제 파일 변형·픽셀 차이 원인 분석 | **7–11** |
| **일정 편성 범위** | | 1인·주5일이면 약7–11주 | **33–53** |

P0 이후 지원 변형과 native 배포 근거로 재산정한다. 큰 단위의 단계는 아래 task별로 검증하며 한꺼번에 교체하지 않는다. 세 형식의 콘솔 경로까지는 P0–P4 합계15–24인일, 위험 여유 전이다. 이 시점에는 메모리/캐시/제품 수용이 끝난 것이 아니다.

추가 요구별 분리 견적:

| 후보 | 추가 추정 | 포함하지 않은 이유 |
|---|---:|---|
| 초대형 progressive JPEG의 디스크 계수 backing | 5–10인일 이상의 별도 조사·구현 출발 견적 | native 메모리 backend와 비순차 계수 I/O 변경. 조사 후 다시 산정; 기본 일정의 고정 추가분으로 보장하지 않음 |
| 정수 SIMD 축소 | 2–4인일 | 기존 축소 단계만 개선. 디코더/디스크 병목 해소 후 동일 픽셀·실측 이득이 있어야 채택 |
| 코덱 자체 순수 C# 구현 / libvips 전체 기능 | 이 조사로 신뢰할 만한 산정 불가 | 세 형식 스트리밍 어댑터와 규모·검증 범위가 다름 |
| 추가 TIFF 압축/색 공간·ICC·다중 페이지·Linux UI | 파일/기능 범위를 받아 별도 산정 | 세 확장자 지원만으로 요구가 확정되지 않음 |

## task의 변경 범위와 진행 순서

완료 상태·수용/검증은 [todo](todo.md)에 있다. 아래는 구현 전 예상 변경 지점이다. 실제로 native 구현을 TIFF/PNG/JPEG별 파일로 나누고 소스 경계·호스트 조립을 별도 단계로 검증했다. `Pyramid`는 `experiments/VisionImaging.Pyramid`, `Comparison`은 `experiments/PyramidComparison`을 뜻한다.

| ID | 예상 변경 지점 | 선행 |
|---|---|---|
| F01 | Comparison의 fixture/측정 파일, 지원 행렬 문서 (3–4파일) | 없음 |
| F02 | 별도 최소 native probe, 결과 문서 (2–3파일) | F01 |
| F03 | 제안 `experiments/VisionImaging.Codecs/native`의 C header/구현/CMake (3파일) | F02 |
| F04 | 같은 어댑터의 csproj/PInvoke/SafeHandle/계약 테스트 (4–5파일) | F03 |
| F05 | Windows/Linux 빌드·배포 스크립트, 버전 lock, THIRD-PARTY-NOTICES (3–5파일) | F04 |
| F06 | Tiff reader, decoder 선택, 테스트 (3파일) | F05 |
| F07 | Tiff reader/변환 helper, fixture/테스트 (3–4파일) | F06 |
| F08 | Own 호스트의 디코더 조립, 초대형 비교/결과 (3–4파일) | F07 |
| F09 | Png reader, decoder 선택, 테스트 (3파일) | F05 |
| F10 | Png reader/scratch reader, fixture/테스트 (3–4파일) | F09 |
| F11 | Jpeg reader, decoder 선택, 테스트 (3파일) | F05 |
| F12 | Jpeg reader/예산 계산, fixture/테스트 (3–4파일) | F11 |
| F13 | Pyramid의 TrianglePyramid/RawLevel, 행 묶음 reader, 테스트 (4파일) | F08 |
| F14 | Pyramid의 RawLevel/RawPyramidSource, 타일 테스트/측정 (4파일) | F13 |
| F15 | RawPyramidSource, 캐시 소유/lock helper, 동시성 테스트 (3–4파일) | F10,F12,F14 |
| F16 | 캐시 예산/회수 정책, manifest 검증, 오류 테스트 (3–5파일) | F15 |
| F17 | 자체 IImageLoader 구현, 프로젝트 참조, 로더 계약 테스트 (3파일) | F16 |
| F18 | 중립 파일 version helper, EditorViewModel 호출, 테스트 (3파일) | F17 |
| F19 | 기존 synthetic/procedural 소스의 실제 사용 경계, 관련 csproj/테스트 (3–5파일) | F17 |
| F20 | InspectionNativeRuntime.props, VisionInspection 참조, 호스트 props/검증 (3–5파일) | F18,F19 |
| F21 | MainWindow 조립, TeachingToolWpf.csproj, 진단 fixture 분리 진입점 (3–4파일) | F20 |
| F22 | SampleWindow/SampleSmoke, LargeImageRun/ImageReplacementRun/ShapeTargetSmoke 호출 (5파일) | F21 |
| F23 | 이미지 교체·검사·표시 회귀 테스트/진단 (3–5파일) | F22 |
| F24 | 양 플랫폼 실행 스크립트·결과 JSON·검증 보고서 (3–4파일) | F23 |

위험 확인 → native 경계 → TIFF 전체 경로 → PNG/JPEG 전체 경로 → 메모리/캐시 → 제품 연결 순서다. F13은 TIFF 완주 후 먼저 진행해도 되지만 성능 측정은 다른 빌드/벤치마크와 병렬 실행하지 않는다. 검증 checkpoint는 F02/F05/F08/F10/F12/F14/F16/F18/F20/F22/F24 후에 둔다. 각 checkpoint에서 증거와 남은 실패를 보고하고, 새 범위나 설계 원칙 예외가 필요한 경우에만 해당 변경 전에 사용자에게 확인한다. Git checkpoint commit/push는 하지 않는다.

## 수용 기준과 측정 방법

**정확성·수명**

1. 원본 Gray16 값, Gray8, RGBA 순서·알파·가장자리·홀수 크기·축별 반올림을 보존한다. 작은 입력은 전 픽셀, 초대형은 결정적 영역/경계 표본과 원본 스트림 검증을 조합한다. 표본을 전체 픽셀 증명으로 표현하지 않는다.
2. JPEG IDCT/upsampling 등에서 기존 출력과 차이가 나면 평균·최대 오차와 원인을 기록한다. 허용 오차를 조용히 늘리지 않는다. 검사 영향이 해결되지 않으면 기본값 교체 수용은 실패다.
3. 취소/잘린 파일/잘못된 헤더/디스크 부족/동일 캐시 동시 요청에서 완료 캐시 오인식·핸들/버퍼 누수가 없어야 한다. 취소는 native 처리 단위 사이에 관찰하고 최장 응답 지연도 측정한다. native decode를 임의 스레드 종료로 중단하지 않는다.
4. 새 이미지 읽기 실패·취소 시 기존 이미지/ROI/선택/통합 이력이 유지된다. 성공 시 기존 수동 이미지 교체 규칙, Undo/Redo, 독립 원본 검사 reader의 소유권을 유지한다. 표시 LOD를 검사 원본으로 사용하지 않는다.

**메모리·디스크**

- 제안 목표: 자체 준비 작업의 행/축소/worker 버퍼 합계64MiB 이내. 실제 대여 배열 길이로 합산한다. 코덱 내부 작업·progressive 계수·metadata·reader별 타일 버퍼·GPU 자원은 별도 항목으로 함께 보고한다. 이64MiB를 프로세스 전체 상한으로 말하지 않는다.
- 일반 sequential PNG/JPEG 및 정상 크기 strip/tile TIFF는 같은 너비에서 높이를 키웠을 때 전체 원본 크기에 비례하는 private 할당이 사라져야 한다. progressive JPEG와 거대한 TIFF strip은 별도 부하군이다.
- Private Bytes/Working Set/native/managed/GC/디스크 cache 크기를 각각 기록한다. Windows와 Linux의 지표 정의를 명시한다. 제한 매핑은 OS 전체 파일 캐시 상한을 보장하지 않는다.
- 디스크 계산은 `sum(levelWidth × levelHeight × bytesPerPixel)`를 checked64비트로 정확히 합산한다. 일반 2D의 약4/3 추정은 UI 설명용이다. 초대형 Gray8/16의 최종 raw cache 약4/8GiB 외에 staging, 기존 cache, Adam7 scratch, 동시 요청분도 반영한다.
- 기존 source가 읽는 캐시는 퇴출하지 않는다. 회수 대상은 소유 manifest로 확인된 cache entry만이며 임의 경로·사용자 원본을 지우지 않는다. 현재 남아 있는 실험 파일의 일괄 정리는 이 계획에 포함하지 않는다.

**속도·화면**

- 같은 장비·입력·Release 실행물·새 프로세스/새 캐시로 NetVips와 새 경로를 번갈아 최소5회 측정한다. 후속 사용자 확정에 따라 초대형24576×131072 TIFF는 캐시를 보존하고각1회 결과로 기록한다. OS cache가 warm인지 구별하고, OS cache를 실제로 비우지 않았으면 cold disk라고 부르지 않는다.
- 준비 전체 시간, 구간 시간, cache 재열기, 타일 p50/p95/p99, 메모리 피크, 해제 후 상태를 기록한다. 현재 JPEG979.6ms는 이전 NetVips900.5ms와 다른 반복 세션이므로 동률/승리로 확정하지 않는다. 초대형 기존8.114s/22.393s는 각각1회라 재측정이 필요하다.
- 제품 전환 후보의 **제안 판정선**은 같은 세션의 입력별 준비 중앙값이 NetVips의1.2배 이내, 메모리 목표 충족, 타일 지연 및 화면 입력의 회귀 없음이다. 사용자 확정 SLA가 아니며 초과 시 수치를 제시하고 개선하거나 기존 기본 경로를 유지한다. 현재 수치로 달성을 보장하지 않는다.
- 엄격한60FPS/약16.67ms 프레임 목표는 그대로 유지한다. 준비·교체·회전·확대·ROI 편집 구간의 프레임 간격/긴 멈춤/입력 대기/GC를 GPU 및 강제WARP로 측정한다. 파일 I/O 향상으로 기존60FPS 미달이 해결됐다고 선언하지 않는다.
- Linux x64에서 세 형식·초대형·오류·메모리·원본 검사 smoke를 실제 실행해야 플랫폼 완료다. 현재 환경에는 실행 가능한 Linux 검증 환경이 확보되지 않았으므로 publish만으로 완료할 수 없다. 기준 Linux 배포판/glibc와 실제 runner는 F05 전에 확정한다.

현재 공통 회귀 명령은 `dotnet build experiments/PyramidComparison.slnx -c Release`, `dotnet test experiments/VisionImaging.Pyramid.Tests -c Release`다. 새 코덱용 fixture/검증 명령은 F01/F04에서 재현 스크립트로 추가한다. 현재 `Own.dll`/`Measure.ps1`은 OpenCV 조립이므로 수정 없이 실행한 값을 새 스트리밍 구현의 결과로 보고하면 안 된다.

## SOLID·GRASP 설계 검토

| 원칙 | 이 계획의 적용/확인 |
|---|---|
| SRP | 디코드·축소·캐시·native 배포·앱 조립을 각각 변경 이유에 맞게 유지 |
| OCP | 세 디코더를 기존 실제 변형 계약으로 연결. Canvas에 확장자 분기 없음 |
| LSP | 픽셀/순차 읽기/취소/오류/소유권 계약 테스트. 미지원 변형을 대체 완료로 숨기지 않음 |
| ISP | reader는 규격·행 읽기·해제, tile source는 타일 읽기. 사용하지 않는 코덱 메서드 노출 없음 |
| DIP | Canvas/Core/검사는 영상 계약, 호스트가 파일 구현 선택 |
| Information Expert | 축소 규칙은 Pyramid, 코덱 형식 해석은 decoder, 캐시 예산은 cache 소유자 |
| Creator | native reader가 handle을 생성/해제. PixelBuffer 소비자에게 기존 소유권 전달 |
| Controller | 앱은 교체 요청/원자적 게시만 조정. WPF에 디코드 규칙 배치 없음 |
| Low Coupling | native 포인터·색 공간 API가 공통 계약으로 새지 않음 |
| High Cohesion | 파일 엔진으로 ROI·검사 판정·GPU 책임을 옮기지 않음 |
| Polymorphism | 실제 TIFF/PNG/JPEG 변형에만 적용, 가상 공급자 추가 없음 |
| Pure Fabrication | 캐시/임시 파일/native 수명은 기술 책임으로 격리 |
| Indirection | C ABI adapter와 호스트 주입으로 외부 ABI 변경 제한 |
| Protected Variations | 형식·OS별 배포 변경을 reader와 host 경계 안에 제한 |

설계 원칙 예외는 제안하지 않는다. 예외가 필요해지면 재현·대안·정확성 영향과 복원 방법을 먼저 제시하고 승인 전에 적용하지 않는다.

## 구현 전 계획 작성 당시 기록

다음은 구현 승인 이전의 산정 이력이며 현재 상태가 아니다. 코드/참조/기존 실측과 공식 API 문서를 검토하여 산정했다. 당시 **PASS:** 상대 파일 링크 존재, task24개와 고유 ID24개, 선행 task 순서, 기본26–42/여유 포함33–53인일 합계, 기존 plan/todo 변경의 `git diff --check`를 정적으로 확인했다. 당시 새 decoder·실측·Linux·제품 회귀는 **NOT_RUN**이었으며 문서만 변경했다. 이후 Windows 구현·검증과 남은 실패는 [최신 검증 기록](../docs/streaming-image-loader-validation.md)과 [작업 목록](todo.md)을 따른다.
