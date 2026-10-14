# NetVips 제거와 기본 파일 로더

2026-10-14 사용자 요청에 따라 새 앱·샘플·검사 CLI의 기본 파일 로더를 자체 스트리밍 구현으로 전환했다. NetVips 관리 패키지와 Windows/Linux libvips 패키지를 참조하지 않는다. 기존 측정 원본·캐시·배포 ZIP은 이력으로 보존하며 새 배포 폴더와 구별한다.

## 사용과 경계

- 기본 지원: TIFF/BigTIFF, PNG, JPEG, 정적 WebP(손실·무손실·알파). 기존 계약의 Gray8·Gray16·RGBA8, 원본 level 0, 피라미드·타일·취소·버퍼 수명을 유지한다.
- BMP·VIPS(.v)와 애니메이션 WebP는 지원하지 않는다. 파일 대화상자는 지원 형식만 표시하고, 미지원 입력은 오류로 종료한다.
- TIFF는 현재 구현한 압축/픽셀 조합만 지원한다. JPEG 압축 TIFF, CMYK, 모든 ICC/EXIF 조합을 지원한다고 표현하지 않는다. 상세 제한은 [코덱 설명](../experiments/VisionImaging.Codecs/README.md)을 따른다.
- Canvas/Core/카메라 프레임 경로에는 파일 코덱 참조를 추가하지 않는다. 파일 호스트가 VisionImaging을 참조한다.
- 기본 경로: ImageLoader → DiskPyramidSource(기존 공개 API 유지) → RawPyramidSource → NativeRasterDecoder → VisionImageCodecs.dll.
- 순수 C# 피라미드와 네이티브 코덱 프로젝트의 위치는 현재 experiments 아래에 유지한다. 폴더 이동 없이 실제 기본 빌드에 연결했고 솔루션 Release 구성에도 등록했다.
- Skia 비교는 UseSkiaImageLoader=true에서만 연결한다. 이전 UseStreamingImageLoader 플래그는 더 이상 로더를 전환하지 않는다.
- OpenCV 검사·필터와 ILGPU CUDA는 이번 파일 로더 교체와 별개다. 테스트 영상 생성기의 OpenCV 참조는 제품 파일 읽기 경로에 포함되지 않는다.

기존 .v 캐시를 변환하거나 지우지 않는다. 자체 캐시는 별도 identity와 소유 표식을 사용한다. 자체 캐시의 디스크 예산/LRU 회수 정책은 기존 구현과 같다. 보존된 측정 캐시를 새 실행의 cache 경로로 지정하지 않는다.

## 빌드

먼저 native 코덱을 해당 OS에서 빌드한다.

~~~powershell
pwsh -NoProfile -File experiments/VisionImaging.Codecs/Build-Native.ps1
dotnet build TeachingToolWpf.slnx -c Release
pwsh -NoProfile -File scripts/Publish-LoaderComparison.ps1
~~~

기본 게시 스크립트는 새 시각별 artifacts/loader-comparison 폴더에 Streaming 배포와 ZIP 하나를 만든다. -CompareSkia를 붙이면 선택형 Skia 배포도 만든다. 전체 ZIP을 풀고 Run.cmd 또는 Run-CPU.cmd로 실행한다. CPU 모드는 Windows WARP다. 자체 포함 .NET runtime과 코덱/검사 native 파일을 함께 유지하며 Visual C++ x64 runtime 조건도 유지한다.

## 라이선스 판단 범위

검토한 고정 코덱은 LibTIFF4.7.2, libpng1.6.59, libjpeg-turbo3.2.0, zlib1.3.2, libwebp1.6.0이다. 각 원문은 licenses/streaming-codecs에 보존한다. 이 허용적 라이선스에는 이들을 정적으로 연결한 응용 프로그램의 소스 공개나 재링크용 오브젝트 제공 의무가 없다. 저작권·면책·필요한 출처 고지는 배포에 유지해야 한다. 이는 검토한 코덱 구성의 결론이며 전체 응용 프로그램/추가 플러그인의 법률적 보증이 아니다.

VisionImageCodecs.dll은 위 다섯 코덱을 정적으로 포함한다. libtiff.dll 등의 별도 파일은 없으며 NetVips/libvips/GLib를 포함하지 않는다. 빌드 고정 버전·해시·옵션은 native/dependencies.json과 Build-Native.ps1을 따른다. 코덱을 수정하거나 추가하면 해당 조건을 다시 확인한다.

This software is based in part on the work of the Independent JPEG Group.
LibTIFF LZW 구현에는 University of California, Berkeley가 개발한 소프트웨어가 포함된다.

