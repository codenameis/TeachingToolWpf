# VisionImaging

`net10.0`에서 원본 이미지 파일, 타일, 합성 프레임 공급을 담당한다. WPF·DX12·검사 알고리즘을 참조하지 않는다. 기존 호출부의 타입 이름을 보존하기 위해 namespace는 `TeachingToolWpf.Imaging`을 유지한다.

- `IImageLoader`/`ImageLoader`: 원본 파일 버전 확인과 독립 `ITileSource` 생성.
- `DiskPyramidSource`: libvips를 통한 파일 읽기와 디스크 피라미드. 검사는 level 0을 읽고 별도 검사 축소 정책을 적용한다.
- `LiveImageSource`/`SyntheticFrameSource`/`ProceduralTileSource`: 원본 입력과 테스트 샘플 공급. 렌더 프레임 선택·표시 해상도 준비는 `VisionCanvas`에 남긴다.

이 프로젝트는 NetVips 관리 패키지만 참조한다. libvips 네이티브 라이브러리와 운영체제별 배포 설정은 실행 호스트가 제공한다. 대상 프레임워크의 플랫폼 독립성은 특정 Linux 환경에서 네이티브 파일 디코드를 검증했다는 뜻이 아니다.
