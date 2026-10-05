# 대량 ROI 복사·붙여넣기 최적화 검증

2026-10-05. 브랜치 `codex/vision-canvas-boundaries`에서 로컬 수정·검증만 수행하며 커밋·푸시는 하지 않는다. 기준 장비는 Windows x64, Ryzen 5 5600H, RAM 32GB, RTX 3060 Laptop이며 .NET 10 Release를 사용한다. 기존 사용자 변경과 GPU/WARP 표시 작업은 보존한다.

## 범위와 변경

수만 개 ROI를 복사·붙여넣을 때 반복하는 집합 생성과 공간 인덱스 정렬을 줄인다. 원본 ROI, 명시적 부모·자손 관계, 복사 순서, 복제 ID, 필터, 선택, 한 번의 편집에 대한 Undo/Redo와 취소 계약을 유지한다. 표시 항목이나 검출 조건을 줄이지 않으며 검사 125/80ms 목표와는 별도 작업이다. 설계 원칙의 성능 예외는 없다.

제품 변경은 `VisionCanvas.Core`의 다음 5개 파일이다.

| 파일 | 변경과 보존 조건 |
|---|---|
| `Editing/RoiClipboard.cs` | 전체 선택은 이미 모든 ROI를 포함하므로 부모·자손 집합을 다시 만들지 않는다. 부분 선택은 해당 ID로 ROI를 조회해 문서 전체 열거를 피한다. 두 경로 모두 기존 `Order` 정렬을 유지하며 빈 선택의 기존 클립보드 보존도 유지한다. |
| `Geometry/RoiSpatialIndex.cs` | 내부 `Filter`가 기존 BVH 분할을 따라 포함/제외 부분집합을 만든다. 그대로 남는 노드와 공간 블록 identity를 재사용하고, 제거된 가지 자리는 빈 노드로 남겨 기존 ID 경로를 유지한다. 현재 스냅샷의 값 맵을 전달해 메타데이터 변경을 반영한다. |
| `Editing/RoiSnapshot.cs` | 내부 `IsSpatialCreated`로 공간 인덱스가 이미 만들어졌는지 확인한다. 최적화를 위해 사용하지 않던 전체 인덱스를 먼저 만드는 일을 막는다. |
| `Editing/DocumentState.cs` | 준비된 전체 인덱스가 있고 선택이 64개 초과이면서 문서의 1/8을 초과할 때 선택 인덱스를 필터링한다. 그 외에는 기존 선택 ROI 직접 구축을 유지한다. |
| `Editing/RoiTransformPreview.cs` | 움직이는 자손 집합과 정지 집합도 준비된 인덱스에서 재사용할 수 있는 경우 필터링한다. 전체 선택·작은 부분집합·전체 인덱스 미생성 경로는 기존 방식을 유지한다. |

성능 재현을 위해 별도로 `CopyPasteProfile`과 앱 `ClipboardRun` 진단을 확장했다. 측정 도구가 제품의 복사·붙여넣기 알고리즘을 대체하지 않으며, 일반 사용자 흐름에 계측을 추가하지 않는다.

## 기능·회귀 검증

증거 루트는 `artifacts/clipboard-optimization`이다. 최종 원시 JSON·집계·테스트/형식 검사 로그·대표 렌더 캡처는 [검증 자료 ZIP](data/clipboard-optimization-20261005.zip)에 보관한다.

