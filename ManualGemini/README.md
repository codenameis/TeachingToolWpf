# ManualGemini — 고등학교 1학년 눈높이로 배우는 VisionCanvas 아키텍처 · 클래스 다이어그램 · 이미지 타일링 · 사용법 · 밑바닥 구현 교본

이 폴더(`ManualGemini`)는 기본적인 **C# 문법(`class`, `struct`, `if`, `for`)과 WPF MVVM 개념**만 아는 입문자가 **산업용 고성능 비전 캔버스(`VisionCanvas`)**를:
1. **실제 프로젝트에 가져다 쓰는 방법(사용법)**을 완벽히 익히고,
2. **고등학교 1학년 눈높이**에서 필요한 **수학(벡터·삼각함수·회전 행렬·이분법·로그)**, **UML 클래스 다이어그램 & 시퀀스 다이어그램**, **대용량 이미지 타일링(512px 피라미드·$\log_2$ LOD·32슬롯 `TileWorker`·256MiB LRU 캐시·Overview 폴백)**, **시스템 프로그래밍(`HWND`, `IntPtr`/`nint`, Win32 `WndProc`, 비트 연산, `SIMD`)**, **알고리즘(불변 스냅샷, BVH 공간 이진 트리)**, **코드 규칙(MS C# 컨벤션·SOLID·GRASP·Composed Method)**을 시각적 다이어그램과 **실시간 애니메이션**으로 이해하며,
3. **이 문서만 보고 빈 폴더에서부터 밑바닥부터 직접 구현(From Scratch)**할 수 있도록 만든 종합 교육 자료입니다.

---

## 📂 폴더 구성 안내

### 1. 인터랙티브 HTML 교과서 (브라우저에서 바로 열기)
오프라인 환경에서도 외부 서버나 인터넷 연결 없이 100% 동작하며, **실시간 인터랙티브 애니메이션 실험실(Play/Pause/슬라이더 조작)**과 **전체/서브시스템별 UML 클래스 다이어그램(SVG)**, **단계별 밑바닥 구현 전체 코드**가 포함되어 있습니다.

- **[index.html](index.html)** — **00장. 통합 마스터 페이지 · 전체 UML 클래스 다이어그램 · 시퀀스 다이어그램 · 4계층 파이프라인 애니메이션**
- **[01-math-and-vectors.html](01-math-and-vectors.html)** — **01장. 고1 눈높이 필수 수학**: 화면 픽셀 vs DIP vs 이미지 좌표, 2D 벡터, 내적(`Dot`)·외적(`Cross`), 회전 행렬(`SinCos`), 마우스 고정 줌(`ZoomAt`) 방정식 유도 + **회전/줌 애니메이션 실험실**
- **[02-hwnd-intptr-simd.html](02-hwnd-intptr-simd.html)** — **02장. 처음 배우는 HWND · IntPtr · SIMD**: 왜 WPF 기본 `Canvas` 대신 Win32 창 핸들(`HWND`, `nint`)을 쓰는가? `WndProc` 메시지 비트 언패킹(`LowWord`/`HighWord`), CPU 1개 코어로 32바이트를 동시에 계산하는 `SIMD`(`Vector256`/`Avx2`) + **비트 분해 & SIMD 병렬 레인 애니메이션**
- **[03-code-rules-and-architecture.html](03-code-rules-and-architecture.html)** — **03장. 코드 규칙과 서브시스템별 상세 UML 클래스 다이어그램**: 고1 눈높이 UML 기호 읽는 법(상속 `──▷`, 구현 `┈┈▷`, 합성 `◆──`, 참조 `──>`), **① 입력/제스처 클래스 다이어그램 · ② 불변 문서/이력 클래스 다이어그램 · ③ 이미지 타일링/렌더러 호스트 클래스 다이어그램**, Microsoft C# 코딩 규칙(Allman), 켄트 백 Composed Method, SOLID & GRASP
- **[04-core-algorithms-and-bvh.html](04-core-algorithms-and-bvh.html)** — **04장. 대용량 이미지 타일링(피라미드·LRU) & 핵심 알고리즘 해부**: **8K~32K 이미지 타일링 5단계 원리(`ImageInfo.CalculateLevelCount`, `TilePlanner.Visible`, `PixelBuffer` ArrayPool, `TileWorker` 32슬롯 큐 & Generation, `GpuImageResources` 256MiB LRU 퇴출 & Overview 폴백)** + **실시간 타일 피라미드/LRU 애니메이션 실험실** + 부모-자식 연속 충돌(40회 이분법 & 슬라이딩) + BVH 공간 트리 애니메이션
- **[05-practical-usage-mvvm.html](05-practical-usage-mvvm.html)** — **05장. 실전 사용법 가이드 (사용자 & 개발자 관점)**: WPF MVVM 앱에서 `Dx12Viewport` / `SkiaViewport` / `VulkanViewport` / `WgpuViewport` 바인딩하기, `EditorSession`·`InteractionController`·`ImageCoordinator` 조립, 커스텀 오버레이 레이어(`DrawingLayerCollection`), 키보드 라우팅, 마우스 조작법 총정리
- **[06-from-scratch-implementation.html](06-from-scratch-implementation.html)** — **06장. 밑바닥부터 직접 구현하기 (Step-by-Step 코드북)**: 빈 폴더에서 시작해 1단계(`VisionGeometry`) → 2단계(**이미지 타일링 & 피라미드 엔진 `TilePlanner`/`TileWorker`**) → 3단계(`VisionCanvas.Core` 상태 머신 & Undo/Redo) → 4단계(`Win32 HWND` & `SIMD`) → 5단계(`WPF MVVM` 앱)까지 그대로 따라 치며 완성하는 자급자족 구현 가이드

### 2. 마크다운(Markdown) 핵심 문서
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — `VisionCanvas` 전체 아키텍처, 서브시스템별 Mermaid UML 클래스 다이어그램 3종, 이미지 타일링 메커니즘 상세 문서
- **[USAGE-GUIDE.md](USAGE-GUIDE.md)** — 실제 사용하는 입장에서의 `VisionCanvas` API 사용법, MVVM 연결 레시피, 조작 단축키 및 주의사항 매뉴얼
