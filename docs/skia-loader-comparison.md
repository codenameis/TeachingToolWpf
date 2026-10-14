# SkiaSharp PNG·JPEG와 자체 스트리밍 로더 비교

후속: JPEG scanline 구현·재측정·메인 앱 두 빌드는 [새 결과](skia-jpeg-scanline.md)를 따른다. 아래는 변경 전 전체 비트맵 방식의 역사적 측정이다.

2026-10-14, Windows x64 / Ryzen 5 5600H / RAM 32GB. 로컬 구현·검증이며 생산 기본 로더는 변경하지 않았다. Linux 실제 실행은 환경 준비 후다.

## 구현과 경계

`experiments/VisionImaging.Skia`는 **SkiaSharp 4.153.1의 SKCodec.GetPixels로 전체 원본 비트맵을 CPU에서 디코드**한다. PNG/JPEG만 담당하며 외부 Skia 타입을 `IRasterDecoder`/`IRasterReader` 밖으로 노출하지 않는다. Gray8 또는 RGBA32 straight alpha를 읽고 색공간 변환을 요청하지 않는다. EXIF 방향에 따른 원본 회전도 적용하지 않는다.

`experiments/PyramidComparison/Skia`의 조립 지점은 TIFF/BigTIFF magic을 보고 기존 `NativeRasterDecoder`로 위임한다. PNG/JPEG 실패를 기존 로더로 숨겨 대체하지 않는다. 두 비교 실행물 모두 같은 `RawPyramidSource`의 피라미드·타일·캐시를 사용한다. VisionCanvas/Core, ROI 이동·회전·확대, 렌더러는 변경하지 않았다. Skia GPU 렌더링 성능을 측정한 것이 아니다.

