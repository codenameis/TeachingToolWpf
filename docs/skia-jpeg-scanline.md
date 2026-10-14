# Skia JPEG scanline 구현과 재비교

2026-10-14. Skia JPEG의 전체 비트맵 할당을 제거하고 기존 자체 스트리밍 로더와 다시 비교했다. Windows x64 메인 앱의 두 비교 빌드도 생성했다. 생산 기본 선택은 변경하지 않았으며 로컬 구현·검증만 진행했다.

## 변경

- JPEG는 `StartScanlineDecode` 후 `GetScanlines`로 호출자가 제공한 버퍼에 직접 디코드한다. 별도의 전체 RGBA 비트맵이나 중간 행 복사를 추가하지 않는다.
- 최대64행씩 native 호출하고 호출 사이에 취소를 확인한다. progressive JPEG 초기화에 포함된 native 작업은 토큰으로 즉시 중단할 수 없으며 종료 후 확인한다.
- 반환된 행 수가 부족하면 오류로 처리하고 codec을 해제한다. native 행 소비 후 취소/오류가 발생한 reader는 재사용하지 않는다. 입력 검증 실패·호출 전 취소는 행을 소비하지 않는다.
- JPEG 원본 크기512MiB 제한을 보존한다. 실제로 그 크기의 비트맵을 할당하는 의미나 내부 계수/프로세스 메모리 상한은 아니다.
- PNG 전체 비트맵 경로와 Gray16 PNG 명시적 거부는 보존했다. TIFF/BigTIFF는 기존 `NativeRasterDecoder`다.
- `build/SkiaFileDecoder.cs`가 앱/CLI 조립 지점의 같은 형식 선택을 제공한다. Canvas/Core·검사 계약에는 Skia 타입/참조를 추가하지 않았다.

