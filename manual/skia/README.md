# Skia 코드 교육서

[index.html](index.html)을 열어 읽는다. 변수·반복문·메서드·클래스를 배운 고등학생을 대상으로 공통 ROI·입력·타일, Skia CPU raster, bitmap과 HWND 복사, Gray16 표시 변환, 반복 ROI 경로·버퍼 재사용을 설명한다.

실제 코드 발췌, 숫자 계산, 교육용 조작 모형, 실습과 풀이를 포함한다. 라이브러리는 `src/VisionCanvas.Skia`, 샘플은 `samples/VisionCanvas.SkiaSample`다. 현재 구현은 GPU Skia가 아니며, 기존 CPU 그리기 측정을 앱의 지속 60FPS로 표현하지 않는다.

오프라인 열람에는 `manual` 폴더 전체가 필요하다. [검증·관리](../../docs/renderer-code-guides.md), [표시 검증](../../docs/skia-canvas-validation.md), [성능 기록](../../docs/skia-canvas-performance.md).

첫 읽기에서는 앞부분의 일곱 단어와 1~7장의 상황·작은 계산·조작 모형을 따라간다. 8~12장은 실제 구현과 자원 관리의 심화 읽기다. 비유 다음에 실제 클래스와 메서드를 연결하며 코드·수식·검증 한계는 유지한다.