이번 구현은 일반 전체 비트맵 경로의 비교다. Skia scanline/incremental API를 이용한 최적화나 Skia 성능의 상한을 측정한 결과가 아니다. 공식 API: [SKCodec.GetPixels](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcodec.getpixels?view=skiasharp), 고정 패키지: [SkiaSharp 4.153.1](https://www.nuget.org/packages/SkiaSharp/4.153.1).

## 측정 방법

- 8192×8192 입력 4종 × 두 실행물 × 각5회 = 40회. 매회 새 프로세스·새 디스크 캐시, 실행 순서를 번갈아 배치했다. 캐시는 전부 보존했다.
- Release .NET 10.0.12. 공통 `VisionImaging.Pyramid.dll` SHA256은 모두 `1FECAA6BC6D2D8A5500E2394B78AA5455DFA5DB9BEC10A058FBEF85CCB77787D`. 입력/실행물/native 해시는 개별 JSON에 기록했다.
- 준비 시간은 최초 디코더/native 로드·JIT·원본 디코드·피라미드 생성·캐시 기록을 포함한다. 디코더만의 순수 연산 시간과 구별한다. 프로세스 시작 자체는 포함하지 않는다.
- 메모리는 준비·재열기·3회 타일 읽기 구간에서20ms마다 표본화한 **프로세스 전체 최대 Private Bytes/Working Set**이다. 짧은 순간 피크를 놓칠 수 있고 OS 전체 파일 캐시나 프로세스 트리는 포함하지 않는다.
- OS 파일 캐시는 비우지 않았다. 새 피라미드 캐시와 물리 디스크 cold read는 다르다. 아래는 각 실행의 최대값을 얻은 후5회 중앙값을 계산한 값이다.
- 결과: [요약과 실행별 수치](skia-loader-results.json). 원본 JSON과 캐시는 `artifacts/skia-comparison/paired-20261014-075437`에 보존했다.

## 준비 시간과 메모리

| 8K 입력 | 기존 스트리밍 준비 ms | Skia 준비 ms | 기존 최대 전용 MiB | Skia 최대 전용 MiB | 기존 최대 상주 MiB | Skia 최대 상주 MiB |
|---|---:|---:|---:|---:|---:|---:|
| Gray8 PNG | 234.4 | 244.2 | 18.4 | 74.8 | 50.7 | 97.1 |
| 컬러 JPEG | 791.2 | 1,287.3 | 31.7 | 274.1 | 63.1 | 296.9 |
| Adam7 컬러 PNG | 1,645.1 | 1,116.0 | 32.1 | 457.5 | 63.1 | 468.3 |
| Progressive JPEG | 858.2 | 1,161.1 | 401.7 | 649.9 | 422.6 | 663.9 |

일반 PNG는 준비 시간이 약4.2% 길어졌고 두 범위가 겹친다(기존231.6~239.7ms, Skia231.9~253.2ms). 일반 JPEG는 약62.7%, progressive JPEG는 약35.3% 길어졌다. Adam7 PNG는 약32.2% 짧아졌지만 전용 메모리는 약14.2배다.

Skia Gray8 원본 비트맵 자체는64MiB, RGBA32는256MiB다. 전체 원본을 먼저 보관하기 때문에 기존 순차 행 디코드보다 메모리가 증가한다. Adam7의 추가 내부 메모리와 progressive JPEG의 계수 메모리까지 생기므로 원본 비트맵 크기가 전체 피크는 아니다. 기존 로더도 progressive JPEG에서는 전용 메모리 약402MiB를 쓴다. 작은 작업 버퍼만으로 모든 형식의 프로세스 메모리를 보장할 수 없다.

Adam7에서 기존 로더는 메모리를 제한하려고 디스크 scratch를 사용한다. 이번 Skia 경로는 전체 원본을 RAM에 보관한다. 이 구현 차이가 속도·메모리 교환을 설명하지만 디코더 내부 단계별 시간을 분리 측정한 인과 증명은 아니다.

## 재열기와 타일 읽기

재열기 중앙값은 모두 약8.5~9.4ms다. 원본 디코드를 다시 하지 않는 같은 raw 캐시 경로다.

| 입력 | 기존 세 번째 타일 pass p95 ms | Skia 세 번째 타일 pass p95 ms |
|---|---:|---:|
| Gray8 PNG | 0.781 | 1.075 |
| 컬러 JPEG | 1.062 | 0.945 |
| Adam7 PNG | 1.013 | 1.341 |
| Progressive JPEG | 1.307 | 0.993 |

동일 타일 코드이므로 이 차이를 Skia의 확대·회전 가속 효과로 해석하지 않는다. 이전 디코드가 남긴 메모리/OS 상태와 실행 변동이 포함된다. ROI 입력 반응·GPU/WARP·60FPS 검증은 이번 범위에 포함하지 않았다.

## 정확성과 한계

- PASS: 위4개 큰 입력, 작은 Gray JPEG, 홀수 크기 RGBA PNG, RGBA tiled TIFF, Gray16 LZW strip TIFF의 원본 및 모든 피라미드 단계 **검사한 표본 차이0**. 큰 파일 전 픽셀 전수검사로 표현하지 않는다.
- PASS: Skia14테스트. PNG palette/저비트 gray/tRNS, 비인터레이스·Adam7 Gray8/RGBA 전 픽셀, 손상 CRC/잘린 JPEG 오류, 순차 행/작은 버퍼/취소/Dispose, 비트맵 예산, Gray16 PNG 명시적 거부.
- PASS: 기존 코덱26 + 공통 피라미드24 회귀 테스트. 합64테스트이며 TRX는 `artifacts/skia-comparison/tests`에 보존했다. 최종 빌드0경고/0오류, 측정한 Skia 디코더 DLL 해시와 최종 빌드 해시가 일치한다.
- **Gray16 PNG는 이번 Skia 구현에서 미지원**이다. 자동8비트 변환 없이 거부한다. 기존 스트리밍 로더는 계속 지원한다. Skia 전체 라이브러리의16비트 지원 불가를 뜻하지 않는다.
- 전체 원본 비트맵은512MiB까지 허용한다. **프로세스 전체 상한이 아니며**, progressive JPEG에서 실측 약650MiB가 이를 보여준다. 기존 로더의 내부 예산과 같은 의미의 제한도 아니다.
- `GetPixels` 실행 중에는 취소 토큰으로 native 호출을 중단하지 못한다. 호출 전후와 행 복사 사이에는 확인한다. 대형 손상 파일·색공간/ICC·CMYK·EXIF 변형 전체 호환성은 추가 corpus 검증이 필요하다.
- Linux native 패키지는 포함했지만 Linux 실행/메모리/속도는 NOT_RUN이다. TIFF 라이브러리도 Linux 환경에서 별도 빌드가 필요하다.

## 판단과 다음 후보

현재 대용량 파일 기본값은 **기존 스트리밍 로더를 유지하는 편이 유리**하다. Skia 전체 비트맵은 Adam7에서 속도 이점이 있지만, 일반 JPEG 속도와 메모리에서 이득이 없었다. PNG/JPEG 지원을 UI 스택과 통합하는 배포 편의성은 별도 판단 항목이다.

후속 실험을 한다면 JPEG의 Skia scanline 디코드를 먼저 비교해 원본256MiB 상주를 없앨 수 있는지 확인한다. PNG는 형식별 incremental 지원·출력 버퍼 요구를 확인하고, Gray16 원본 정밀도·native 취소·메모리 한도를 보완해야 한다. 이는 아직 구현하거나 성능을 검증한 항목이 아니다.

## 설계 검토

| 원칙 | 이번 적용/확인 |
|---|---|
| SRP | 디코더·피라미드·측정 실행물 책임 분리 |
| OCP | 기존 IRasterDecoder 구현 추가, 생산 분기 추가 없음 |
| LSP | 순차 행/소유권/오류/취소 검증; Gray16·예산·취소 지연 차이로 완전 대체 불가 명시 |
| ISP | 기존 작은 영상 계약 사용, 새 공개 메서드 없음 |
| DIP | 피라미드는 계약만 참조, 호스트가 TIFF/Skia 조립 |
| Information Expert | 픽셀 형식은 디코더, 단계 계산/타일은 기존 영상 계층 |
| Creator | 디코더가 SKBitmap 생성, reader가 해제 |
| Controller | 비교 CLI가 측정/선택 조정, 업무 규칙/UI 변경 없음 |
| Low Coupling | 공개 계약에 Skia 타입 없음 |
| High Cohesion | 실험 디코더와 호스트/보고서 분리 |
| Polymorphism | 실제 두 디코더를 같은 IRasterDecoder로 실행 |
| Pure Fabrication | 기존 캐시/피라미드 기술 책임 재사용 |
| Indirection | TIFF 라우팅은 실험 호스트에 한정 |
| Protected Variations | native 코덱 변경이 Canvas/ROI에 전파되지 않음 |

성능을 위한 설계 원칙 예외 없음. 테스트의 PNG 생성기는 두 코덱 테스트가 공유한다. 실행법은 [실험 README](../experiments/PyramidComparison/Skia/README.md), 라이선스 원문은 [고지](../THIRD-PARTY-NOTICES.md)를 따른다.
