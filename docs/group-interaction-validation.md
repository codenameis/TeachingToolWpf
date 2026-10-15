# 복수 선택·벽 슬라이딩·4만 ROI 입력 검증

2026-10-15, main에서 로컬 수정·검증. 커밋·푸시하지 않았다. 기존 미커밋 변경과 `dotnet-anti-slop/`를 보존했다. [동작 결정](decisions/0049-roi-sliding-and-group-interaction.md).

## 바뀐 동작

- 주황색 복수 선택의 회전 핸들·좌우 회전·각도 초기화를 제거했다. 단일 ROI와 실제 그룹 하나의 회전은 유지한다. 다중 이동/복사/삭제와 Core의 프로그램식 복수 변환은 유지한다.
- 부모 벽에서 남은 이동의 접선 성분을 사용한다. 회전된 사각형·원·오목 다각형과 여러 부모를 함께 검사하고, Shift 축 제한과 금지 구간을 건너뛰지 않는 연속 이동을 유지한다. 포인터가 이미지 밖에 가도 유효한 접선 방향 입력을 비례 축소하지 않는다.
- 동일 위치·Shift 상태의 Mouse Up은 접촉 계산을 반복하지 않아 미리보기와 확정이 정확히 같다. 이는 비교 허용 오차를 늘린 것이 아니다.
- 대량 문서에서 단일 선택은 즉시 게시한다. 그룹 자손의 변환 준비는 실제 드래그 때 기존 worker가 담당한다. 큰 단일 그룹의 이동·회전은 기존 공간 색인 배치의 경계를 갱신하여 전체 재정렬을 줄인다.

## 측정 조건과 판정

RTX3060 Laptop GPU, DX12 하드웨어, 5.9GiB VRAM/측정 시 예산5.1GiB, GPU debug OFF. 8192×8192 합성 이미지 위에 일반 ROI40,000개와 루트·모듈·ROI 그룹3개를 둔다. 압축 반복 셀 시험과 다르다. 각 프로세스에서 최초1회와 준비 후3회를 기록한다.

생산 `InteractionController`·ViewModel·편집 worker·DX12 렌더러를 사용한다. 별도120Hz 입력 생성 스레드가 UI에 한 번에 하나씩 전달하며 늦어진 입력 번호를 건너뛰고 시간을 기록한다. 이동2초, 확정 중 탐색 최소0.7초, 회전2초, 확정 중 탐색 최소0.7초, 더블클릭과 탐색2초가 한 반복이다. 더블클릭은 약125ms마다 수행하며 최종 실행은 **48회 모두 기대한 계층 선택**, 모든 이동/회전에서 전체 ROI 값과 선택의 Undo→Redo→Undo 동등성을 확인한다.

창은 화면 밖에 두고 제어기 입력을 사용하여 사용자 마우스/포커스 경합을 피했다. 실제 HWND 라우팅과 픽셀은 별도의 native smoke로 검증한다. 이 성능 결과는 물리 입력→모니터 표시 지연이나 현장 영상/다른PC 보장이 아니다. 진단용 고해상도 대기는 [CreateWaitableTimerExW](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw)와 [SetWaitableTimer](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimer)의 상대100ns 단위를 따른다. 생산 타이머·프레임 예산은 바꾸지 않았다.

프레임 FPS는 연속 제출 기록의 간격이며 입력 콜백/선택 완료/편집 완료와 구별한다. 활성 구간 첫 프레임의 이전 간격은 정적 화면 시간을 포함할 수 있어 따로 기록한다. 첫 프레임·첫 preview 지연과 최초 실행을 결과 JSON에 보존한다. DXGI 카운터 기반 표시 주기 추정도 별도 필드이며 엄격한 제출 간격 판정을 대체하지 않는다. **평균60FPS 이상 및 모든 활성 프레임16.6667ms 이하가 수용 기준이며 이번 결과는 FAIL이다.**

최종3회 측정(`verified`). p95는 반복별 p95 중 최댓값, 최대는 전체 반복의 최댓값이다.

| 활성 구간 | FPS 범위 | 프레임 p95 / 최대 ms | 입력 콜백 최대 ms | 엄격한60FPS |
|---|---:|---:|---:|---|
| 그룹 이동 | 59.98~60.02 | 17.19 / 18.59 | 1.75 | FAIL |
| 이동 확정 중 탐색 | 59.96~60.00 | 17.37 / 22.41 | 9.80 | FAIL |
| 그룹 회전 | 59.96~60.00 | 18.36 / 19.34 | 1.11 | FAIL |
| 회전 확정 중 탐색 | 59.96~59.99 | 17.37 / 25.61 | 8.61 | FAIL |
| 더블클릭·탐색 | 59.99~60.10 | 17.29 / 19.22 | 6.96 | FAIL |

