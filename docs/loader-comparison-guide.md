> 2026-10-14 후속: NetVips 기본 경로는 제거되었다. 이 문서는 당시 비교 이력이며 현재 사용법은 [NetVips 제거](netvips-removal.md)를 따른다.

# 자체 로더 / NetVips 비교 실행

두 배포는 같은 메인 앱의 Release Windows x64 버전이다. 창 제목의 **자체 파일 로더** / **NetVips 파일 로더**로 구분한다. .NET 10 실행 runtime을 포함하므로 .NET SDK 설치는 필요 없다. OpenCV 검사/필터 등 native 실행에는 기존 Visual C++ x64 runtime 조건이 남는다. WPF 화면은 Windows 전용이다.

## 실행

1. ZIP을 사용하면 먼저 각각의 폴더 전체를 쓰기 가능한 위치에 압축 해제한다. EXE만 따로 옮기지 않는다.
2. `Run.cmd`: GPU 우선, 장치를 만들 수 없으면 CPU(WARP)로 표시한다.
3. `Run-CPU.cmd`: GPU가 있어도 CPU(WARP) 표시를 강제한다. 검사 CUDA 선택과는 별개다.
4. 파일 열기로 같은 TIFF·PNG·JPEG를 선택하고 회전·확대·ROI 편집·Undo/Redo·검사를 비교한다.

실행 파일 이름은 양쪽 모두 `TeachingToolWpf.exe`다. 위 실행 도우미는 각 배포 폴더의 `cache`를 사용하므로 기존 측정 캐시와 섞이지 않는다. EXE를 직접 실행하면 기존 앱 기본 캐시를 사용한다. 최초 파일 열기는 피라미드 생성, 두 번째부터는 캐시 재열기다. 로더 속도를 비교할 때는 한 번에 한 앱만 실행하고 같은 준비 상태를 비교한다. cache를 자동 삭제하는 도우미는 제공하지 않는다.

자체 로더는 TIFF/BigTIFF의 contig Gray8/16·RGB8·straight RGBA8 및 none/LZW/Deflate/PackBits, 일반/Adam7 PNG, sequential/progressive JPEG를 지원한다. 현재 WebP와 `.v`, TIFF JPEG 압축/planar separate/associated alpha, CMYK·16bit RGB/alpha 등은 지원하지 않는다. 공통 파일 대화상자에 표시되는 확장자가 자체 로더의 지원 보장은 아니다. 전체 ICC 색 관리·자동 orientation/gamma·다중 페이지 처리도 지원 범위가 아니다.

초대형 파일은 디코드한 원본과 모든 축소 단계를 저장할 공간이 필요하다. 24576×131072 Gray8/Gray16의 자체 raw 캐시는 각각 약4/8GiB다. Adam7 임시 파일은 별도이며 공간 부족은 오류로 반환한다. progressive JPEG는 계수 RAM을 사용하므로 모든 파일이 낮은 고정 메모리로 열리는 것은 아니다.

## 기본 전환과 완전 제거에 남은 일

**제품 배포물에서 NetVips를 빼는 선택 경로는 이미 구현되어 있다.** 자체 ZIP에는 NetVips 패키지/실행 DLL이 없고, 압축 해제용 LibTIFF/libpng/libjpeg-turbo/zlib를 정적으로 연결한 `VisionImageCodecs.dll`이 있다. 공통 라이선스 문서에 남은 NetVips 언급은 실행 의존성이 아니다. 코덱 자체의 외부 의존성과 업데이트 관리는 계속 필요하다.

| 단계 | 남은 작업 | 완료 판정 |
|---|---|---|
| 사용 범위 확정 | 실제 카메라/고객 파일 corpus를 검사하고 위 미지원 변형·WebP/`.v`의 필요 여부 결정. 필요한 것만 구현하고 UI 안내/필터 정리 | 지원한다고 표시한 입력의 원본 값·색/알파·원본 검사 결과 검증 |
| 운영 안정성 | 장시간 열기/교체, 메모리 압박, 디스크 full·권한·중단 복구 시험. native 빌드/패치·라이선스 배포 절차 고정 | 오류 때 기존 이미지/ROI/이력과 자원 수명 보존 |
| 플랫폼 | 준비된 Linux 환경에서 `.so` 빌드/로드, 세 형식·대용량·취소·검사 CLI 실행 | 실제 Linux 실행 증거. WPF의 Linux 화면 이식은 별도 |
| 입력 반응 | 파일 준비 중 탐색과 ROI 편집을 GPU/WARP에서 재현·최적화 | 기존 엄격한16.67ms 기준. 양쪽 모두 현재 미달이며 NetVips 제거만으로 해결되지 않음 |
| 기본값 변경 | 위 수용 후 호스트의 기본 선택과 native 패키지 조건, 샘플/CLI/문서/빌드를 함께 변경. 실험 엔진을 정식 지원 경로로 정리 | 기본 빌드만으로 자체 경로가 선택되고 지원 대상 회귀 통과 |
| 저장소에서 제거 | 아래 실제 소비자를 이전한 뒤 기존 VisionImaging/NetVips 참조·패키지·코드 정리 | 생산/테스트/benchmark/배포의 활성 NetVips 참조0 |

