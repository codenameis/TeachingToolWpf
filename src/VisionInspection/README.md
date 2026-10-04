# VisionInspection

`net10.0`에서 학습·원본 이미지 검사·타일 검색·결과 통합을 담당한다. `Core`의 이미지/ROI 값, `VisionImaging`의 원본 입력, `VisionAlgorithm`의 알고리즘을 연결한다. WPF·Canvas·DX12를 참조하지 않으며 기존 namespace `TeachingToolWpf.Imaging.Matching`을 유지한다.

- 형상·OpenCV 템플릿·일반 도형 서비스는 원본 영상과 검색 조건을 받아 검출 결과를 반환한다.
- `MatchingImageReader`는 검사별 독립 reader/frame lease와 16 MiB 원본 Gray8 타일 캐시를 소유한다. 화면 표시 LOD를 검사 입력으로 사용하지 않는다.
- GPU 전처리는 호스트가 `IImagePreprocessor` factory로 주입한다. GPU가 선택됐지만 구현이 등록되지 않았으면 명시적으로 실패한다. 이 라이브러리는 GPU 공급자를 직접 생성하거나 CPU로 조용히 대체하지 않는다.
- 화면 상태·원자적 편집 이력·판정색·오버레이·사용자 파라미터 바인딩은 앱이 담당한다. 검사 도중 입력이 바뀐 결과의 게시 여부도 앱이 결정한다.

네이티브 OpenCV/libvips 런타임은 실행 호스트의 운영체제에 맞게 배포하고 별도로 검증한다. `net10.0` 빌드만으로 Linux 실행 검증 완료를 주장하지 않는다.
