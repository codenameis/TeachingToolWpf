# ADR-039: 재사용 WPF 필터 컨트롤과 연속 미리보기

2026-10-06 사용자 요청: MVVM 기반 별도 WPF 컨트롤 프로젝트, 필터 파라미터 입력, 토글과 이동 중 실시간 미리보기. `main`에서 로컬 변경만 한다.

## 결정

`VisionCanvas.Filters.Wpf`가 FilterPanel, ParameterSlider와 FilterPreviewViewModel을 소유한다. TeachingToolWpf에 역참조하지 않으며 Canvas의 기존 편집/영상 처리 계약을 사용한다. Core와 계산 엔진에 WPF 의존성을 추가하지 않는다. 앱은 processor/장치 생성, 세션 상태 전달과 viewport 게시를 조립한다.

파라미터의 숫자 검증은 기존 FilterSettings 계약을 그대로 사용한다. 슬라이더 gesture는 임시 FilterRequest.OverrideSettings로 처리하고 종료 시 EditorSession.SetFilters를 한 번 호출한다. ROI 문서·이력에 미리보기 픽셀이나 중간 슬라이더 값을 저장하지 않는다. 다른 선택 또는 문서 설정이 들어오면 이전 gesture의 적용 권한을 폐기한다.

토글 상태는 ViewModel에 보관하며 OFF 이후 재선택으로 자동 해제하지 않는다. 기존 자동 표시 규칙은 ON 상태에 한해 유지한다. 입력이 계속 들어와도 실행 중 요청은 완료하고, 대기 하나를 최신으로 교체한다. 완료된 위치의 결과를 유지하면서 다음 결과를 기다린다. 다른 이미지·선택·확정 설정과 취소는 세대로 구별하여 늦은 결과를 버린다. 처리 주기와 renderer 60FPS는 별개다.

## SOLID / GRASP 확인

| 원칙 | 이번 경계 |
|---|---|
| SRP | View의 입력 gesture, VM의 설정/요청/진단, Core 이력, processor 계산을 분리 |
| OCP | 기존 IRoiFilterPreview로 실제 엔진과 독립 시험 processor를 교체 |
| LSP | 기본 설정과 임시 설정 모두 검증·취소·읽기 전용 원본 계약 유지 |
| ISP | 컨트롤은 사용하는 파라미터/명령만 바인딩; GPU 수명 메서드 강제 없음 |
| DIP | WPF 프로젝트가 앱·CUDA 구현을 참조하지 않음 |
| Information Expert | 수치 유효성은 FilterSettings, 이력 확정은 EditorSession |
| Creator | 호스트가 VM/processor/backend 소유; VM이 임시 요청 소유 |
| Controller | View는 command만 호출; ROI 변경은 VM이 EditorSession에 전달 |
| Low Coupling | 호스트 리소스·DataContext 자동 생성에 의존하지 않는 컨트롤 |
| High Cohesion | 파라미터 gesture는 VM의 Parameters 부분, 요청 pump는 기존 VM에 응집 |
| Polymorphism | IRoiFilterPreview와 ICommand의 실제 변형 지점 재사용 |
| Pure Fabrication | UI 임시 상태가 ROI 도메인/장치 객체로 유입되지 않음 |
| Indirection | FilterRequest.OverrideSettings가 이력 변경 없이 미리보기 값을 전달 |
| Protected Variations | 장치/영상 입력 변화는 기존 processor 경계에 격리 |

원칙 완화 없음. 새로운 필터 알고리즘/사용자 정의 convolution/자유 pipeline 편집기를 이번 변경으로 주장하지 않는다. [실제 검증](../filter-controls.md)을 따른다.
