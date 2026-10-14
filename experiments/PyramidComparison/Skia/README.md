# Skia PNG/JPEG 비교 실행물

SkiaSharp4.153.1 JPEG scanline / PNG 전체 비트맵 → 기존 C# raw 피라미드/타일. TIFF/BigTIFF는 기존 native 디코더를 사용한다. 별도 CLI 및 선택형 메인 앱 비교 빌드다. [최신 실측](../../../docs/skia-jpeg-scanline.md), [앱 빌드 실행법](../../../docs/skia-scanline-builds.md).

저장소 루트에서 .NET10 SDK와 기존 `VisionImageCodecs.dll` 준비 후 PowerShell7로 실행한다.

```powershell
dotnet build experiments/PyramidComparison/Skia/Skia.csproj -c Release
dotnet build experiments/PyramidComparison/Streaming/Streaming.csproj -c Release
dotnet test experiments/VisionImaging.Skia.Tests/VisionImaging.Skia.Tests.csproj -c Release

# 둘 중 원하는 로더를 단독 실행. 각 호출의 cache 경로는 새 경로여야 한다.
dotnet experiments/PyramidComparison/Skia/bin/Release/net10.0/Skia.dll "D:/images/test.png" "D:/compare/skia-01" "D:/compare/skia-01.json"
dotnet experiments/PyramidComparison/Streaming/bin/Release/net10.0/Streaming.dll "D:/images/test.png" "D:/compare/native-01" "D:/compare/native-01.json"

# 같은 파일의 원본/피라미드 타일 표본 비교. EXACT_SAMPLED=0, DIFFERENT=1.
dotnet experiments/PyramidComparison/Skia/bin/Release/net10.0/Skia.dll compare "D:/images/test.png" "D:/compare/quality-01" "D:/compare/quality-01.json"

# 이 저장소에 보존된8K 입력4개 × 로더2개 × 각5회. 캐시 자동 삭제 없음.
pwsh -File experiments/PyramidComparison/Compare-Skia.ps1

# JPEG2입력만 새 캐시로 비교
pwsh -File experiments/PyramidComparison/Compare-Skia.ps1 -JpegOnly

# 메인 앱 자체/Skia 두 self-contained ZIP 생성
pwsh -File scripts/Publish-LoaderComparison.ps1 -CompareSkia
```

측정 JSON의 ImportMilliseconds는 디코드만이 아니라 최초 로드·JIT·피라미드 생성·디스크 저장까지 포함한다. 디코딩과 관계없는 ROI 회전/확대 성능을 이 결과로 판단하지 않는다. 메모리는 프로세스 전체 Private/Working Set이다.

Gray16 PNG는 명시적으로 거부하며 기존 Streaming 실행물로 확인할 수 있다. Skia는512MiB 원본 크기 한도를 보존하지만 JPEG는 그 크기의 비트맵을 할당하지 않는다. 내부 코덱 메모리를 포함한 프로세스 상한도 아니다. JPEG는64행 호출 사이에 취소를 확인하며 실행 중인 native 호출의 취소는 완료 후 반영된다.

Linux용 Skia 패키지는 연결했지만 실행 검증하지 않았다. 기존 TIFF native shim을 해당OS에서 빌드해야 한다. 실험 배포에는 `licenses/skia`와 `licenses/streaming-codecs` 고지가 함께 복사된다.
