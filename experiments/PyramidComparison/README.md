# NetVips 대체 가능성 실험

2026-10-13 후속: 전체 Mat 없는 TIFF/PNG/JPEG 경로 `Streaming`과 [코덱 빌드/선택형 앱 사용법](../VisionImaging.Codecs/README.md)을 추가했다. 아래 OpenCV 비교도 유지한다. 솔루션 전체 또는 Baseline 빌드 전 `pwsh -File experiments/VisionImaging.Codecs/Build-Native.ps1`을 실행한다. 새 [실측·검증](../../docs/streaming-image-loader-validation.md)은 이전 실험 수치와 구별한다.

기존 앱을 바꾸지 않고 **OpenCV 파일 디코더 + 자체 C# 피라미드·타일 캐시**를 기존 NetVips 경로와 비교한다. PNG/JPEG/TIFF 압축 코덱 자체를 새로 만든 것은 아니다. 결과와 수용 범위는 [실측 보고서](../../docs/pyramid-decoder-comparison.md)를 따른다.

## 경계와 플랫폼

```text
파일 → VisionImaging.OpenCv → IRasterReader
                              ↓
                    VisionImaging.Pyramid → ITileSource → 소비자
                              ↓
                    디스크 raw 단계 + memory map

비교 기준: 파일 → 기존 VisionImaging / NetVips → ITileSource
```

- `VisionImaging.Pyramid`: `net10.0`, BCL + 기존 `VisionImaging.Abstractions`만 참조. Gray8·Gray16·RGBA32, 512px 타일, 2배 삼각 필터, 디스크 캐시를 구현한다. Windows API·WPF·DX12·OpenCV·NetVips 참조가 없다.
- `VisionImaging.OpenCv`: `net10.0` 어댑터. OpenCvSharp4 `4.13.0.20260627`의 Mat을 내부에만 보관한다. 파일의 전체 원본을 먼저 디코딩하며 Gray16은 원본 명암을 유지하고 BGR/BGRA는 RGBA로 변환한다.
- `Own`: 자체 경로만 참조하는 콘솔 호스트. OS/RID에 따라 Windows 또는 Linux x64 OpenCV native 패키지를 연결한다. NetVips 패키지가 없다.
- `Baseline`: 기존 NetVips 비교 호스트, fixture 생성과 픽셀 비교를 함께 제공한다. 자체 엔진의 실행 의존성과 구별한다.
- `VisionImaging.Pyramid.Tests`: 외부 코덱 없이 동작하는 공통 엔진 계약 테스트.

공통 엔진은 **64비트 little-endian** 프로세스가 대상이다. Linux 실행 검증과 Windows에서의 Linux 배포물 생성은 서로 다르다. 기존 WPF/DX12 VisionCanvas 화면을 Linux로 이식하는 작업은 포함하지 않는다. ROI·회전·확대·이력 구현도 변경하지 않는다. 카메라의 메모리 프레임은 기존 `VideoFrame` 경로를 그대로 쓸 수 있으며 파일 디코더를 거칠 필요가 없다.

## 실행

저장소 루트에서 .NET 10 SDK로 실행한다. 실험 솔루션은 생산 솔루션과 별도다.

```powershell
dotnet build experiments/PyramidComparison.slnx -c Release
dotnet test experiments/VisionImaging.Pyramid.Tests -c Release
pwsh -NoProfile -File experiments/PyramidComparison/Measure.ps1
```

`Measure.ps1`은 fixture를 준비하고 구현 순서를 번갈아 3회씩 실행한다. `artifacts/pyramid-comparison/run-*`에 raw JSON·로그·새 캐시를 남긴다. 기존 입력·캐시는 삭제하지 않는다. 초대형 기존 파일도 비교하려면 해당 두 TIFF가 있는 폴더를 `-LargeInputDirectory`에 전달한다.

개별 측정과 픽셀 비교:

```powershell
dotnet experiments/PyramidComparison/Baseline/bin/Release/net10.0/Baseline.dll fixtures artifacts/pyramid-comparison/inputs
dotnet experiments/PyramidComparison/Own/bin/Release/net10.0/Own.dll IMAGE NEW_CACHE REPORT.json
dotnet experiments/PyramidComparison/Baseline/bin/Release/net10.0/Baseline.dll IMAGE NEW_CACHE REPORT.json
dotnet experiments/PyramidComparison/Baseline/bin/Release/net10.0/Baseline.dll compare IMAGE COMPARISON_CACHE QUALITY.json
```

`compare`는 결정적 타일 표본의 **전체 채널 값**을 비교한다. 차이가 있으면 `DIFFERENT`와 종료 코드1을 반환하며 평균/최대 오차를 기록한다. 성공한 표본 비교를 모든 가능한 입력의 동등성으로 해석하지 않는다. 성능 측정의 양 끝 바이트 checksum은 정확성 검사를 대신하지 않는다.

Linux x64 배포물 생성:

