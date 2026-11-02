# Skia Canvas 구현·검증

2026-10-16, Windows x64 / .NET 10 / SkiaSharp3.119.2 CPU raster. [ADR-067](decisions/0067-skia-canvas-and-sample.md), [실행 안내](../samples/VisionCanvas.SkiaSample/README.md).

## 결과

| 항목 | 결과 | 범위 |
|---|---|---|
| 독립 솔루션/앱 Release 빌드 | PASS | 경고0/오류0 |
| Skia 테스트 | PASS 28건 + 계측1건 | 실제 WPF 창·픽셀·원본 파일·1억 ROI·튜닝 전후 비교 |
| 공통 편집 회귀 | PASS 396건 | 기존 TeachingToolWpf.Core.Tests |
| Gray8/Gray16/RGBA/BGRA | PASS | 채널·alpha·원본 불변·표시 버퍼 소유권 |
| 도형·레이어·마스크·필터 표시 | PASS | HWND 캡처·지정 픽셀·회전 후 Undo 화면 복원 |
| 샘플 UI | PASS | 바인딩/명령 연결·8K·그리기·그룹·복붙·연쇄 삭제·Undo/Redo·새 편집의 Redo 제거 |
| 실패/취소 | PASS | 실패한 파일 교체의 문서/세대/이력 유지·드래그 취소의 이력 보존 |
| 8K 직접 프레임 | PASS | 7680×4320 Gray8/Gray16·재시작·최소화/복원·resize |
| 반복 문서 | PASS | 추가 버튼·200,040,003개 문서·개별 드래그/회전/삭제·복붙·Undo/Redo·555/556px 경계 |
| ken32 | PASS | 17,280×30,720 파일 전체 열기·fit·원본1:1 타일 |

ken32 새 Skia 전용 애플리케이션 캐시 준비 포함 로딩은7,605.795ms, fit 캡처 완료까지7,697.970ms였다. 2026-10-16 20:01 KST 한 번이며 OS 캐시는 초기화하지 않았다. PNG 저장과20ms polling을 포함한다. 버튼→물리 scan-out·냉간 부팅·반복 성능 개선으로 해석하지 않는다. 원본 파일과 raw 캐시는 Git에 추가하지 않는다.

현재 Skia CPU 렌더러다. GPU 시간은 없고 공통 `TextureBytes`는 표시 bitmap의 바이트다. GDI 복사와 DWM/모니터 표시 완료는 다르다. GPU 가속·실제 카메라 FPS·엄격한60FPS·혼합 DPI 모니터 이동·장시간 메모리 안정성은 NOT_RUN이다. Gray16 표시 변환·안티앨리어싱·텍스트 차이로 DX12와 픽셀 전체 동일성도 주장하지 않는다.

후속 [1억 ROI·성능 검증](skia-canvas-performance.md)에서 같은 프로세스 전후 비교의 합성 CPU 그리기는54.78→44.74ms였다. 반복 윤곽 픽셀은 같고 불투명 컬러 보간은 최대1/255 차이다. 조밀한 합성 장면은16.67ms 예산을 초과하므로 이 조건의60FPS 목표는 FAIL이며, 실제 앱의 지속 FPS 자체는 미측정이다. ken32 재검증은 기존 애플리케이션 캐시를 재사용해 첫 준비7.6초와 비교하지 않는다.

## 재현

```powershell
dotnet build VisionCanvas.Skia.slnx -c Release
dotnet test tests/VisionCanvas.Skia.Tests -c Release
dotnet test tests/TeachingToolWpf.Core.Tests -c Release
$env:VISIONCANVAS_SKIA_KEN32 = (Join-Path (Get-Location) 'ken32.png')
dotnet test tests/VisionCanvas.Skia.Tests -c Release
```

ken32 환경 변수가 없으면 원본 파일 시험을, `VISIONCANVAS_SKIA_PERF`가 없으면 선택형 계측을 skip한다. 후속 증거는 `artifacts/skia-validation`의 `skia-repeated-final.trx`(28통과/계측1skip), `core-repeated-final.trx`(396통과), 별도 실행한 `performance-final-2.json`, PNG와 `ken32/result.json`이다. 테스트 창은 실제 생성·렌더하고 종료한다. 샘플에는 진단 코드를 넣지 않았다.

## 코드 규칙

새 파일에 명명·Allman/4칸·멤버 간격·괄호·using 순서·명시적 자원 수명·MVVM을 적용했다. 새3개 프로젝트의 공백·스타일·명명 검사는 모두 PASS다. 로그는 `artifacts/skia-validation`의 `renderer/sample/tests-style.log`와 각각의 `-whitespace.log`다. 저장소 전체 `scripts/Format-Code.ps1 -Verify`는 기존 파일의 형식 위반으로 실패한다. 무관한 파일을 일괄 수정하지 않았으며 원문은 `artifacts/skia-validation/repository-format.log`다.

튜닝·정리 후에도 세 프로젝트의 공백·지정 스타일·명명 검사를 다시 통과했다. 최종 Release 빌드 경고0/오류0은 `repeated-build-final.log`, 저장소 전체 형식 재검사의 기존 위반은 `repository-format-repeated.log`에 남겼다. 이 전체 검사에는 Skia 프로젝트 위반이 없으며, 다른 검사 프로젝트 등의 기존 줄바꿈 위반을 이번 작업에 섞어 수정하지 않았다.

로컬 수정·검증만 수행했으며 커밋·푸시는 하지 않았다.