공식 API 구현 근거: [SKCodec의 scanline API](https://github.com/mono/SkiaSharp/blob/main/binding/SkiaSharp/SKCodec.cs). 패키지는 이전과 같은 SkiaSharp4.153.1이다.

## Windows 8K 측정

Ryzen5 5600H / RAM32GB / .NET10.0.12 / Release.8192×8192 일반 컬러 JPEG와 progressive JPEG를 두 로더로 각각5회, 총20회 새 프로세스·새 캐시에서 교차 실행했다. OS 파일 캐시는 비우지 않았으며 기존/새 측정 캐시는 모두 보존했다. 원본·실행물·native 해시와 공통 피라미드 Release 최적화를 기록했다.

준비 시간은 디코드만이 아니라 최초 JIT/native 준비·피라미드 생성·캐시 저장을 포함한다. 메모리는 준비/재열기/타일 읽기 중20ms 간격으로 표본화한 프로세스 최대 Private Bytes 또는 Working Set이며 짧은 피크를 놓칠 수 있다. 아래 수치는 각 실행의 결과를 모은5회 중앙값이다.

| 입력/구현 | 준비 ms | 준비 최소~최대 ms | 최대 전용 MiB | 최대 상주 MiB | 재열기 ms | 세 번째 타일 pass p95 ms |
|---|---:|---:|---:|---:|---:|---:|
| 일반 JPEG / 자체 | 785.4 | 771.9~802.4 | 31.9 | 62.3 | 9.00 | 1.053 |
| 일반 JPEG / Skia scanline | 1,168.5 | 1,148.5~1,185.3 | 32.3 | 63.9 | 9.28 | 1.000 |
| Progressive / 자체 | 834.0 | 820.4~845.8 | 401.8 | 422.6 | 8.73 | 1.013 |
| Progressive / Skia scanline | 1,029.5 | 1,007.6~1,079.9 | 402.4 | 424.7 | 9.01 | 1.054 |

**메모리 절감은 달성했고, 속도는 기존 자체 로더가 우세하다.** 이번 준비 시간에서 Skia는 일반 JPEG 약48.8%, progressive 약23.4% 더 오래 걸렸다. 같은 피라미드/타일 코드이므로 타일 수치 차이를 Skia 회전·확대 성능 이득으로 해석하지 않는다.

이전 [전체 비트맵 측정](skia-loader-comparison.md)의 Skia 전용 메모리는 일반274.1MiB, progressive649.9MiB였다. 이번에는 각각32.3/402.4MiB로 약242/248MiB 감소했다.8192² RGBA 비트맵256MiB를 없앤 효과와 일치하는 규모다. 이전 기록은 별도 측정 세션이며 현재 자체 로더와의 교차 비교와 구별한다. progressive의 약402MiB는 내부 계수 작업 등이 남기 때문에 일반 JPEG처럼32MiB로 내려가지 않았다.

원본 결과는 `artifacts/skia-comparison/paired-20261014-080727`, [요약/실행별 수치/해시](skia-jpeg-scanline-results.json)에 있다. 재현:

```powershell
dotnet build experiments/PyramidComparison/Streaming/Streaming.csproj -c Release
dotnet build experiments/PyramidComparison/Skia/Skia.csproj -c Release
pwsh -NoProfile -File experiments/PyramidComparison/Compare-Skia.ps1 -JpegOnly
```

## 검증

- Skia18테스트 PASS. 새 JPEG 배치1/17/130행과 내부64행 경계, 전체 디코드와 전 픽셀 동일, 호출자 버퍼 끝 보존, 잘린 entropy 오류, 취소/순차 행/Dispose/파일 핸들 해제를 확인했다. 기존14테스트 범위도 유지했다. 기존 native코덱26·공통 피라미드24를 합쳐68테스트 PASS, TRX는 `artifacts/skia-scanline/tests`에 보존했다.
- 일반8K JPEG, progressive8K JPEG, Gray JPEG, 홀수 RGBA PNG, Gray16 LZW TIFF의 원본 및 모든 피라미드 단계 **표본 차이0**. 큰 원본 전체를 전수 비교한 결과는 아니다. `artifacts/skia-scanline/quality`에 보존했다.
- 자체/Skia 메인 앱은 일반8K JPEG와128 ROI 복사/붙여넣기·정확한 Undo/Redo를 GPU 자동 선택/CPU 강제에서 검증했다. 실제 장치는 RTX3060과 Microsoft Basic Render Driver(WARP)로 확인했다. 추가 Skia24K Gray16 TIFF·8K PNG까지6개 앱 시나리오 PASS다. 진단 결과는 `artifacts/skia-scanline/app-smoke`다.
- 처음 사용한 작은 TIFF는 진단에 하드코딩된 ROI 배치보다 작아 파일 열기 이후 ROI 생성에서 중단됐다. 로더 오류와 구별하여 실패 기록을 보존하고, ROI 배치가 들어가는24K TIFF로 재검증했다.
- PNG/TIFF 디코더 구현과 공통 피라미드·Canvas는 변경하지 않았다. Linux 실실행, ICC/CMYK 전체 corpus, native 호출 도중 즉시 취소, 새로운60FPS 성능 수용은 미검증이다.

## 비교 빌드

`artifacts/loader-comparison/20261014-080920`에 Windows x64 Release self-contained 메인 앱 두 폴더/ZIP을 생성했다. `Streaming-win-x64.zip`과 `Skia-win-x64.zip`이며 양쪽 NetVips 패키지/DLL0개, 공통 Canvas/알고리즘 DLL 해시 동일이다. Skia판만 SkiaSharp/libSkiaSharp를 포함하고 양쪽 모두 TIFF용 VisionImageCodecs를 포함한다.

압축을 각각 풀어 **Run.cmd**로 실행한다. **Run-CPU.cmd**는 CPU(WARP)를 강제한다. 창 제목에서 로더를 구별하고 각 폴더의 별도 캐시를 사용한다. [배포 사용법](skia-scanline-builds.md). 이 빌드는 WPF 화면이므로 Windows용이며 Linux 디코더 실행 검증을 대신하지 않는다.

## 원칙 검토

| 원칙 | 확인 |
|---|---|
| SRP | JPEG 행 읽기 수명은 JpegScanlineReader, 파일 선택은 호스트 |
| OCP | 기존 IRasterDecoder 확장, Canvas 변경 없음 |
| LSP | 순차 행·소유권·오류·취소 보존 테스트; PNG16/입력 크기 제한은 공개 |
| ISP | 기존 작은 reader 계약만 구현 |
| DIP | 도메인에 Skia 타입 없음, 선택형 빌드가 의존성 조립 |
| Information Expert | 행 위치/stride는 reader가 소유 |
| Creator | codec 생성 성공 시 reader로 소유권 이전, 실패/Dispose에서 해제 |
| Controller | 앱의 기존 조립 지점만 선택 제어 |
| Low Coupling | Skia/native 참조는 디코더와 호스트에 한정 |
| High Cohesion | PNG bitmap과 JPEG scanline 수명 분리 |
| Polymorphism | 자체/Skia를 같은 영상 계약으로 실행 |
| Pure Fabrication | 기존 raw 캐시/피라미드 재사용 |
| Indirection | 공통 호스트 형식 라우팅으로 앱/CLI 차단 |
| Protected Variations | codec 변경이 ROI/편집 이력에 전파되지 않음 |

설계 원칙 예외·커밋·푸시 없음. 다음 속도 개선은 디코드 단계 프로파일링 근거가 필요하며 이번 메모리 개선만으로 자체 로더보다 빠르다고 표현하지 않는다.