Linux 검증은 사용자 결정대로 환경 준비 후다. 초대형 TIFF는 각1회 결과를 유지하고 기존 캐시를 보존한다. Linux/60FPS 미달을 이유 없이 통과로 변경하거나 Windows 시험 사용과 플랫폼 전체 완료를 혼동하지 않는다.

## 완전 제거 시 실제 수정 대상

- **호스트/빌드:** `build/ImageLoaderHost.*`, `build/InspectionNativeRuntime.props`, 샘플 csproj, 두 제품 솔루션과 실험 솔루션. 현재는 `UseStreamingImageLoader=true`가 선택 경로다.
- **기존 구현/호환성:** `src/VisionImaging`의 ImageLoader·DiskPyramidSource·VipsRegionPixels·VipsRuntime과 managed 패키지. 옮긴 타입의 forwarding도 공개 소비자가 있으므로 assembly 제거 전에 직접 참조 이전을 확인한다.
- **테스트/입력 생성:** `tests/VisionCanvas.Tests/DiskPyramidTests.cs`, `tests/VisionInspection.Tests/DiskTileRegionTests.cs`, `tests/Shared/PrimitiveShapeFixture.cs`, `build/DiagnosticImageFixtures.cs`의 생성/읽기를 이전한다. 생성기까지 없애면서 테스트를 삭제해서는 안 된다.
- **성능 도구:** `benchmarks/TeachingToolWpf.Matching.Benchmarks`의 ShapeRegression/PaperLatency/KensaText 경로에 기존 파일 version/로더 사용이 남고 ShapeRegression은 NetVips로 PNG도 생성한다. 중립 version·자체 로더·독립 생성기로 이전하고 같은 원본/결과/측정 범위를 보존한다.
- **비교 기준:** `experiments/PyramidComparison/Baseline`과 두 측정 스크립트는 의도적으로 NetVips를 사용한다. 완전 제거 때 기존 결과/해시를 보존하고 기준 구현은 별도 보관하거나 의존성을 제거한다. 비교기 유지와 저장소 전체 의존성0은 동시에 성립하지 않는다.
- **배포 고지/문서:** 실제 남은 의존성에 맞춰 라이선스와 실행법을 정리한다. 이전 실측·결정의 역사적 NetVips 언급을 지워서 근거를 잃지 않는다.

ROI·회전·확대·카메라 메모리 프레임에는 이 제거를 위한 알고리즘 재구현이 필요하지 않다. 파일 loader와 중립 영상 계약의 연결은 이미 분리되어 있다.

재생성 명령(저장소 루트): `pwsh -NoProfile -File scripts/Publish-LoaderComparison.ps1`. 새 시각별 폴더에 두 self-contained 배포와 ZIP, 실행 도우미, 의존성/공통 실행물 해시 결과를 만든다. 이전 캐시는 삭제하지 않는다.

2026-10-13 배포 검증: `artifacts/loader-comparison/20261013-230723`의 두 Release 게시와 ZIP 생성 PASS. 자체 배포의 NetVips 패키지/DLL0개, NetVips 배포의 기존 패키지/DLL 포함 및 자체 codec DLL 미포함, 양쪽 Canvas/알고리즘 DLL SHA256 일치를 확인했다. .NET/Windows Desktop10.0.12 runtime을 포함한다. 두 배포×Auto/CPU의4회 실행에서 실제24576×8192 Gray16 파일 열기,128 ROI 복사/붙여넣기·Undo/Redo와 정상 종료 PASS. Auto는 RTX3060, CPU는 WARP를 사용했다. 기록은 같은 폴더의 `validation/summary.json`이다. 이번 실행은 배포 기능 확인이며 새로운 성능 수용 시험은 아니다. 개발 환경 없는 별도 PC의 native 재배포 조건은 아직 검증하지 않았다.

상세 측정은 저장소 `docs/streaming-image-loader-validation.md`, 현재 작업 상태는 `tasks/todo.md`, 설계 검토는 `docs/decisions/0043-streaming-file-engine.md`를 따른다. 이번 추가는 창 제목과 배포 도우미뿐이며 새 도메인/공개 API·설계 원칙 예외는 없다.

이번 변경의 SOLID 확인: SRP는 창의 식별 표시와 외부 배포 스크립트를 분리, OCP/DIP는 기존 호스트 선택/주입을 사용, LSP/ISP는 영상 계약 변경 없이 양쪽 실제 읽기와 이력을 검증한다. GRASP 확인: Information Expert/Creator는 기존 영상 데이터·자원 소유자를 유지, Controller는 창 제목만 추가, Low Coupling/High Cohesion은 배포를 스크립트에 한정, Polymorphism/Indirection/Protected Variations는 기존 로더 계약·조립 경계를 재사용한다. Pure Fabrication의 캐시/수명 서비스는 변경하지 않으며 실행 도우미에서 캐시 위치만 분리한다.
