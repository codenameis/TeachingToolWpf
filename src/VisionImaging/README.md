# VisionImaging

net10.0의 기본 파일 로더다. TIFF/BigTIFF·PNG·JPEG·정적 WebP를 자체 스트리밍 코덱과 C# 디스크 피라미드로 읽는다. WPF·DX12·Canvas·검사 알고리즘을 참조하지 않는다.

- ImageLoader: IImageLoader 진입점과 합성 소스 연결.
- DiskPyramidSource: 기존 공개 API를 유지하며 RawPyramidSource/NativeRasterDecoder로 위임.
- ImageFileVersion: 영상 계약 프로젝트가 파일 버전을 계산한다.
- VisionImaging.Sources: 카메라/합성 소스의 별도 구현으로 파일 코덱에 의존하지 않는다.

NetVips/libvips 패키지 참조는 제거했다. native 파일은 VisionImageCodecs.dll(Windows) 또는 libVisionImageCodecs.so(Linux)이며 해당 OS에서 Build-Native.ps1로 준비한다. 자세한 경계·제약·라이선스·검증은 [전환 기록](../../docs/netvips-removal.md)을 따른다. Linux 실행은 미검증이다.