```powershell
dotnet publish experiments/PyramidComparison/Own -c Release -r linux-x64 --self-contained false -o artifacts/pyramid-comparison/linux-own
```

단계별 메모리 진단과 변경 전/후 비교는 [메모리·성능 후속 검토](../../docs/pyramid-memory-review.md)를 따른다. `inspect`는 디코드/원본 저장/피라미드/재열기/타일 읽기/해제 경계를 기록하고20ms로 구간별 피크를 표본 추출한다. 강제 GC는 수행하지 않으며 일반 성능 실행과 구별한다.

```powershell
dotnet experiments/PyramidComparison/Own/bin/Release/net10.0/Own.dll inspect IMAGE NEW_CACHE MEMORY.json
dotnet experiments/PyramidComparison/Baseline/bin/Release/net10.0/Baseline.dll inspect IMAGE NEW_CACHE MEMORY.json
pwsh -NoProfile -File experiments/PyramidComparison/CompareVariants.ps1 -BeforeAssembly BEFORE/Own.dll -AfterAssembly AFTER/Own.dll
```

실제 Linux에서는 .NET 10과 native 패키지에 필요한 시스템 라이브러리를 갖춘 뒤 `dotnet Own.dll IMAGE NEW_CACHE REPORT.json`을 실행한다. OpenCvSharp native 배포의 지원 환경은 [프로젝트 안내](https://github.com/shimat/opencvsharp)를 확인한다. 배포물 생성 성공만으로 Linux 지원 완료를 선언하지 않는다.

## 실험 구현의 한계

- OpenCV `ImRead`는 전체 원본 Mat을 소유한다. 피라미드 작성은 행 단위여도 디코딩 메모리가 원본 크기에 비례한다. native 디코딩 중 취소는 즉시 중단되지 않고 호출 반환 후 확인한다.
- 원본·모든 축소 단계를 raw로 저장한다. 디스크 사용량은 일반적인 2D 이미지에서 비압축 원본의 약4/3이며, 가늘고 긴 이미지는 이 비율과 다르다. memory map은 프로세스 RAM 상한을 보장하지 않는다.
- Gray8·Gray16·8비트 색상 첫 이미지가 대상이다. 다중 페이지/애니메이션 전체, 16비트 RGB, float, ICC/EXIF 의미 동등성은 보장하지 않는다.
- 실패/취소 staging은 진단용으로 남는다. 자동 퇴출·복구·동시 동일 파일 import 조정·내용 해시 검증은 구현하지 않았다. 완료 manifest와 정확한 raw 파일 길이를 검사하고 손상 캐시는 오류로 반환한다.
- 원본 파일 변경 감지는 기존 경로처럼 길이와 수정 시간에 의존한다. 캐시는 사용자가 신뢰하는 로컬 폴더에 둔다.
- 타일 읽기는 `ITileSource`의 단일 소유자 계약을 따른다. Read와 Dispose를 동시에 호출하지 않는다. 반환 `PixelBuffer`는 호출자가 해제하거나 기존 Canvas에 소유권을 전달한다.
- 이 실험의 I/O 지연은 UI 프레임 지연이 아니다. Linux 화면, 실제 저사양 PC, 장시간 스트리밍, ROI 편집60FPS는 별도 검증이 필요하다.

## 설계 검토

원칙 예외 없이 기존 계약의 별도 구현과 실제 디코더 변형 경계만 추가했다. 생산 코드를 바꾸지 않는다.

| 원칙 | 이번 적용/범위 |
|---|---|
| SRP | 디코드, 피라미드 필터, 캐시/소유권, 측정 책임 분리 |
| OCP | 디코더 교체가 공통 캐시·Canvas 수정으로 이어지지 않음 |
| LSP | ITileSource의 픽셀 형식·가장자리·취소·해제 계약 테스트. 지원 파일 범위와 native 취소 지연은 명시 |
| ISP | 디코더/연속 행 reader와 기존 타일 소비 계약을 작게 유지 |
| DIP | 공통 엔진은 영상 계약만 참조, 호스트가 어댑터/native 패키지 조립 |
| Information Expert | 축소 규칙은 TrianglePyramid, 이미지 규격/단계 수는 기존 ImageInfo |
| Creator | RawPyramidSource가 단계 수명, OpenCV reader가 Mat 수명 소유 |
| Controller | 콘솔은 실험 순서만 제어. UI 업무 규칙 추가 없음 |
| Low Coupling | UI·검사·GPU 역참조 없음, 공개 계약에 Mat 없음 |
| High Cohesion | 파일 경로가 ROI·검사 정책을 알지 않음 |
| Polymorphism | 실제 디코더와 테스트 reader, 기존 ITileSource 대체 구현 사용 |
| Pure Fabrication | raw 캐시와 측정은 영상 도메인 밖 기술 책임 |
| Indirection | IRasterReader가 OpenCV의 메모리/채널 배치를 격리 |
| Protected Variations | 코덱/native 배포 변경은 어댑터·실행 호스트에 한정 |