| 상태 | 검증 | 근거와 범위 |
|---|---|---|
| PASS | 전체 회귀 958개 | `tests.log`: Core194 + Canvas117 + 앱240 + Inspection107 + Algorithm248 + CUDA52, 실패/건너뜀0. |
| PASS | 복사 순서 회귀 2개 추가 후 Core196개 | 전체 선택/부분 선택에서 삭제로 비연속인 `Order`, 자식보다 나중에 생성된 그룹 부모, 부모·자식 동시 선택을 검증. 전체 회귀와 합쳐 서로 다른 테스트 960개에 해당하며 최종 전체 솔루션 일괄 실행과 구별한다. 최종 Release 빌드 뒤 `dotnet test tests/TeachingToolWpf.Core.Tests/TeachingToolWpf.Core.Tests.csproj -c Release --no-build --no-restore`의 CLI 출력에서 실패/건너뜀0을 확인했다. |
| PASS | 공간 부분집합 동등성 | `SpatialSubsetTests`: 빈/전체/부분 선택, 부모 밖 자손, 중복 없는 이동 집합, 원본 불변성, 최신 메타데이터, 이동·삭제·추가와 빈 집합 재삽입, 유지된 블록 identity, 겹친 ROI hit 우선순위를 확인한다. 독립 재구축 인덱스와 교차·가시 결과, hit, 회전 경계·허용각을 비교한다. |
| PASS | 실제 앱 복사·붙여넣기 반복 | 최종 `app-cycles-*` 보고서12개 모두 `stage=complete`, `error=null`, 검증8항목과 PNG 캡처 존재를 확인했다. 원본·복제·부모·루트 선택·목록 페이지·Undo/Redo를 검사한다. |
| FAIL(기존) | 전체 형식 검사 | `format-full.log`에서6파일의 whitespace 위반을 확인했다. 해당6경로의 `git diff --exit-code`가 exit0으로 HEAD와 동일함을 확인했다. 이번 최적화에 무관한 기존 코드는 재포맷하지 않았다. |
| PASS | 최종 빌드·변경 범위 형식·diff | Release 빌드 경고0/오류0. 변경10개 C# 파일의 whitespace·명명/style 검증과 `git diff --check` 통과. `format-scoped-whitespace.log`와 `format-scoped-style.log`는 오류 없는 빈 로그이며 명령 exit0을 확인했다. |
| PASS | 추가 RTX/WARP smoke | `app-final-overlap-filter.json`: 7천 중첩·필터 ROI→1만4천. `app-final-repeat-pcb.json`: 300부터1만9천2백까지 반복 복제. `app-final-warp-pcb.json`: 실제 Microsoft Basic Render Driver에서4만→8만. 모두 DX12 debug on, 취소·이력·계층·목록·렌더 캡처 확인, complete/error=null/exit0. WARP 시험은 실제 저사양 PC 성능 보장이 아니다. |

Core 진단은 마지막 준비 결과를 실제로 확정하고 복제 형상·메타데이터·부모·루트 선택·원본 보존 및 Undo/Redo 값 복원을 측정 바깥에서 검사한다. 앱 진단은 실제 명령 경로, 취소 후 문서 유지, 성공한 복제의 ID·부모·메타데이터·형상 규격, 이력과 선택 복원, 전체 ROI 목록 및 표시 페이지를 검증한다. 오류·취소·검증 실패를 예외 무시로 성공 처리하지 않는다.

## 측정 방법과 해석

### Core 구간 진단

8192×8192 이미지 좌표의 ROI 40,000개를 전체 선택해 80,000개로 만드는 작업이다. `flat`은 독립 루트, `pcb`는 부품 하나와 패드 최대 8개의 명시적 계층이다. Core fixture는 이미지 중앙 4096×4096 영역에 두며, 앱 fixture는 전체 8192×8192 영역을 사용한다. 두 진단의 절대 시간을 동일 입력 결과로 합치지 않는다.

동일 프로세스의 같은 세션·불변 원본으로 워밍업2회와 측정5회를 수행한다. 각 회차는 새 클립보드로 복사한 뒤 공개 `PreparePasteAsync`를 await하며, 준비 도중 세션이 바뀌지 않았는지 확인한다. 세션 생성·초기 ROI 구축·원본 인덱스 예열은 별도 `FixtureStages`다. 준비 시간에는 배치·복제·이력 작성·인덱스 예열이 포함된다. GC를 강제로 실행하지 않는다.

`ReplayStages`는 이미 예열된 준비 결과를 다시 측정하는 오류를 피하기 위해, 동일 결과 값으로 별도의 미예열 스냅샷을 만들어 공개 연산을 재실행한 진단이다. Place, DocumentEdit, Spatial, Children, SelectionFacts, SelectedSpatial, TransformSelection, Moving, Stationary, RotationLimits를 나눈다. 이 값들은 통합 준비 시간을 구성하는 직접 계측값이 아니므로 통합 시간에 더하거나 정확한 분해 합계로 해석하지 않는다.

