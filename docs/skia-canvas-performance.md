# Skia 반복 ROI·CPU 렌더 튜닝

2026-10-16. [ADR-067](decisions/0067-skia-canvas-and-sample.md), [앱 사용법](../samples/VisionCanvas.SkiaSample/README.md). 사용자 요청 범위는 1억 개 이상 반복 ROI, 측정 기반 튜닝, 성능을 유지하는 쉬운 코드 흐름이다. 로컬에서만 수정·검증한다.

## 앱에서 사용하는 방법

`8K 예제` 또는 파일 열기 → `1억 ROI 추가`. 10,001×10,001 = **100,020,001개**를 기존 문서에 한 번의 명령으로 추가한다. 버튼을 다시 누르거나 복사·붙여넣기로 더 늘릴 수 있다. 가로·세로 556px 미만은 상태 메시지로 거부하며 문서와 이력은 바뀌지 않는다.

반복 배치는 행·열 범위와 개별 수정값을 압축 저장한다. 축소 상태의 요약 표시와 확대 상태의 개별 셀 표시를 구별한다. 임의 도형 객체 1억 개나 전수 윤곽을 한 화면에 펼쳤다는 의미가 아니다. 기존 Core의 포함 판정·표시 단계·개별 셀 ID·부분 선택·이력은 변경하지 않았다.

실제 WPF 테스트에서 일반 ROI 1개를 유지하며 100,020,001개를 추가하고, 중앙 셀 드래그·회전·삭제·Undo/Redo, 전체 복사·붙여넣기, 추가 명령 뒤 Redo 제거를 확인했다. 마지막 문서는 **200,040,003개**다. 첫 추가 명령은 한 실행에서18.354ms, 첫 배치의 Core 추정 보관량은1,416바이트였다. 이는 화면 표시 시간이나 프로세스 전체 메모리가 아니다.

## 측정 조건

- Windows x64, .NET 10 Release, SkiaSharp3.119.2 CPU raster. 기준 장비는 Ryzen5 5600H/32GB/RTX3060 Laptop이며 이번 렌더 계산에 GPU를 사용하지 않는다.
- 출력1200×800, 반복 격자100,020,001셀, pitch=`8192 × .45 / 10001`, 중앙 좌표4096.
- 전후 구현을 **같은 프로세스에서 교대 순서**로 실행. 장면별8회 워밍업,48회 측정,24개의 동일 pan 위치. 선 두께·안티앨리어싱·요약 간격·보이는 셀 범위를 유지한다.
- 이미지 비교는1024×1024 합성 Gray8 또는 불투명 BGRA를 출력 사각형으로 확대한다. 합성 장면은 Gray8 이미지와 반복 윤곽을 함께 그린다.
- CPU 그리기와 `Flush`만 잰다. 화면 지우기·픽셀 대조·문서 준비·파일 읽기·타일 업로드·라벨·GDI Present·DWM/모니터 scan-out은 제외한다. 실제 앱의 지속 FPS가 아니다.
- 튜닝 전 구현은 테스트 프로젝트의 `ReferenceRepeatedRois`/`ReferenceSprites`에 고정했다. 제품은 이를 참조하지 않는다. 테스트의 비교용 픽셀 배열도 제품 프레임 할당에 포함되지 않는다.

## 전후 결과

코드 정리 후 첫 재측정 `artifacts/skia-validation/performance-final-1.json`의 중앙값(ms):

| 장면 | 전 | 후 | 단축 |
|---|---:|---:|---:|
| 전체 보기, scale .09 | 1.10 | 1.05 | 편차 범위 |
| 요약, scale7 | 4.61 | 3.85 | 16% |
| 개별 윤곽, scale9 | 38.29 | 30.42 | 21% |
| 17° 회전 개별 윤곽, scale9 | 59.35 | 39.18 | 34% |
| 확대, scale32 | 3.24 | 2.64 | 19% |
| 17° 회전 확대, scale32 | 5.58 | 3.92 | 30% |
| Gray8 이미지 | 16.52 | 13.83 | 16% |
| Gray8 이미지 + scale9 윤곽 | 55.31 | 45.33 | 18% |

