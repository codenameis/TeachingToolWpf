# 검색 범위 ROI와 그룹 이름

## 사용법

1. ROI 하나를 선택하고 오른쪽 속성에서 **ROI 타입 → 검색 범위 ROI**를 선택한다.
2. **가로 양쪽 여유(px)**, **세로 양쪽 여유(px)**를 입력하고 **변경 적용**을 누른다. 기본값은20px이다. 너비100px ROI에 가로 여유20px를 주면 검색 너비는140px이다.
3. 원래 ROI 바깥의 검색 사각형이 빨간 점선으로 표시된다. 원·삼각형·폴리곤도 사용할 수 있으며 원래 윤곽은 유지된다. 이동·회전 시 검색 경계도 따라간다.
4. **일반 ROI**로 바꾸고 적용하면 검색 경계가 제거된다. 변경 적용 한 번은 Undo/Redo 한 번이다.

패턴 학습은 원래 ROI를 사용한다. 패턴/일반 도형의 **선택 영역 검사**를 켜면 검색 범위 ROI의 확장 사각형에서 검사한다. 전체 이미지 검사는 기존과 같다. 이미지 밖까지 확장한 검색 범위는 검사 서비스의 기존 범위 검증에 걸릴 수 있다. 자동으로 여유값을 줄이지 않는다.

자식이 있는 부모와 그룹화로 만든 부모는 **그룹 전용**이다. 이 부모를 선택해 학습하거나 선택 영역 검사할 수 없다. 오른쪽 **이름**에서 표시 이름을 입력하고 **캔버스에 이름 표시(Visible)**로 이름표를 켜고 끈 뒤 변경 적용한다. 윤곽/핸들/트리와 자식 이름은 그대로이며, 이름 숨김은 선택 중에도 적용한다. 이름과 표시 설정은 복사·Undo/Redo에 포함된다.

여유값은 원본 이미지 픽셀이며 ROI 로컬 축을 따른다. 크기 변경/붙여넣기 축소에서도 입력된 여유값을 유지한다. 좌우/상하의 서로 다른 여유와 마우스 검색 경계 조절은 제공하지 않는다. 부모 전환으로 휴면 상태가 된 검색 설정은 실제 자식이 없어져 일반 ROI가 될 때 돌아온다. `IsGroup` 부모는 계속 그룹이다.

## 검증 기록 — 2026-10-06

- PASS: Release 빌드, 새 검색 타입의 파싱·실패 원자성·Undo/Redo·복사·이동/회전 preview·4종 ROI 형상·부모 전환·검색 경계만의 공간 조회.
- PASS: 패턴3방식/일반 도형의 선택 검색 범위, 실제 학습 형상 유지, 그룹 부모 학습/선택 검사 차단, 결과 무효화와 모델 보존.
- PASS: RTX3060 Laptop GPU, DX12 debug 켬. 실제 WPF ComboBox/숫자 입력/이름/Visible 바인딩과 Undo/Redo, 캡처 픽셀의 빨간 대시/공백 및 일반 타입에서 제거 확인. 전체 `--smoke` 종료0.
- PASS: Microsoft Basic Render Driver의 CPU(WARP), DX12 debug 켬. 같은 WPF/픽셀/이력 시나리오와 전체 `--smoke` 종료0. 저사양 PC의 성능 보장은 아니다.
- PASS: 전체6개 테스트 프로젝트1,086개(Core246, 앱257, Canvas176, Algorithm248, Inspection107, Compute52). 첫 실행의 새 폴리곤 테스트 중심 기대값과 기존 목록 역할 문자열 기대값을 수정 후 해당 프로젝트를 재검증했다.
- PASS: `pwsh -File scripts/Format-Code.ps1 -Verify`, `git diff --check`. Microsoft C# 명명/괄호/줄바꿈 규칙 유지.
- NOT_RUN: 물리 마우스를 이용한 수동 점검, 이번 기능을 포함한 수만 검색 ROI의60FPS 실측. 기존 성능 목표 미달 기록을 대체하지 않는다.

증거 경로: `artifacts/roi-search-gpu-20261006/artifacts/smoke.log` 및 같은 폴더의 `roi-search-dashed.png`, `roi-search-standard.png`, `roi-search-undo.png`. WARP는 `artifacts/roi-search-warp-20261006/artifacts`에 같은 이름으로 보관한다. 구현 결정은 [ADR-036](decisions/0036-roi-search-range-and-group-label.md)을 따른다.