할당량은 준비 worker를 포함하는 프로세스 전체 누적 할당 증가량이며 상주 메모리나 최대 메모리가 아니다. Core DLL SHA256, 프레임워크, 논리 프로세서 수, 워밍업 여부, GC 정지 시간을 JSON에 남긴다. 공개 `AddRange`가 만든 원본 ID는 프로세스마다 새 GUID이며, 한 프로세스 안에서 모든 회차는 같은 원본을 사용한다.

### 실제 앱 반복 진단

`--clipboard-count 40000 --clipboard-cycles 5`로 각 회차에서 4만→8만 붙여넣기를 수행한다. 다음 회차는 Undo로 같은 원본 값·선택을 복원한 뒤 Copy/Paste한다. `flat`/`pcb` 각각 before/after를 번갈아 3개 프로세스씩 실행하며, 프로세스마다 앞2회는 워밍업, 뒤3회는 측정이다. 따라서 방식·레이아웃당 반복 측정은9표본이다. 복제 ID와 순서는 새 편집에 맞게 생성되며 8만→16만으로 누적 증가하는 시험은 아니다.

초기 생성·마퀴·복사·취소 시험과 첫 성공한 붙여넣기는 `phase=initial`로 반복 표본에서 분리한다. 초기 `paste`도 취소 시험 이후의 첫 성공 작업이므로 완전히 미준비된 프로세스의 최초 호출이라고 표현하지 않는다. 최초 성공과 반복 평균을 혼합하지 않는다.

앱 `milliseconds`는 실제 명령 호출부터 편집/붙여넣기 완료, UI 게시와 `UpdateLayout` 반환까지다. `dispatchMs`는 호출이 반환한 시간으로, 비동기 작업 완료 시간이 아니다. `maxDispatcherGapMs`는 해당 구간의 16ms Normal-priority DispatcherTimer 사이 최대 간격과 구간 양끝의 간격을 포함한다. 스케줄링·GC·동기 UI 작업의 영향을 받는 응답성 보조 지표이며 GPU 프레임 간격이나 물리 모니터의 표시 지연이 아니다.

진단 창은 화면 밖에 배치되며 종료 전 별도의 렌더 캡처를 확인한다. 작업마다 GPU 완료·최종 Present·물리 화면 scan-out을 기다리는 측정은 아니므로 클릭부터 실제 화면 표시까지의 수용 결과나 무버벅임 보장으로 해석하지 않는다. 실제 표시된 모든 ROI가 개별 윤곽으로 동시에 그려졌다는 의미도 아니다. 앱과 Core DLL의 SHA256은 보고서 `binaries`로 확인하고, 같은 최종 진단 실행 파일에 변경 전/후 Core를 각각 사용한 비교인지 검증한다.

## 측정 결과

앱 최종 수치는 `app-summary.json`과 `app-cycles-before/after-*`에 근거한다. 초기 탐색용 `app-before-*`, `app-paired-*`와 섞지 않았다. 아래 표는 모두 변경 전→후 순서이며 시간은 ms다. 할당량 MB는10진수1,000,000bytes이며 MiB와 구별한다.

| 바이너리 | SHA256 |
|---|---|
| 변경 전 Core | `2307EA545EEC57651640864566FF6BB80C6FBCFD8E5DCDC7044F6E42FC974C2B` |
| 변경 후 Core | `16DF55F28D08E13037435B3A578F1D7B3B65E3A920AEA42C52C05A2F4772698B` |
| 양쪽 공통 앱·진단 | `435BF69D4CB63822F270F730187683DD54FBE8A24F64DEDCB5930EF63089721F` |

초기 `core-before/after-*`의3프로세스 반복 결과는 최종 서식 적용 전 빌드에서 얻은 구간 진단 자료다. 아래 최종 표는 `core-final-before/after-pcb/flat.json` 4개에만 근거하며, 각 레이아웃의 변경 전/후1프로세스씩 워밍업2회와 측정5회를 실행했다. 각 보고서의 Core SHA256은 위 앱 측정의 해당 바이너리와 일치하며 복제·부모·선택·원본 보존·Undo/Redo 값 검증은 모두 PASS다. 앱의3프로세스 반복 표본과 표본 수를 혼동하지 않는다.

