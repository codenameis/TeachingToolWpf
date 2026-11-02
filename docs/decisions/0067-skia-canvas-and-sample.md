# ADR-067: 별도 Skia Canvas와 WPF 샘플

2026-10-16. 상태: 사용자 요청에 따른 로컬 구현·검증.

## 결정

사용자가 VisionCanvas를 Skia로 만든 별도 프로젝트와 앱, 기존 코드 규칙 준수를 요청했다. `VisionCanvas.Skia`, `VisionCanvas.SkiaSample`, `VisionCanvas.Skia.Tests`, 독립 `VisionCanvas.Skia.slnx`를 추가한다. 기존 DX12와 별도 Vulkan 작업을 교체하지 않는다.

현재 작업 트리의 `IRenderWorker`, 텍스처 계약, `ScenePainter`, 타일·프레임 준비·캐시를 사용한다. 이는 Vulkan 작업에서 이미 추가한 공통 경계다. 공통 프로젝트에는 Skia의 internal 접근 권한만 추가하고 Core 문서·기하·계층·이력은 수정/복제하지 않는다.

SkiaSharp/NativeAssets.Win32 3.119.2를 고정한다. 전용 스레드의 CPU raster로 이미지·ROI·핸들·레이어·텍스트·미니맵을 그린다. HWND 입력/바인딩은 `Dx12Viewport`를 재사용하고 렌더 생성 지점만 교체한다. 공통 assembly의 Vortice 참조는 남지만 이 실행 경로는 DX12/Vulkan 장치·명령 큐·셰이더를 생성하지 않는다. 완성한 프레임만 GDI `StretchDIBits`로 복사한다. 추가 GPU 컨텍스트/스왑체인은 이번 범위가 아니다.

## 책임·수명·예산

- App: EditorSession·InteractionController·ImageCoordinator·파일 선택기 조립/종료.
- ViewModel: 상태·명령·오류. View: 바인딩·키 전달·Dispatcher·창 수명.
- Core: 형상·계층·선택·취소·Command/Memento 이력.
- SkiaRenderWorker: 최신 요청 병합·단일 렌더 자원 소유·타일 세대·화면 복사·캡처.
- SkiaTexture/Surface: 픽셀/네이티브 버퍼 수명. SkiaRois: BVH 보관 형상/역할. SkiaSprites: 공통 삼각형/UV 표시.

렌더 스레드는 UI 객체를 읽지 않는다. 정지 시 대기하고 WM_PAINT로 가려진 화면을 복원한다. 종료는 renderer→HWND 순서다. CPU 그리기는 동기 완료하므로 이전 프레임 자원을 fence 없이 반환한다.

타일 캐시256MiB·ROI 보관128MiB·공통 준비 큐32개·최신 프레임 mailbox를 유지한다. 가시 자원/출력 bitmap은 별도이며 프로세스 총 메모리 상한이 아니다. Gray16 원본을 보존하고 표시 복사본에서 `(value + 128) / 257`로 Gray8을 만든다. DX12의16비트 texture 보간과 비트 단위 동일성을 보장하지 않는다.

## 원칙 검토

SRP/DIP는 조립·편집·렌더 분리, OCP는 기존 렌더 생성 지점, ISP는 작은 소스/프레임/렌더 계약으로 유지한다. LSP의 소유권·취소·이력·재시작을 실제 창 테스트로 확인한다. 렌더러별 픽셀과 성능 차이는 명시한다.

GRASP의 정보 전문가·생성자·컨트롤러는 Core 기하, Skia 자원 소유자, EditorSession에 둔다. 낮은 결합도·높은 응집도·간접화는 기존 계약으로 유지하고 다형성은 실제 렌더 교체에만 쓴다. 기술 책임은 캐시/worker, 변동 보호는 영상/렌더 경계다. 범용 프레임워크나 서비스 로케이터를 추가하지 않았으며 설계 원칙 예외는 없다.

별도 샘플은 기존 렌더러 샘플과 같은 명령 구성을 사용한다. 샘플 공통 프레임워크로 추출하는 구조 변경은 하지 않는다. 카메라 수집·검사 알고리즘/UI는 제외하며 GPU 가속·엄격한60FPS는 달성했다고 표현하지 않는다.

## 2026-10-16: 1억 ROI와 CPU 렌더 튜닝 후속

사용자 요청으로 샘플의 `1억 ROI 추가`를 기존 `EditorSession.AddRepeatedGrid`에 연결한다. 100,020,001셀을 압축된 한 배치로 추가하며 기존 문서·개별 편집·부분 선택·클립보드·한 건의 Undo를 유지한다. Core의 요약 간격이나 후보 셀을 줄이지 않는다.

반복 윤곽의 네 모서리를 `SKPath.AddPoly` 한 번으로 전달하고, 같은 배치의 회전 변환을 재사용한다. 불투명·단색 흰색 변조·축 정렬 사각 이미지에만 bitmap shader 사각형 경로를 사용한다. UV의 부분 영역, Clamp, 선형/최근접 설정은 기존과 같다. 투명·회전·색 혼합은 삼각형 경로를 유지한다. 직접 `DrawImage` 실험은 1:1 부근 표본 차이로 제외했다.

가독성은 기존 클래스 안에서 이름 있는 단계로 나누어 개선한다. 새 범용 추상화나 매 프레임 배열 복사를 추가하지 않는다. 계측과 이전 구현 비교는 별도 테스트 프로젝트에만 둔다. [실측·픽셀 검증](../skia-canvas-performance.md)의 CPU 그리기 시간과 실제 앱 FPS를 구별한다.

## 공식 API 근거

[검증](../skia-canvas-validation.md), 공식 [고정 패키지](https://www.nuget.org/packages/SkiaSharp/3.119.2), [SKVertices.CreateCopy](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skvertices.createcopy), [SKCanvas.DrawVertices](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcanvas.drawvertices)를 확인했다. 고정 패키지의 [네이티브 고지](../../licenses/skia-canvas/Win32-THIRD-PARTY-NOTICES.txt)와 [MIT 전문](../../licenses/skia-canvas/SkiaSharp-LICENSE.txt)을 복사한다.
