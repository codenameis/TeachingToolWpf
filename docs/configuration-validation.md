# ConfigPage·이미지 중심 크로스헤어 검증 — 2026-10-06

`codex/named-drawing-layers`의 후속 로컬 수정이다. 앞선 레이어 구현을 보존하고 [ADR-035](decisions/0035-configuration-and-image-crosshair.md)와 사용자 답변인 **선택 상태와 그룹 관계 구별**을 적용했다. [사용 가이드](configuration-guide.md)를 따른다. 검증 당시에는 커밋·푸시하지 않았으며, 이후 사용자의 `main` 병합 요청에 따라 커밋과 로컬 병합을 진행한다. 푸시는 포함하지 않는다.

## 확인 항목

| 항목 | 상태·증거 |
|---|---|
| 이미지 중심·Canvas 전체 크로스헤어 | PASS: 확대·이동·창 크기·DPI, 이미지 없음, 화면 밖 축. 기존 ScreenCenter API 유지 |
| 단일/다중 선택·역할별 색상 | PASS: 역할별 GPU 상수, 비선택 색 보존, 기존 128byte 상수/ROI 형상/역할 캐시 유지 |
| ConfigPage 입력 | PASS: 역할별 색상/두께 일괄 검증·게시, 오류 시 이전 팔레트와 문서/Undo/Redo 유지, 기본값·동일 적용 no-op |
| 크로스헤어 설정/수명 | PASS: 버튼/뷰모델, 한 레이어 항목 유지, 색/두께 오류 원자성, 구독 해제 후 종료된 Canvas에 게시하지 않음 |
| 전체 Release 빌드 | PASS: 경고0/오류0, `build-final.log` |
| 전체 회귀 | PASS: 1,066개, 실패/건너뜀0. Core234 / 앱251 / Canvas174 / Algorithm248 / Inspection107 / Compute52, `tests-final.log` |
| 최종 컨벤션 | PASS: 전체 `Format-Code.ps1 -Verify`, `format-final.log`. 샘플 마지막 수정의 LF 7곳을 CRLF로 정리 후 통과 |
| RTX 앱 실행 | PASS: `gpu-final/artifacts/smoke.log`, exit0. ConfigPage 입력/재열기·양방향 체크박스·크로스헤어 픽셀/숨김/탐색/재시작, 기존 편집·필터·8K live 회귀 |
| WARP 앱 실행 | PASS: `warp-final/artifacts/smoke.log`, exit0. 같은 시나리오와 픽셀 검사 통과. 별도 저사양 PC 성능 보장과 구별 |
| 독립 예제 | PASS: `sample-final/report.json`, exit0. 기존 Inspection 추가/재제출/개별 삭제, 이미지 중심 가이드, 8K/24K 합성·파일·이력·reader 수명 |

단위 회귀는 이번에 5개 증가했다. 예제의 기존 Inspection 추가는 Add 이후 같은 장면 재제출→개별 Remove에서도 원래 검사 결과 개수가 유지되는지 확인한다. 전체 화면 가이드는 렌더 시 두 선만 추가하며 ROI별 계산이나 전체 화면 텍스처를 추가하지 않는다.

작업 로그는 `artifacts/config-page`, 재검토할 최종 로그·캡처는 [증거 ZIP](data/configuration-20261006.zip)에 보관한다. 초기 `gpu`/`warp` 실행 후 밝은 이름표 글자 대비와 체크박스 검증을 보강했고, 위 최종 `gpu-final`/`warp-final` 실행으로 확인했다.

## 실제 앱 검증

기준 장비 Ryzen 5 5600H / RAM32GB / RTX3060 Laptop6GB, Windows x64. RTX는 VRAM5.9GiB·Budget5.1GiB·DX12 debug True, WARP는 Microsoft Basic Render Driver·debug True로 실행한다. DPI1.5의 실제 HWND 렌더 타깃을 캡처한다.