| Core 레이아웃 | Copy+Prepare 평균 | Copy 평균 | Prepare 평균 | 합계 누적 할당 MB |
|---|---|---|---|---|
| flat | 640.47→582.67 | 20.58→11.32 | 619.85→571.32 | 82.82→78.95 |
| pcb | 628.62→524.68 | 30.61→10.90 | 597.97→513.74 | 88.23→83.79 |

Core 합계 할당 감소는 Copy 할당 감소를 포함한 값이다. Prepare만 보면 flat75.42→77.72MB, pcb80.83→82.56MB로 늘었으므로 붙여넣기 자체의 메모리 개선으로 표현하지 않는다. 한 프로세스당5회 결과이며 일반적인 개선율 보장으로 사용하지 않는다.

별도 `ReplayStages`에서 정지 집합 구성 평균은 flat109.79→57.06ms, pcb109.62→55.29ms, pcb 이동 집합은107.13→61.58ms다. 재정렬을 줄이는 방향을 뒷받침하는 진단이지만 앞서 명시한 대로 실제 준비 작업의 직접 분해값은 아니다. 변경 후에도 전체8만 ROI 공간 인덱스 재구축은 replay에서 flat239.61ms·pcb204.78ms로 남아 있다.

| 앱 레이아웃 | 반복 Copy 평균 | 반복 Paste 평균 | 반복 Paste 중앙값 | Undo 평균 |
|---|---|---|---|---|
| flat | 30.79→10.22 | 700.42→638.89 | 697.65→606.02 | 194.52→198.72 |
| pcb | 41.21→10.63 | 729.06→637.76 | 674.82→652.26 | 220.20→207.63 |

레이아웃·방식당9표본(3프로세스×3회)이다. Paste 평균은 flat 약8.8%, pcb 약12.5% 감소했지만 중앙값 감소는 각각 약13.1%, 약3.3%로 다르다. 평균 하나만으로 일관된 개선 폭을 주장하지 않는다. flat Undo 평균은 약4.2ms 증가했다.

| 앱 레이아웃 | 반복 Paste 최소–최대 | Paste dispatcher 최대 간격 | 반복 Copy 평균 할당 MB | 반복 Paste 평균 할당 MB |
|---|---|---|---|---|
| flat | 652.44–757.66→577.80–723.52 | 47.07→51.61 | 7.32→1.15 | 104.43→109.36 |
| pcb | 655.26–862.00→554.59–721.25 | 57.93→48.59 | 7.32→1.15 | 111.93→114.18 |

복사 평균은 약31–41ms에서 약10–11ms로 줄었으며 할당도 약7.32MB에서1.15MB로 줄었다. 붙여넣기 평균은 줄었지만 할당은 flat 약4.92MB, pcb 약2.25MB 증가했다. 부분집합 경로 맵·노드 구성의 비용과 앱 계측의 다른 할당을 포함하므로 재정렬 감소가 모든 할당을 줄였다고 해석하지 않는다. Dispatcher 최대 간격은 pcb에서는 줄고 flat에서는 늘었다. 모든 UI 멈춤이나 프레임 지연이 개선됐다는 결과가 아니다.

| 앱 레이아웃 | 초기 첫 성공 Paste 평균 | 초기 첫 성공 Paste 최소–최대 | 완료·정확성·렌더 오류 |
|---|---|---|---|
| flat | 1864.74→1621.96 | 1038.58–2833.60→1016.51–1946.36 | 양쪽3프로세스 모두 완료·검증 통과·오류없음 |
| pcb | 2181.24→1849.77 | 1076.95–2763.71→1735.04–1961.06 | 양쪽3프로세스 모두 완료·검증 통과·오류없음 |

첫 성공한 Paste는 변경 후에도 약1.02–1.96초 걸렸다. 반복 평균 약0.64초와 구별하며, 대량 붙여넣기의 대기와 초기 비용은 남아 있다. 이번 측정은 복사 비용의 뚜렷한 감소와 제한된 붙여넣기 시간 개선을 뒷받침한다. 붙여넣기 할당 증가와 일관되지 않은 Dispatcher 간격은 후속 병목 분석에 남긴다.

## 재현 명령

저장소 루트에서 실행한다. before 비교는 변경 전 Core를 보관한 별도 실행 디렉터리를 사용하고, 최종 진단 앱·하네스 및 나머지 의존성이 같은지 SHA256으로 확인한다. Release 빌드·시험과 성능 측정은 동시에 돌리지 않는다.

