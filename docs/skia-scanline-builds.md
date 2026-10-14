# 자체 로더 / Skia JPEG scanline 비교 빌드

Windows x64 Release self-contained 메인 앱 두 종류다. .NET SDK/런타임 별도 설치는 필요하지 않으며 기존 OpenCV 검사/필터의 Visual C++ x64 runtime 조건은 유지한다. 두 ZIP 모두 NetVips 실행 의존성을 포함하지 않는다.

| 폴더 | JPEG | PNG | TIFF/BigTIFF |
|---|---|---|---|
| Streaming | 기존 libjpeg-turbo 순차 행 | 기존 libpng 스트리밍 | 기존 LibTIFF |
| Skia | SkiaSharp scanline | Skia 전체 비트맵(Gray16 PNG 거부) | 기존 LibTIFF |

1. 각 ZIP을 별도 쓰기 가능한 폴더에 **전체 압축 해제**한다. EXE만 이동하지 않는다.
2. `Run.cmd`는 GPU 우선/장치 생성 불가 시 CPU(WARP), `Run-CPU.cmd`는 CPU 표시를 강제한다.
3. 두 앱의 제목에서 `Streaming` 또는 `Skia JPEG Scanline`을 확인한다.
4. 파일 열기로 같은 JPEG를 선택한다. 일반 JPEG와 progressive JPEG를 별도로 비교한다. ROI·회전·확대·Undo/Redo는 공통 Canvas다.
5. 한 번에 한 앱씩 실행한다. 첫 열기는 피라미드 생성, 재열기는 캐시 읽기다. 캐시는 각 배포의 `cache` 폴더이며 자동 삭제하지 않는다. EXE 직접 실행보다 제공한 cmd를 사용한다.
6. 성능 수치는 [scanline 실측](skia-jpeg-scanline.md)을 참고한다. 앱 실행 검증은 새60FPS 보장을 뜻하지 않는다.

Skia JPEG는 전체 RGBA 원본 비트맵 없이 호출자의 행 버퍼로 디코드한다. progressive JPEG는 코덱 내부 계수 메모리가 여전히 필요하다. 비교용 JPEG 원본 크기 제한512MiB는 프로세스 전체 메모리 상한이 아니다. PNG의 전체 비트맵/512MiB 제한과 Gray16 PNG 미지원은 이전과 같다. Linux 실제 실행은 환경 준비 후다.

저장소에서 재생성:

```powershell
pwsh -NoProfile -File scripts/Publish-LoaderComparison.ps1 -CompareSkia
```

새 시각별 `artifacts/loader-comparison` 폴더에 두 빌드와 ZIP/의존성 해시 기록을 만든다. 기존 배포와 캐시를 덮어쓰지 않는다. .NET, Skia 및 TIFF/native 코덱 라이선스 고지는 각 배포의 `licenses`에 포함된다. 2026-10-14 후속으로 생산 기본값도 Streaming으로 전환했다. [NetVips 제거](netvips-removal.md).