앱 smoke에서 상단 설정 버튼으로 ConfigPage를 열고 중복 창 방지·재열기 상태를 검사한다. 실제 WPF 입력 바인딩으로 역할별 두께와 다중 선택 색을 적용한다. 크로스헤어/픽셀 체크박스와 버튼 상태의 양방향 연결도 확인한다. 이름표·선택 묶음·설정 창을 캡처해 시각적으로 확인한다.

크로스헤어는 버튼으로 표시한 다음 원본 이미지 중심을 화면으로 계산한다. 그 X/Y에서 캔버스 네 가장자리 근처의 선 픽셀을 검사한다. 확대·이동·창 크기 변경·렌더 재시작 후에도 같은 규칙을 검사하고 숨김 후 해당 픽셀이 사라지는지 확인한다. 가시창의 물리 마우스/모니터 scan-out 시험과는 구별한다.

## 설계 검토

| 원칙 | 판단 |
|---|---|
| SRP | 입력 초안/검증은 표시 설정, 좌표·그리기는 Canvas, UI 수명 연결은 CrosshairLayerBinding |
| OCP | 기존 팔레트 생성자에 선택 인수를 추가하고 별도 ImageCrosshairGraphic을 추가. 기존 ScreenCenter 보존 |
| LSP | 기존 팔레트는 다중 선택 색 미지정 시 단일 선택 색 사용. GPU/WARP의 같은 표시 계약 검증 |
| ISP | 기존 IViewportSurface 구현자에게 새 필수 설정 멤버를 강제하지 않음 |
| DIP | Core와 검사에 WPF/표시 설정 참조 추가 없음. 호스트 조립 지점에서 바인딩 연결 |
| Information Expert | 이미지 중심 변환은 현재 이미지/viewport를 가진 표시기, 역할 판정은 기존 RoiStyleRoles |
| Creator | 설정은 앱, 레이어는 Canvas, native 자원은 기존 렌더러가 소유 |
| Controller | 버튼·페이지는 설정 명령을 호출. ROI 계층/편집 규칙은 변경하지 않음 |
| Low Coupling | ROI 문서에 설정/검사 결과를 넣지 않고 불변 팔레트와 그래픽만 게시 |
| High Cohesion | 설정 페이지에 표시 옵션을 모으고 개별 검사 레시피 편집은 기존 패널에 유지 |
| Polymorphism | 기존 그래픽 모델의 검증/표시 확장, WARP 동일 경로 사용 |
| Pure Fabrication | 가이드 바인딩이 UI 구독 수명만 소유. 별도 범용 설정 프레임워크 없음 |
| Indirection | MainWindow의 조립과 작은 바인딩으로 설정 모델/GPU 소유권 연결 |
| Protected Variations | GPU 상수·셰이더 메모리 배치·ROI 캐시를 바꾸지 않고 스타일을 독립 변경 |

입력 오류를 무시하거나 분석기를 억제하지 않았다. 새 종속성·원칙 예외 없음. 기존 선택형 분석기 감사 미완료는 [이전 품질 기록](release-quality-review.md)을 따른다.

## 남은 범위

실제 사용자 문서·물리 입력/표시·별도 저사양 PC·장시간 사용은 NOT_RUN이다. 설정 파일 저장·복원은 구현 범위가 아니며 UI에 실행 중 설정임을 표시한다. 기존 대량 편집의 60FPS와 별도 검사 CPU125/CUDA80ms FAIL은 유지한다. 이번 기능 회귀·두 선 추가·GPU 상수 재사용을 새 성능 수용 결과로 해석하지 않는다.

## 재현 명령

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
dotnet test TeachingToolWpf.slnx -c Release --no-build --no-restore
pwsh -NoProfile -File scripts/Format-Code.ps1 -Verify
# 각 앱 실행은 새 작업 폴더에서 수행한다. 해당 폴더의 artifacts에 캡처/로그가 남는다.
TeachingToolWpf.exe --smoke --gpu-debug
TeachingToolWpf.exe --smoke --gpu-debug --software-rendering
dotnet run --project samples/VisionCanvas.Sample -c Release --no-build -- --smoke --gpu-debug --output artifacts/config-sample
```
