# 스트리밍 파일 코덱

TIFF/PNG/JPEG/WebP 압축 해제를 담당하는 기본 파일 어댑터다. LibTIFF 4.7.2, libpng 1.6.59, libjpeg-turbo 3.2.0, zlib 1.3.2, libwebp 1.6.0을 작은 C ABI DLL/SO에 정적으로 연결한다. C# 피라미드·캐시는 별도 프로젝트다. NetVips 의존성은 제거했다. Windows x64를 실제 실행했으며 Linux x64 실행은 사용자 선택에 따라 환경 준비 후 검증한다.

## 빌드와 실행

Windows는 .NET 10 SDK, PowerShell 7, Visual Studio C++/CMake 도구가 필요하다. NASM은 고정 SHA256으로 로컬 `artifacts`에 내려받는다. Linux 빌드 정의는 CMake/Ninja/C 컴파일러/NASM/PowerShell 7을 전제로 하며 아직 실제 검증하지 않았다.

```powershell
pwsh -NoProfile -File experiments/VisionImaging.Codecs/Build-Native.ps1
dotnet test experiments/VisionImaging.Codecs.Tests -c Release
dotnet test experiments/VisionImaging.Pyramid.Tests -c Release
dotnet build experiments/PyramidComparison.slnx -c Release
dotnet run --project experiments/PyramidComparison/Streaming -c Release -- image.tif artifacts/new-cache artifacts/result.json
```

최초 준비 측정은 매번 새 캐시 디렉터리가 필요하다. 이전 `Measure-Streaming.ps1`은 폐기했다. `Compare-Skia.ps1`은 독립 Skia 디코더와 비교하며 `Measure.ps1`은 NativeReference/OpenCV를 비교한다. OS 파일 캐시는 비우지 않으며 cold disk 측정이 아니다.

기본 앱·독립 Canvas·CLI 게시:

```powershell
dotnet publish src/TeachingToolWpf/TeachingToolWpf.csproj -c Release -o artifacts/streaming-app
dotnet publish samples/VisionCanvas.Sample/VisionCanvas.Sample.csproj -c Release -o artifacts/streaming-sample
dotnet publish src/VisionInspection.Cli/VisionInspection.Cli.csproj -c Release -o artifacts/streaming-cli
& ./artifacts/streaming-sample/VisionCanvas.Sample.exe --software-rendering
```

기본값은 이 자체 스트리밍 로더다. 실패 후 다른 디코더 재시도는 하지 않는다. 새 게시 폴더를 사용해 이전 NetVips DLL을 섞지 않는다. [전환 기록](../../docs/netvips-removal.md). 게시 폴더에 `VisionImageCodecs.dll`과 라이선스를 함께 유지한다. WPF 앱은 여전히 Windows 전용이며, OpenCV 필터/검사 의존성은 파일 로더 선택과 별개다.

## 지원·오류 계약

| 입력 | 현재 처리 |
|---|---|
| TIFF/BigTIFF | 첫 이미지, contig unsigned Gray8/16·RGB8·straight RGBA8, strip/tile, LE/BE, none/LZW/Deflate/PackBits |
| PNG | Gray8/16, RGB/RGBA8, palette·1/2/4bit gray 확장, 8bit tRNS, 일반/Adam7 |
| WebP | 정적 VP8/VP8L, RGB/straight RGBA, 전체 입력/출력 합계 예산512MiB. 애니메이션 거부 |
| JPEG | 8bit gray/RGB/YCbCr, sequential/progressive, 예산 내 계수 |

TIFF associated alpha/planar separate/YCbCr/JPEG 압축, CMYK·float·16bit RGB·PNG16 alpha, 전체 ICC 색 관리·다중 페이지는 지원하지 않는다. 자동 orientation/gamma 보정은 하지 않는다. 손상 경고도 오류로 처리하므로 일부 특수 metadata 파일은 기존 NetVips보다 엄격하게 거부할 수 있다.

`IRasterReader.ReadRows`는0번부터 연속된 행만 허용한다. Gray16은 little-endian 원본 값이며 색 영상은 RGBA 순서다. 출력은 호출자 소유 버퍼에 기록한다. 잘못된 managed 요청은 읽기 위치를 소비하지 않는다. native 처리 중 오류/취소가 나면 reader를 폐기한다. `SafeHandle`과 C 내부 `setjmp/longjmp` 경계에서 부분 생성 자원을 정리한다. 파일 이름은 Windows UTF-16 / Linux UTF-8로 전달한다.

## 메모리와 캐시

- 전체 원본 Mat을 만들지 않는다. 원본 기록 버퍼는 최대8MiB 요청, 축소 입력은 최대16MiB 행 묶음, 최대8개 worker의 행 scratch를 사용한다. `RawPyramidSource.PreparationBufferPeakBytes`는 실제 대여 배열 길이의 작업 피크이며 native/파일/OS 버퍼와 프로세스 전체 메모리는 별개다. 공통 엔진의 최대 원본 행은4MiB다.
- 타일 reader는 단계마다 필요한8MiB 매핑 창을 사용한다. 반환 행 Span은 다음 읽기 전까지만 유효하며 `ITileSource`의 단일 소유 읽기/해제 계약을 따른다. OS 전체 파일 캐시를 제한하는 기능은 아니다.
- 기본 native 예산512MiB는 TIFF native allocator 및 어댑터 행 묶음, PNG allocator, JPEG 계수/작업 추정에 적용한다. TIFF native와 어댑터 예산은 각각 적용되며 JPEG의 작은 보조 할당을 포함한 프로세스 전체512MiB 하드 상한은 아니다. 8K progressive JPEG는 계수 때문에 약400MiB private가 관찰됐다. 낮은 고정 RAM의 디스크 계수 backing은 구현하지 않았다.
- Adam7은 임시 디스크 파일에서 pass를 합치며 종료·취소에 삭제한다. scratch는 최종 cache 예산과 별도이고 임시 볼륨의 공간 부족은 쓰기 오류로 반환한다.
- raw cache v3는 원본/모든 축소 수준과 완료 manifest를 원자적으로 게시한다. 기본32GiB 예산은 자체 소유 entry의 raw 합계+항목당16KiB 예약이다. 디스크 여유64MiB도 확인한다. root별 import를 직렬화하며, 기존 reader의 읽기는 계속된다.
- 소유 표식·고정 파일명·경로/재분석 지점 검증을 통과한 사용하지 않는 항목만 오래된 순으로 회수한다. reader lease가 살아 있으면 회수하지 않는다. 실패 staging은 즉시 정리하고 비정상 종료 staging은 다음 열기에서 정리한다. 알 수 없는 파일/폴더와 이전 실험 캐시는 보존한다.
- manifest/규격/파일 길이 손상은 재생성한다. 같은 길이의 raw bit corruption을 검출하는 전 픽셀 체크섬은 없다. 원본 version은 기존 길이+UTC 수정 시각 정책이며 내용 해시를 대신하지 않는다.

실측·미달과 남은 검증은 [검증 보고서](../../docs/streaming-image-loader-validation.md), 전체 계획은 [작업 목록](../../tasks/todo.md)을 따른다.


WebP는 행 스트리밍이 아니라 전체 RGBA 디코드다. 내부 workspace는 입력/출력 예산 밖이며 취소는 native 호출 반환 후 확인한다. [메모리·라이선스·검증](../../docs/netvips-removal.md)의 WebP 후속을 따른다.
