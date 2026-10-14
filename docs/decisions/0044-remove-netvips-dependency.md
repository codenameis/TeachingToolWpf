# ADR-044: 기본 파일 로더와 NetVips 의존성 제거

- 상태: 사용자 승인 구현, 2026-10-14
- 범위: 이전 ADR-032/043의 NetVips 유지·선택형 자체 경로를 대체. 로컬 수정·검증만 진행.
- 요청: NetVips를 제거하고 응용 소스 공개 의무가 없는 코덱 의존성만 유지.

기존 검증한 NativeRasterDecoder와 RawPyramidSource를 기본 ImageLoader에 연결한다. DiskPyramidSource의 공개 API는 위임으로 유지한다. 앱·샘플·검사 CLI의 조립 경계, Canvas/Core의 IImageLoader/ITileSource 계약과 원본 프레임 소유권은 보존한다. 폴더 이동·새 플러그인 계층은 추가하지 않는다.

기본 파일 런타임 외부 구성은 LibTIFF/libpng/libjpeg-turbo/zlib/libwebp다. 검토한 고정 라이선스는 소스 공개/재링크용 오브젝트 제공을 요구하지 않으며 배포 고지를 유지한다. OpenCV 필터·검사와 ILGPU는 별도 기능이다. Skia는 명시적 비교 빌드만 유지한다.

후속 사용자 답변으로 정적 WebP도 유지한다. TIFF/BigTIFF·PNG·JPEG·정적 WebP를 지원한다. 애니메이션 WebP·BMP·VIPS 및 코덱 미지원 TIFF 변형은 실패한다. 이전 NetVips보다 좁은 범위를 숨기거나 다른 decoder fallback으로 우회하지 않는다. Linux 실제 실행·현장 corpus·60FPS 목표는 별도 검증이다.

테스트 입력 생성기는 제품 밖의 도구 프로젝트에서 기존 OpenCV와 무압축 TIFF 작성기를 사용한다. 테스트 판정을 약하게 하지 않고 원본 픽셀·알파 태그를 보존한다. 이전 인코딩과 새 fixture의 바이트 동일성은 가정하지 않는다. NetVips 측정 기록/캐시/옛 ZIP은 보존하고 새 도구에서 과거 이름으로 자체 엔진을 측정하지 않는다.

## 설계 원칙 검토

| 원칙 | 이번 적용 |
|---|---|
| SRP | 디코드·피라미드·호스트 조립·fixture 작성 책임 분리 |
| OCP | 기존 IRasterDecoder 계약으로 연결, Core 형식 분기 없음 |
| LSP | 원본 값·취소·수명·실패 원자성 회귀. 지원 형식 축소는 명시적 호스트 정책 변경 |
| ISP | 기존 IImageLoader/ITileSource 그대로, 새 소비자 메서드 없음 |
| DIP | Canvas는 계약만 참조, 호스트가 파일 구현 연결 |
| Information Expert | 형식 해석은 코덱, 단계/타일은 피라미드에 유지 |
| Creator | decoder reader/타일 버퍼의 기존 소유자 유지 |
| Controller | UI는 기존 이미지 교체 요청에 위임 |
| Low Coupling | NetVips/native 패키지 연결 제거, 검사·렌더로 코덱 타입 전파 없음 |
| High Cohesion | fixture는 제품 외부 도구, 로더와 생성기 수명 분리 |
| Polymorphism | Native/Skia는 기존 디코더 계약 사용 |
| Pure Fabrication | 기존 캐시/원본 reader 서비스 보존, 새 범용 서비스 없음 |
| Indirection | DiskPyramidSource는 기존 API 호환 위임, 중간 픽셀 복사 없음 |
| Protected Variations | 외부 C ABI/코덱 변경을 영상 계약 뒤에 유지 |

원칙 예외 없음. 실제 검증과 배포는 [전환 기록](../netvips-removal.md)을 따른다.

WebP 후속도 위14원칙을 유지한다. native adapter에서만 타입/메모리/해제를 처리하고 공개 IRasterDecoder/ITileSource를 변경하지 않는다. 전체 RGBA 디코드의 공간 예산과 취소 지연을 명시하며 원칙 예외는 없다. 사용자 승인한 Vips 전용 두 파일은 삭제했다.