공식 근거: [libjpeg-turbo](https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/LICENSE.md), [libpng](https://www.libpng.org/pub/png/src/libpng-LICENSE.txt), [zlib](https://zlib.net/zlib_license.html), [LibTIFF 원문](../licenses/streaming-codecs/libtiff-LICENSE.md). 배포 고지는 THIRD-PARTY-NOTICES.md를 따른다.

## 측정 도구 변경

Baseline CLI의 실행 라벨은 NativeReference이며 예전 NetVips 실행물이 아니다. Measure.ps1은 NativeReference/OpenCV 비교로 전환했다. compare 명령의 보고서에도 양쪽 구현을 명시한다. 이전 compare-streaming/Measure-Streaming.ps1은 같은 자체 엔진을 다른 이름으로 비교하지 않도록 오류로 안내한다. 과거 NetVips 압축 corpus 생성 명령도 폐기했다. 기존 원본과 결과는 보존한다.

새 fixture 생성기는 기존 OpenCV와 작은 무압축 TIFF/BigTIFF 작성기로 입력을 만든다. 픽셀 패턴은 유지하지만 압축 바이트·타일/strip 배치가 이전 생성기와 같다고 가정하지 않는다. 측정 전 입력 SHA256을 확인하며 새 입력 결과와 기존 결과를 섞지 않는다.

## 검증 범위

전환 검증 결과는 아래에 추가한다. 기존 성능 실측은 역사적 기록이며 이번 기본값 변경을 새 속도 개선이나60FPS 달성으로 해석하지 않는다. Linux 실제 실행·다른 PC 검증은 환경 준비 후다.

### 2026-10-14 Windows 검증 결과

| 항목 | 결과 |
|---|---|
| TeachingToolWpf.slnx Release | PASS, 경고0/오류0 |
| 메인 솔루션 회귀 | PASS1,210: Core296/Canvas228/Imaging274/Algorithm249/Inspection111/Compute52 |
| 별도 파일 엔진 회귀 | PASS68: native26/pyramid24/Skia18 |
| 기본 파일 전환 | 위 Canvas에 포함된12건:24K Gray16·PNG/JPEG 채널·BigTIFF 행·취소·캐시 재열기·기존 캐시 보존·미지원 WebP/BMP 거부 |
| 실제 기본 배포 앱 | 8K JPEG·128 ROI·복사/붙여넣기·Undo/Redo, RTX3060 GPU 및 CPU(WARP) 두 모드 모두 PASS |
| 독립 샘플 | CPU(WARP)8K/24K 합성·작은 PNG·ROI/이미지 통합 이력·파일 실패 원자성 PASS |
| 검사 CLI | paper/geometric/template/primitives 완료, 검출2/2/1/4 |
| 신규 앱/샘플/CLI 배포 | NetVips/libvips/Skia 및 fixture 패키지0, native 코덱 동일SHA256 |
| Linux·다른 PC·현장 corpus·새60FPS 수용 | NOT_RUN. 기존 성능 미달은 유지 |

실행 증거는 artifacts/netvips-removal의 gpu.json, cpu.json, sample-smoke/report.json, 네 검사 결과 및 dependency-check.json에 보존했다. 의존성 증거의 사본은 [배포 점검](netvips-removal-dependencies.json)이다. 첫 전환에서 OpenCV가 만든 RGBA TIFF의 ExtraSamples 누락을 발견했고 fixture 작성기를 수정한 뒤 위 전체 회귀가 통과했다. 지원 형식 검증을 완화하지 않았다.

기본 self-contained ZIP: artifacts/loader-comparison/20261014-100720/Streaming-win-x64.zip.
실행 파일 해시와 의존성 목록은 같은 폴더의 Streaming/build-info.json에 있다.

소스 정리: 사용자 승인으로 VipsRuntime.cs/VipsRegionPixels.cs 두 파일을 각각 삭제했고 임시 Compile 제외도 제거했다. 이전 NetVips 패키지 캐시·bin 잔여 파일·옛 배포 ZIP도 일괄 삭제하지 않았다. 새 게시 폴더의 의존성0개와 저장소 디스크의 모든 역사적 흔적 삭제는 서로 다른 상태다.

선택형 Skia 앱도 별도 publish가 PASS였고 NetVips0개 및 Skia 라이선스 포함을 확인했다. 기본값으로 다시 빌드해 Release0경고/0오류를 확인했다. 이 추가 빌드는 새 성능 비교 측정이 아니다.

### WebP 유지 후속

사용자가 WebP 지원 유지와 전용 소스 두 파일 삭제를 승인했다. libwebp1.6.0의 decoder-only static target만 VisionImageCodecs에 연결한다. encoder·mux·demux·CLI는 제품 DLL에 연결하지 않는다. 다운로드 URL·SHA256을 고정하고 COPYING/PATENTS를 배포한다. [공식 라이선스](https://github.com/webmproject/libwebp/blob/v1.6.0/COPYING)는 BSD-3-Clause로 응용 소스 공개를 요구하지 않으며 저작권·조건·면책 고지와 비보증 조건을 유지한다. 특허 허여의 범위/종료 조건은 [PATENTS](https://github.com/webmproject/libwebp/blob/v1.6.0/PATENTS)를 따른다.

정적 VP8/VP8L·straight RGBA를 읽는다. 확장자 대신 RIFF/WEBP signature로 판별한다. 애니메이션을 첫 프레임으로 조용히 축소하지 않고 거부하며 BMP/VIPS는 계속 미지원이다. ICC/EXIF 자동 변환도 추가하지 않는다.

WebP는 전체 압축 입력과 RGBA 출력 버퍼를 사용한다. 합계와 adapter 구조체가 기본512MiB 예산을 초과하면 거부하고 크기·RIFF 길이·절단을 검사한다. libwebp 내부 workspace는 이 예산 밖이며 프로세스 전체512MiB 보장은 아니다. 8192² RGBA 출력은256MiB다. 입력 읽기는64KiB마다 취소를 확인하지만 단일 WebPDecode 호출 중 즉시 취소는 지원하지 않고 반환 후 확인한다. TIFF/PNG/JPEG와 같은 낮은 메모리의 행 스트리밍이라고 표현하지 않는다. 추후 메모리 개선은 별도 측정 대상이다.

새 형식 추가이며 TIFF/PNG/JPEG 출력 규칙을 바꾸지 않았으므로 기존 native cache identity는 유지한다. 새 WebP는 기존 자체 캐시가 없고 기존 다른 형식 캐시를 불필요하게 재생성하지 않는다. WebP 코덱 버전/출력 정책 변경 시 cache identity 재검토가 필요하다. 선택형 Skia 비교 호스트의 WebP도 기본 libwebp 경로로 연결한다.

검증 결과와 새 실행 ZIP은 아래 기록을 따른다. 기존 ZIP은 WebP 추가 이전 이력으로 보존한다.
WebP 후속 검증:
- PASS: native 빌드(/W4 /WX), 메인 Release0경고/0오류, Canvas228회귀.
- PASS: WebP12건(손실/무손실/알파·signature·절단2종·RIFF 예산·취소·소유권·animation flag 거부), 기본 파일 통합의8192px 폭 WebP.
- PASS: 기존 native26/pyramid24/Skia18회귀를 새 DLL로 실행. WebP 테스트를 플랫폼 독립 Codecs.Tests로 옮긴 추가 실행에서는37 PASS/1 FAIL(디스크 부족으로256MiB TIFF fixture 생성 실패). 기준을 낮추지 않았다. 동일 기존 PackBits 입력을 사용한8MiB 예산 거부/512MiB 원본 행 확인은 별도 PASS(artifacts/netvips-webp/giant-strip-reuse.txt).
- PASS: 8192×8192 straight RGBA WebP의 모든 픽셀 일치(artifacts/netvips-webp/8k-decode.json). 시간에는 픽셀 검증을 포함하며 성능 비교 결과가 아니다.
- 앱 진단:8192×1024 WebP 파일 준비는 성공했으나 기존8192² ROI fixture가 이미지 밖이라 이후 편집 진단은 FAIL. 결과를 artifacts/netvips-webp/gpu.json에 보존했다. 이를 ROI 편집 전체 PASS나8K 정사각 앱 표시 검증으로 해석하지 않는다. 이전 TIFF/JPEG 앱 편집 검증은 별도 기록이다.
- 공간 제한으로 새 게시 폴더 복사 없이 MSBuild ResolvedFileToPublish의 모든 파일을 직접 ZIP에 넣는다. 기존 결과·배포 ZIP·벤치 캐시는 보존했다. 이번 테스트가 만든 단일256MiB 임시 TIFF만 회수했다.
- Linux 실행·WebP 처리 중 즉시 취소·낮은 고정 메모리 WebP·애니메이션·새60FPS 수용: NOT_RUN/미구현 범위 유지.

선택형 Skia 호스트의 WebP 라우팅도 수정했다. 해당 선택 빌드의 C# 오류는 없었으나 다중 RID 네이티브 디버그 심볼 복사 중 디스크 부족으로 빌드가 실패했다(추가 검증 미완료). 기본 win-x64 self-contained Build/ComputeFilesToPublish는 그 전에 성공했고 게시 목록은 artifacts/netvips-webp/publish-files.json에 있다.

사용자 승인 범위인 src/TeachingToolWpf/bin/Release/net10.0-windows/runtimes 폴더만 삭제해 공간을 확보했다. 새 WebP 포함 ZIP은 artifacts/netvips-webp/Streaming-WebP-win-x64.zip이며 665개 항목의 SHA256을 원본과 대조했다. NetVips/Skia/fixture 패키지가 없으며 WebP COPYING/PATENTS를 포함한다. 상세 해시는 같은 폴더의 build-info.json에 기록한다. 압축을 풀고 Streaming/Run.cmd로 자동 GPU/WARP 선택, Streaming/Run-CPU.cmd로 강제 CPU(WARP) 실행한다. 이전 ZIP·원본·벤치마크 캐시는 보존했다. 새 ZIP의 무결성 검증은 다른 PC에서의 실행 검증을 대신하지 않는다.