```powershell
dotnet build TeachingToolWpf.slnx -c Release
dotnet run --no-build --project benchmarks/TeachingToolWpf.Benchmarks -c Release -- --copy-paste-profile --rois 40000 --layout pcb --report artifacts/clipboard-optimization/core-after-pcb-1.json
dotnet run --no-build --project src/TeachingToolWpf -c Release -- --clipboard-check --clipboard-layout pcb --clipboard-count 40000 --clipboard-cycles 5 --clipboard-report artifacts/clipboard-optimization/app-cycles-after-pcb-1.json
```

`pcb`를 `flat`으로 바꿔 각각 실행한다. 프로세스마다 보고서 이름의 회차를1·2·3으로 구분하고 before/after를 교대로 실행한다. 진단은 성공 시 종료하며 실패하면 앱 보고서의 `error`와 비정상 종료 코드를 확인한다. 실제 성공·실패는 JSON과 로그로 판정한다.

## SOLID·GRASP 검토

| 원칙 | 이번 변경의 적용·검토 |
|---|---|
| SRP | 복사 항목 선택은 RoiClipboard, 공간 부분집합 생성은 RoiSpatialIndex, 파생 인덱스 선택은 기존 문서·변형 준비 책임에 둔다. |
| OCP | 기존 공간 조회·편집 계약을 바꾸지 않고 내부 부분집합 구성 방법만 추가한다. 새로운 공개 확장 지점은 만들지 않는다. |
| LSP | 필터링한 인덱스가 독립 재구축과 같은 조회·hit·회전·편집 결과를 내는지 회귀로 확인한다. 불변 원본을 변경하지 않는다. |
| ISP | 내부 Filter와 준비 여부만 추가하며 소비자가 쓰지 않는 메서드를 구현하도록 강제하지 않는다. |
| DIP | Core는 기존 기하·영상 계약에 의존한다. WPF·DX12·앱 진단이나 공급자 참조를 추가하지 않는다. |
| Information Expert | 노드·ID 경로·블록 identity를 소유한 공간 인덱스가 부분집합을 구성한다. |
| Creator | 공간 인덱스가 새 노드와 경로 맵을 생성하며, ROI 복제 ID·부모 생성은 기존 붙여넣기 책임을 유지한다. |
| Controller | 앱은 기존 편집 명령을 호출하고 집합·공간 계산은 Core가 수행한다. 진단에서 업무 규칙을 대신 구현하지 않는다. |
| Low Coupling | 제품 변경은 Core 내부5파일로 제한하며 렌더러·검사·파일 I/O와 새로운 결합을 만들지 않는다. |
| High Cohesion | 복사 열거와 공간 인덱스 재사용이라는 기존 책임 안에서 수정한다. 범용 매니저나 중계 계층을 추가하지 않는다. |
| Polymorphism | 새 대체 구현·공급자가 필요한 변경이 아니므로 인터페이스를 추가하지 않는다. 기존 조회 계약의 결과 동등성을 검증한다. |
| Pure Fabrication | 기존 기술 책임인 공간 인덱스와 독립 진단 도구를 활용한다. ROI에 캐시 정책·계측 상태를 넣지 않는다. |
| Indirection | 기존 세션·불변 스냅샷·인덱스 경계를 유지한다. 중간 계층을 늘리지 않고 준비 여부로 불필요한 구축을 막는다. |
| Protected Variations | 재사용 여부와 내부 BVH 배치가 사용자 형상·부모·선택·이력 계약에 드러나지 않도록 격리하고 동등성 회귀를 둔다. |

## 남은 범위

다른 CPU·GPU, 실제 GPU 없는 PC, 장시간 반복에 따른 메모리와 꼬리 지연, 대규모 중첩/깊은 계층, 물리 입력부터 최종 화면 표시까지의 실측은 이번 4만→8만 flat/pcb 시험으로 보장하지 않는다. 작은 부분집합의 기존 경로를 유지하지만 모든 분포에서 필터링이 최선이라는 주장은 하지 않는다. 최종 측정에 남는 할당·UI 게시·렌더 비용은 별도 근거로 후속 우선순위를 정한다.