최종48회 선택 완료 평균 **1.90ms**, p95 **2.25ms**, 최대 **6.80ms**. 이동 확정 중앙값 141.5ms(범위 141.0~163.6ms). 회전 확정 중앙값 149.1ms(범위 144.9~149.8ms).

최초 실행은 별도다. 최초 이동 첫 preview 88.6ms, 최초 구간 전체 최대 프레임 69.7ms였다. 준비 후 수치만으로 최초 입력까지60FPS를 보장하지 않는다.

## 전후 비교와 남은 비용

`before`는 기능 수정 후 최적화 전, `after-refit`은 공간 색인 배치 재사용만 적용, `selection-before`는 같은 구현의 재측정과 선택 완료 시간 계측 추가, `after`는 즉시 선택 추가, `verified`는 최종 입력 경계 수정·계층 선택 정확성 확인을 포함한다. 각 실행의 해시·최초 실행·반복별 프레임/입력/GC는 [요약 JSON](group-interaction-results.json)에 보존한다. 원본은 `artifacts/group-input-20261015/<실행>/report.json`, `frames.csv`, `profile.csv`다.

색인 재사용만 적용한 두 실행에서 확정 구간 평균 할당은 이동38.9→30.7MB, 회전38.1→30.7MB였다. 이동 확정 중앙값227.7→214.0/199.5ms, 회전209.7→148.5/163.8ms였다. 시간 편차가 있으므로 일정한 지연 보장으로 표현하지 않는다. 재현되는 할당 감소와 질의 동등성을 근거로 유지했다.

즉시 선택 비교(`selection-before`→`after`)에서48회 선택 완료 평균13.73→1.74ms, 최대34.38→2.12ms였다. 더블클릭·탐색2초 구간의 평균 할당124.6→13.2MB로 감소했다. 콜백은1~2ms 내 동기 작업을 더 수행하지만 자손 전체 준비 대기와 약111MB의 불필요한 할당을 제거했다. 최초 그룹 드래그의 준비 시간은 별도로 남으며 선택 시간을 줄이기 위해 그것을 숨기지 않는다.

## 기능 검증과 실패 기록

- PASS: Core359개. 회전된 벽/원/오목 홈 슬라이딩, 여러 부모 공동 제한, 이미지 밖 포인터, Shift 축 고정, 지연 요청의 마지막 Shift 상태, 제거된 복수 선택 핸들, 실제 그룹 회전·경계·Undo/Redo. `artifacts/group-input-core-verified.log`.
- PASS: 최종 전체 회귀1,231개(Core358·앱279·Canvas235·Algorithm194·Compute52·Inspection113), 실패/건너뜀0. 이후 이미지 밖 포인터 회귀1개를 추가한 Core359개를 재검증했다. 현재 합계1,232개에 해당한다. `artifacts/group-input-final-tests.log`. 테스트 수는 작업 당시 현재 소스 기준이며 다른 검사 기능 변경의 과거1,298개 결과와 직접 비교하지 않는다.
- PASS: 솔루션 Release 빌드0경고/0오류. `artifacts/group-input-verified-build.log`.
- 초기 슬라이딩5개 재현 FAIL은 `artifacts/roi-slide-red.log`에 보존했다. 최초 native smoke의 exact preview 불일치는 같은 입력을 반복 계산한 약1.8e-9px 차이였고 `group-slide-up-red.log`로 Core에서 재현 후 수정했다. 이미지 밖 포인터의 접선 이동 축소도 `group-slide-image-red.log`로 재현 후 수정했다.
- 기존 혼합 도형 smoke는 제거된 복수 회전 명령 뒤 Undo를 호출하여 엉뚱한 이전 편집을 복원했다. 복수 회전 비활성 검사를 추가하고 실제 그룹 하나의 회전·Undo와 그룹화 Undo를 검사하도록 갱신했다. 실패 로그 `gpu-full-final`, 구 실행물을 사용한 재시도 `gpu-full-verified`는 보존했다. 필터/마스크 검사 자체를 삭제하거나 완화하지 않았다.
- 최초 성능 시도 `baseline`은 Task.Delay와 작은 native 좌표/왕복 입력으로 예정240회 중 약125회만 발생해 정적 구간이 섞였다. 이때의 회전47FPS는 최적화 전후 비교 수치로 사용하지 않는다. `baseline-active`의 편집 미확정 실패와 `baseline-active-check`의 입력 큐 대기도 보존했으며 생산 취소 규칙을 바꿔 숨기지 않았다. 최종 비교는 동일한 고해상도 생성기·제어기 입력·Send 우선순위를 사용한다.

