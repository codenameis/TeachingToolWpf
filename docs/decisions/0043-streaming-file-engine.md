> 2026-10-14 후속: NetVips 기본 경로는 제거되었다. 이 문서는 당시 비교 이력이며 현재 사용법은 [NetVips 제거](../netvips-removal.md)를 따른다.

# ADR-043: 선택형 스트리밍 파일 엔진

- 날짜: 2026-10-13
- 상태: 사용자 요청으로 Windows 선택 경로 구현·검증. Linux 실제 검증은 환경 준비 후로 사용자 지정. 기본 로더 전환은 수용 전 보류.
- 범위: 현재 main의 로컬 변경·검증. 커밋·푸시 없음. [계획](../../tasks/streaming-image-loader-plan.md), [실측](../streaming-image-loader-validation.md).

## 결정과 경계

압축 코덱을 재작성하지 않고 고정 버전 LibTIFF/libpng/libjpeg-turbo/zlib를 C shim으로 격리한다. C# `VisionImaging.Pyramid`가 원본 raw, 삼각 축소, 타일과 캐시 수명을 소유한다. C# 코덱 공개 계약에 native 포인터/NetVips/OpenCV 타입을 노출하지 않는다. managed 프레임을 넘어 longjmp하지 않는다.

```text
파일 → VisionImaging.Codecs → IRasterReader → VisionImaging.Pyramid
                                              ↓ RawImageLoader / ITileSource
앱·샘플·CLI 조립 → SourceImageLoader → IImageLoader를 받는 Canvas / 검사
                        ↓
              VisionImaging.Sources (live/procedural/synthetic)

기존 선택: SourceImageLoader → VisionImaging / NetVips
```

`build/ImageLoaderHost.props`의 `UseStreamingImageLoader=true`가 새 구현을 연결한다. 기본 NetVips는 보존한다. 새 경로 실패를 NetVips로 숨기지 않는다. 아직 실험 프로젝트를 선택 빌드에서 참조하며 안정화/양 플랫폼 수용 전에 기본 프로젝트로 승격하지 않는다.

`LiveImageSource`, `SyntheticFrameSource`, `ProceduralTileSource`와 내부 패턴은 native 참조 없는 `VisionImaging.Sources`로 옮긴다. 파일과 생성 소스를 분기하는 규칙은 두 파일 구현이 사용하는 `SourceImageLoader` 한 곳에 둔다. 픽셀/ROI 복사를 추가하지 않는다. 원본 Gray8 선택 계약 `IOriginalGrayTileSource`는 Abstractions assembly로 옮겨 VisionInspection의 VisionImaging 참조를 제거한다. 기존 namespace와 이전 assembly의 type forwarding을 유지한다. 파일 version 계산은 중립 `ImageFileVersion`이며 기존 `DiskPyramidSource.Version`은 호환 위임으로 남긴다.

ROI·회전·확대·계층·통합 이력, Canvas의 GPU/WARP 선택, 카메라 메모리 프레임 흐름은 변경하지 않는다. 파일 표시와 검사는 각각 독립 reader를 받는다. fixture 생성은 NetVips 비교/기본 호스트에만 포함하며 Streaming 진단은 준비된 입력을 사용한다.

## 수명과 트레이드오프

준비는 전체 매핑 대신 불변 행 묶음을 worker에 공유한다. 읽기는 단일 소유 reader의 제한 매핑 창을 쓴다. 매핑 교체 비용과 메모리 절감은 실측으로 판단한다. progressive JPEG 계수는 여전히 큰 RAM이 필요하며 예산 초과를 거부한다. Adam7은 낮은 작업 메모리 대신 scratch 디스크 I/O를 사용한다.

cache v3는 root별 import lock으로 서로 다른 준비의 공간 경합까지 직렬화한다. 이미 열린 source는 계속 읽는다. 읽기 lease가 있는 항목은 삭제하지 않는다. 소유 표식이 확인된 고정 이름 파일만 회수하고, 잘못된 표식·임의 파일·재분석 지점은 보존한다. 같은 크기 raw의 내용 손상을 검사하는 해시는 이 버전에 없다.

코덱 소스·빌드·native 배포 및 보안 수정 추적은 우리가 관리한다. 외부 의존성을 없앤 구현으로 표현하지 않는다. Linux용 파일/검사 엔진과 WPF 화면 이식은 별도다.

## SOLID·GRASP 확인

| 원칙 | 확인 |
|---|---|
| SRP | 코덱 오류/수명, 피라미드 계산, 캐시 회수, 호스트 조립의 변경 이유를 분리 |
| OCP | 기존 IRasterDecoder/IImageLoader 변형으로 연결; ROI/Core에 형식 분기 없음 |
| LSP | 순차 행·취소·독립 reader·중복 Dispose·오류·픽셀 동등성 검증; 미지원 변형을 명시 |
| ISP | 파일 행, 타일, 프레임 계약을 유지하고 live에 디코더 메서드를 강제하지 않음 |
| DIP | Canvas/검사가 중립 영상 계약을 사용하고 호스트만 구현 선택 |
| Information Expert | 픽셀 규격과 단계 수는 ImageInfo, 축소 규칙은 Pyramid, 형식 정보는 각 코덱에 배치 |
| Creator | native handle/PNG scratch는 코덱, raw/source lease는 피라미드 소유자가 생성·해제 |
| Controller | UI 업무 흐름 유지; 선택 로더 주입만 변경 |
| Low Coupling | Sources/검사에 NetVips 전이 참조 없음; native ABI가 C 어댑터 밖으로 확산되지 않음 |
| High Cohesion | 생성 영상, 파일 디코드, 표시·편집의 책임 분리 |
| Polymorphism | NetVips/Raw의 실제 두 IImageLoader 변형을 공통 소비자가 사용 |
| Pure Fabrication | RawCacheEntry/CacheWriterLock에 기술 수명·공간 정책 배치 |
| Indirection | SourceImageLoader는 실제 두 파일 로더의 생성 영상 분기를 공유하며 픽셀 중계를 추가하지 않음 |
| Protected Variations | 코덱·OS 로딩 차이는 native/file 경계에 격리; ROI/원본 검사 계약 보존 |

설계 원칙 예외를 적용하지 않았다. 파일 준비 개선을 전체60FPS 또는 Linux 검증 완료로 표현하지 않는다.