별도 paired1/paired2 실행의 개별 윤곽 단축은19%, 회전 윤곽은32~34%였다. 전체 보기의 작은 차이는 개선으로 주장하지 않는다. 실행 간 절대 시간 편차가 있어 서로 다른 실행의 최솟값끼리 비교하지 않는다.

최종 두 번째 실행(`performance-final-2.json`)도 개별 윤곽38.50→31.41ms, 회전 윤곽67.17→43.01ms, Gray8 이미지16.26→13.88ms, 합성 장면54.78→44.74ms였다. 불투명 BGRA 이미지도12.04→8.80ms(27%)로 단축됐다. 합성 장면의 p95는60.52→51.12ms다. 반복 윤곽·Gray8 비교의 픽셀 차이는0, BGRA48프레임의 최대 채널 차이는1이었다. 비교용 두 픽셀 배열은 재사용하고 복사·차이 계산 시간은 계측에서 제외했다.

합성 이미지+조밀한 윤곽은 개선 후에도16.67ms를 넘는다. **엄격한60FPS 달성은 FAIL**, GPU 가속·실제 카메라 FPS·장시간 메모리 안정성은 NOT_RUN이다.

## 유지한 변경과 제외한 실험

| 실험 | 판단 | 근거 |
|---|---|---|
| 셀별 MoveTo/LineTo/Close5회 → AddPoly1회 | 유지 | 단독 paired 실험에서 조밀/회전 윤곽9~14% 단축, 전후 픽셀 동일 |
| 배치의 `grid.Pose` 한 번 계산 | 유지 | AddPoly와 함께 회전 윤곽32~34% 단축. 각 모서리의 삼각함수 반복 제거, 계산 순서·double 좌표 유지 |
| bitmap shader로 불투명 사각 이미지 그리기 | 유지 | Gray8 paired 실험14~18% 단축. 동일 Clamp·UV·선형/최근접 설정 |
| 직접 `DrawImage` 사용 | 제외 | 부분 픽셀 위치의1:1 부근에서 최대130단계 채널 차이. 속도만으로 채택하지 않음 |
| 반투명 이미지도 사각형 경로로 통합 | 제외 | alpha 결합에서2단계 차이. 기존 삼각형 경로 유지 |

반복 윤곽은 성능 비교 장면과 부분 선택·개별 수정·DPI1/1.5·회전 회귀에서 픽셀 전체가 동일하다. 불투명 이미지의 float 보간 경로는 일부 채널에 최대1/255 반올림 차이가 있다. 이미지 비교의 허용 기준은 채널1단계이며 원본 픽셀·Gray16 원본에는 변경이 없다. 픽셀 전체 동일성 주장은 반복 윤곽에 한정한다.

## 가독성과 검증

`SkiaRepeatedRois`는 `DrawGrid → GetDrawingBlocks → GetVisibleCells → BuildPath`로 읽는다. `SkiaSprites`는 사각형 빠른 경로의 조건과 기존 삼각형 경로를 나눈다. VM의 `PasteAsync`는 준비·취소·확정 순서, worker의 `UpdateRetainedFrame`은 프레임 교체·해제 순서를 드러낸다. 코너 배열·path·paint 재사용과 App/VM/View/Core 책임을 유지한다. 범용 추상화·새 의존성·매 프레임 전체 픽셀 변환은 추가하지 않았다.

```powershell
dotnet build VisionCanvas.Skia.slnx -c Release
$env:VISIONCANVAS_SKIA_KEN32 = (Join-Path (Get-Location) 'ken32.png')
dotnet test tests/VisionCanvas.Skia.Tests -c Release
$env:VISIONCANVAS_SKIA_PERF = 'local-comparison'
dotnet test tests/VisionCanvas.Skia.Tests -c Release --filter FullyQualifiedName~PerformanceTests
dotnet test tests/TeachingToolWpf.Core.Tests -c Release
```

성능 환경 변수는 출력 이름이며 미지정 시 계측 한 건만 skip한다. ken32는 기존 앱 캐시를 사용하는 재검증이며 초기 디코딩 성능 개선으로 해석하지 않는다. PNG 캡처와 로그는 `artifacts/skia-validation`에 남긴다.

공식 [SKCanvas](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcanvas?view=skiasharp-3.119)와 [SkBitmap shader 계약](https://api.skia.org/classSkBitmap.html), 고정3.119.2 assembly의 실제 overload를 확인했다.