- PASS: 최종 GPU/debug·WARP 계층 smoke 종료0. `gpu-native-verified`/`warp-native-verified`의 `artifacts/smoke.log`와 캡처에 벽 슬라이딩·복수 회전 핸들 없음·색상·중첩 선택·빠른 이동/회전·외부 편입·깊이·이력이 기록되어 있다.
- PASS: 최종 GPU 전체 앱 smoke 종료0. `gpu-full-current/artifacts/smoke.log`. 단일 실제 그룹의 혼합 도형 회전·Undo, 필터/마스크·검색 범위·속성·8K 미니맵/라이브·재시작까지 확인했다.
- PASS: 변경 C# 파일에 한정한 whitespace/style 검사. `artifacts/group-input-whitespace-check.log`, `group-input-style-check.log`. 이번 입력 변경의 매뉴얼 원문52개와 동작 실습4개 발췌, HTML11개/로컬 참조299개 검사 통과. 실제 브라우저 배치는 변경하거나 재검증하지 않았다.
- 별도 잔여: 전체55개 원문 검사에서는 이번 ROI 범위 밖 `manual/08-inspection.html`의 `ShapeMatcher.cs` 삽입 원문이 현재 검사 코드와 다르다. 해당3개를 제외한52개를 따로 검사했으며 전체 원문 일치 PASS로 표현하지 않는다. 관련 없는 검사 코드/문서 변경을 이 작업에 섞지 않았다.


## 재현

```powershell
dotnet build TeachingToolWpf.slnx -c Release
# 결과를 새 출력 디렉터리에서 실행한다. 실행 파일은 위 빌드의 절대 경로를 사용한다.
TeachingToolWpf.exe --group-interaction-benchmark --rois 40000 --iterations 3 --report report.json --metrics frames.csv --render-profile profile.csv
python scripts/Measure-GroupInteraction.py <출력 디렉터리>
TeachingToolWpf.exe --smoke --gpu-debug --hierarchy-rules-smoke
TeachingToolWpf.exe --smoke --software-rendering --hierarchy-rules-smoke
TeachingToolWpf.exe --smoke --gpu-debug
```

이전 `--roi-rotation-benchmark`의 기본값도 실제 단일 그룹으로 변경했다. `--rotation-selection all|parents|half`는 제거된 복수 선택 회전임을 명시한 오류를 반환하며 `group`만 지원한다. 예전 성능 기록은 당시 기능의 이력이다.

## 설계 원칙 검토

| 원칙 | 확인 |
|---|---|
| SRP | Geometry는 접선 후보, Core는 부모 공동 제한/이력, 입력은 제스처, VM은 명령/선택 게시, 진단은 측정을 담당한다. |
| OCP | 기존 도형/편집 경로를 사용하며 WPF 분기를 기하에 추가하지 않았다. |
| LSP | 직접/준비된 편집의 포함·미리보기·취소·이력과 GPU/WARP 입력 계약을 유지한다. |
| ISP | IViewportInput·렌더·영상 인터페이스에 불필요한 메서드를 추가하지 않았다. translation callback의 bool은 Shift 정책 전달에 사용한다. |
| DIP | Geometry/Core에 WPF·DX12 의존성을 추가하지 않았다. |
| Information Expert | 접촉/접선은 실제 윤곽, 공동 부모·이미지 제한은 선택 데이터를 가진 Core에서 처리한다. |
| Creator | EditorSession의 생성/확정 불변식을 유지한다. 새 ROI나 임시 부모를 만들지 않는다. |
| Controller | View 입력 전달과 제어기의 제스처 선택 경계를 유지한다. |
| Low Coupling | 기존 preview worker에 마지막 요청의 정책만 전달한다. |
| High Cohesion | 새 범용 유틸리티/매니저 없이 해당 기하·선택·제스처 안에서 해결한다. |
| Polymorphism | 기존 입력 도구/도형 계약을 유지한다. 새 대체 구현은 필요하지 않다. |
| Pure Fabrication | 기존 BVH와 준비 worker를 재사용하며 새 캐시 수명을 추가하지 않았다. |
| Indirection | 앱 조립/VM/Input/Core/렌더 연결을 유지한다. |
| Protected Variations | native 입력과 성능 생성기를 분리하며 표시 장치 변화는 기존 경계 안에 둔다. |

설계 원칙 예외 없음. 검사 알고리즘/원본 이미지 품질/후보 수를 줄여 프레임 수치를 맞추지 않았다. WARP의 기능은 검증하며 이 부하에서 WARP60FPS, 별도 저사양PC, 현장 영상은 미측정이다.
