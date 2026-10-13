# 신입용 가독성 정리와 HTML 매뉴얼 검증

후속 반영 안내: 이 문서의 로컬 수정·커밋/푸시 없음은 각 검증 당시 상태다. 이후 사용자가 현재 프로젝트 변경 전체의 검증 후 커밋·푸시 및 main 반영을 승인했다. 반영 전 재검증은 [작업 목록](../tasks/todo.md)에 기록한다.

2026-10-13. 저장소 `TeachingToolWpf`, 브랜치 `main`. 사용자 선택에 따라 **로컬 수정·검증만** 수행했다. 기존 대량 ROI·DX12·문서 작업의 미커밋 변경을 보존했다.

## 가정과 변경

대상 독자는 C# 기본 문법을 배웠지만 영상처리·행렬·WPF·GPU·불변 자료구조 경험이 없는 개발자다. 기존 설계 경계를 유지하고 학습의 출발 경로를 정리했다. 저장소 전체를 재작성하거나 모든 과거 구현을 전면 감사한 작업은 아니다.

- `EditHistory.cs`에 함께 있던 기존 `DocumentEdit`/`RoiChange`, `HistoryStep`, `PreparedHistoryStep`을 각각 대응 파일로 이동했다. 알고리즘·namespace·타입·서명은 같다. 편집 차이, 준비 요청, 준비 결과, 이력 목록/커서/예산을 구별하는 한국어 책임 주석을 추가했다.
- `TilePlanner.Visible`의 지역 이름에 원본 좌표·타일 열/행·제외 끝의 의미를 드러냈다. LOD·반올림·순회 순서·리스트 할당은 그대로다.
- `ViewportTransform`, `RigidTransform2D`, `RoiGeometry`의 단위·확대 anchor·회전/역변환·AABB 반너비/반높이를 풀어 설명했다. 수식의 계산 순서를 유지했다.
- [manual/index.html](../manual/index.html)에 13페이지의 오프라인 HTML 실습서를 작성했다. 수식의 기호·단위·손계산 예, 실제 소스, 과제/정답, 용어 검색, 메모리·회전·이진화·타일 단계 실험, 인쇄용 CSS를 포함한다.
- `manual/labs/Foundations`는 기존 Core/기하/영상 계약을 사용하는 콘솔 실습, `manual/labs/CanvasHost`는 기존 Canvas에 8K 합성 타일·입력·편집·이력을 연결하는 WPF 호스트다. 두 예제는 제품 솔루션이나 실행 경로에 추가하지 않았다. 새 생산 패키지·전역 상태·중계 계층을 추가하지 않았다.

## 성능 보존의 근거와 한계

변경 전 현재 작업 트리를 Release로 빌드한 뒤 다음 타입과 컴파일러 생성 하위 타입의 메서드 본문을 추출했다.

`ViewportTransform`, `RigidTransform2D`, `RoiGeometry`, `TilePlanner`, `RoiChange`, `DocumentEdit`, `HistoryStep`, `PreparedHistoryStep`, `EditHistory`.

파일 이동으로 달라지는 메타데이터 토큰은 실제 타입/멤버 이름으로 해석했다. 메서드 키 순으로 정렬한 IL opcode·피연산자·지역 변수 형식·예외 구성·최대 스택·InitLocals를 전후 비교했다. **151개 모두 동일**하다. 정규화 JSON의 전후 SHA256은 모두 다음과 같다.

```text
74560E26D97CC003471B9B3B017215CB4CC90BFE3A69CCFE85DDFA3669311263
```

로컬 증거는 `artifacts/manual-refactor/before-il.json`, `after-il.json`, 비교 도구는 `artifacts/manual-refactor/il-tool`에 있다. 이 폴더는 저장소의 기존 artifacts 제외 규칙을 따른다. 기준 소스도 `before`에 보관하여 기존 미커밋 변경을 기준으로 비교했다.

```powershell
dotnet run --project artifacts/manual-refactor/il-tool -- src/TeachingToolWpf/bin/Release/net10.0-windows artifacts/manual-refactor/after-il.json
Get-FileHash artifacts/manual-refactor/before-il.json
Get-FileHash artifacts/manual-refactor/after-il.json
```

이는 변경한 생산 코드에 추가 실행 연산·복사·할당을 넣지 않았다는 근거다. DLL/PDB 전체 바이트 동일성, 모든 JIT/하드웨어에서의 절대 실행 시간 동일성, 전체 프레임 예산 통과를 뜻하지 않는다. 새 장시간 FPS 벤치마크는 **NOT_RUN**이다. 기존 엄격한60FPS와 검사CPU125ms/CUDA80ms 목표의 **FAIL을 유지**한다.

## 실행한 검증

| 상태 | 검증 | 결과 |
|---|---|---|
| PASS | 변경 전 Release 솔루션 `-m:1` | 경고0/오류0 |
| PASS | 변경 전 Core/TilePlanner | 296+2개 |
| PASS | 변경 후 Release 솔루션 `-m:1` | 경고0/오류0 |
| PASS | 변경 후 Core / Canvas / Imaging.Tests | 296 / 222 / 274개, 합792, 실패·건너뜀0 |
| PASS | Foundations Release 빌드·실행 | 좌표 왕복·anchor·회전·Gray16·계층·Undo/Redo·분기 제거 11개 확인 |
| PASS | CanvasHost Release 빌드 | 경고0/오류0, HLSL 4파일 출력 복사 확인 |
| PASS | CanvasHost 실제 Windows 실행 | RTX3060 Laptop GPU, 8K 체커·초기 ROI·세션 삭제/Undo/Redo·PNG 캡처, exit0 |
| PASS | 이번 생산 코드8파일 whitespace/style | IDE1006/0003/0011/0047/0048/0161/0065 검사 |
| PASS | C# 실습 whitespace/style | 해당 실습 소스만 검사 |
| PASS | HTML 정적 구조·링크 | 13페이지, 고유 id·h1·ko 언어·태그 균형·로컬 경로·anchor·외부 런타임 자원 없음 |
| PASS | JS 구문·DOM 계약 | 계산/경계/DPI/용어 검색/목차 15개 확인. 실제 브라우저 DOM 시험은 아님 |
| FAIL | 전체 `Format-Code.ps1 -Verify` | 기존 미수정4파일의 ENDOFLINE14곳 |
| BLOCKED | 실제 HTML 브라우저 검사 | 도구 정책이 file:// 접근 차단. 다른 경로로 우회하지 않음 |
| NOT_RUN | HTML 실제 화면·반응형·인쇄/PDF·브라우저 키보드 | 정적 CSS/구조 검토만 수행 |
| NOT_RUN | 새 학습 호스트 물리 마우스/키보드·WARP·장시간 | GPU 초기화와 세션 API/캡처 시험만 수행 |

전체 스타일 실패 위치는 `VisionAlgorithm/OpenSrc/PatternMatching/PreparedSearchImage.cs` 8곳, `ShapeMatcher.cs` 3곳, `TrainingPixelMask.cs` 1곳, `tests/VisionAlgorithm.Tests/MaskingTests.cs` 2곳이다. 이번 범위 밖 파일을 수정하거나 검사 기준을 낮추지 않았다.

최초 병렬 빌드는 경고/오류0 표시에도 exit1이어서 성공으로 계산하지 않았다. `-m:1` 재실행은 성공했다. 한 번의 빌드 동반 Core 테스트는 testhost 연결90초 시간 초과로 중단됐고, 완료된 Release 실행물의 `--no-build` 재실행에서296개가 통과했다. `dotnet format`의 sandbox 파이프 접근 오류는 권한 승인 후 같은 검사로 재실행하여 통과했다. 새 예제 NuGet 네트워크 제한은 승인된 restore 뒤 빌드 성공으로 확인했다. 임시 WPF 검증 도구의 restore에는 캐시된 NU1900 경고가 남았으며 제품/학습 예제 빌드 경고와 구별한다.

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore -m:1
dotnet test tests/TeachingToolWpf.Core.Tests -c Release --no-build --no-restore
dotnet test tests/VisionCanvas.Tests -c Release --no-build --no-restore
dotnet test tests/TeachingToolWpf.Imaging.Tests -c Release --no-build --no-restore
dotnet build manual/labs/Foundations -c Release -m:1
dotnet run --project manual/labs/Foundations -c Release --no-build
dotnet build manual/labs/CanvasHost -c Release -m:1
dotnet run --project manual/labs/CanvasHost -c Release --no-build
```

학습 호스트 실실행 증거는 [요약 JSON](beginner-manual-results.json), [GPU 화면 캡처](../manual/assets/canvas-host.png)에 남겼다. 캡처는 GPU 뷰포트만 포함하며 WPF 툴바는 포함하지 않는다. 로컬 자동 실행 도구/로그는 `artifacts/manual-refactor/host-smoke`, `host-runtime`에 있다. 이 도구는 실제 LessonWindow를 열고 세션 API를 호출하며, 렌더러를 모방하지 않는다. 물리 입력 시험은 아니다.

## SOLID·GRASP 리뷰

| 원칙 | 검토 |
|---|---|
| SRP | 기존 이력 타입별 읽기 경계와 책임을 드러냄. 역할 추가 없음 |
| OCP | 생산 확장 지점/공개 계약 변경 없음. 공급자 신규 도입과 무관 |
| LSP | 생산 대체 구현 변화 없음. 기존 오류/동작·회귀/IL 보존. 교육 소스는 key·취소·해제 계약 검토 |
| ISP | 새 범용 인터페이스·미사용 메서드 강요 없음 |
| DIP | Core에 WPF/DX12/검사 참조 추가 없음. 호스트가 조립 |
| Information Expert | 기하 계산은 Geometry, 타일 주소는 Planner에 유지 |
| Creator | 문서 생성 규칙/GPU 자원 생성 위치 유지. 교육 버퍼의 성공/실패 소유권 명시 |
| Controller | 버튼은 EditorSession에 요청. 문서 사전 직접 변경 없음 |
| Low Coupling | 제품 프로젝트 참조 경계 보존. manual을 제품이 참조하지 않음 |
| High Cohesion | 차이/준비 요청/준비 결과/이력 순서를 각각 읽도록 분리 |
| Polymorphism | 생산 변형 추가와 무관. 학습 소스는 기존 ITileSource 사용 |
| Pure Fabrication | 이력의 기술 책임 유지. 불필요한 manager/wrapper 추가 없음 |
| Indirection | 기존 계약과 호스트 연결 유지, 새 픽셀 중계/복사 없음 |
| Protected Variations | 플랫폼·파일·알고리즘 세부가 Core로 유입되지 않음 |

원칙 예외는 없다. 데이터 배치, 이력 정확성, 성능 목표를 가독성을 이유로 완화하지 않았다. HTML의 실제 가독성·초보자의 독립 구축 가능성은 신입 독자의 사용 평가 전까지 검증 완료로 주장하지 않는다.

## 2026-10-13 후속: 본문 코드와 타일링 설명

사용자 피드백에 따라 소스 파일로 이동하지 않고 읽을 수 있도록 1~9장의 설명 위치에 실제 코드16곳을 넣었다. 전체 파일과 연속 발췌를 구별하고, 각 블록의 `data-source`에 원본 경로를 기록했다. 짧은 코드는 기본으로 펼치고 긴 코드는 같은 페이지의 details로 제공한다. 9장에는 Foundations의 2파일과 CanvasHost의 4파일 전체가 있다. 코드 자체를 HTML에 저장하므로 실행 중 소스 fetch나 외부 라이브러리가 필요 없다. 3장의 독립 C# 이진화 예제는 실제 제품 코드 발췌와 구별했다.

6장은 타일 주소·유효 크기·가시 타일·피라미드/DPI·부분 읽기·캐시·세대·필터 halo를 순서대로 확장했다. 1300×900의 가장자리 276×388 계산, 8192 피라미드5단계의 Gray8 합85.25MiB, 표시 단계와 검사 원본의 차이를 설명한다. 새 창 이동 실험은 actual `TilePlanner`의 격자 제한을 따른다. x=1400처럼 영상 바깥이어도 마지막512px 격자 셀 안이면 가장자리 타일을 요청할 수 있는 동작을 그대로 표시하고, 개요 추가·캐시 적중은 계산기 범위에서 제외한다고 명시했다. 개요 우선·32준비 슬롯·Update당 최대16결과·세대 전환·fence 뒤 해제는 실제 소스를 대조했다. halo는 교육용 설계 예이며 현재 표시 파이프라인의 새 기능으로 주장하지 않는다.

| 상태 | 후속 검증 | 결과 |
|---|---|---|
| PASS | HTML 정적 구조·링크 재검사 | 13페이지, 로컬 참조335개, 오류0 |
| PASS | HTML 코드와 실제 파일 대조 | 16곳 일치. 전체 파일은 줄바꿈 정규화/끝 공백 제거 후 비교, 발췌는 연속 행과 공통 들여쓰기만 허용 |
| PASS | 새 C# 확인 도구 Release 빌드 | 경고0/오류0. 실제 TilePlanner 소스 파일을 링크해 실행 |
| PASS | 실제 C# 타일 계획 | 기본4타일, 경계512의 1타일, 경계±1, 모서리·완전히 격자 밖·마지막 격자 셀 등10사례 |
| PASS | JS 구문·DOM 계약 | 기존15개 + C# 결과와 주소/강조색/창 사각형을 대조한10개 = 25개 |
| PASS | 독립 C# 이진화 예제 | 임계값100 포함 경계의 결과 `0, 0, 255, 255` |
| BLOCKED | 실제 브라우저 화면·이벤트·인쇄 | 앞선 file:// 도구 정책 제한 유지. DOM 대역 시험을 브라우저 실행으로 표현하지 않음 |
| NOT_RUN | 제품 전체 재빌드·회귀·FPS 재측정 | 이번 후속은 HTML/CSS/학습용 JS와 설명만 수정. 생산 C#·실습 프로젝트의 실행 코드 변경 없음 |

로컬 검증 도구와 결과는 `artifacts/manual-refactor/check_inline_examples.py`, `inline-check.json`, `verify_manual.py`, `html-check.json`, `tiling-checks`, `tile-cases.json`, `check_manual_js.cjs`에 있다. 기존 artifacts 제외 규칙을 따른다. 처음 새 C# 확인 도구의 암묵적 빌드는 실패했고 명시적 `--no-restore` 빌드에서 NETSDK1004(자산 파일 없음)를 확인했다. `dotnet restore ... --ignore-failed-sources -m:1` 후 빌드·실행이 통과했다. 확인 도구의 튜플 이름 경고도 수정한 뒤 경고0으로 재검사했다.

후속 원칙 검토: SRP·Information Expert·High Cohesion은 장별 설명과 각 실험 내부 계산으로 유지했다. OCP·LSP·ISP·DIP·Creator·Controller·Low Coupling·Polymorphism·Pure Fabrication·Indirection·Protected Variations는 생산 계약/생성/제어/의존성/변형 지점을 바꾸지 않아 새 적용 변경이 없으며, 본문의 실제 코드와 앞선 원칙 검토를 대조했다. 원칙 예외, 생산 경로 추가 비용, 커밋·푸시는 없다. 소스 변경 시 본문의 정적 코드도 갱신해야 하는 유지보수 책임은 `manual/README.md`에 기록했다.

## 2026-10-13 후속: 수학책 형태의 수식 표기

사용자가 수학책 같은 수식 조판을 요청하여 1·2·3·6·7·8·10·12장의 수식47곳을 표준 MathML로 변환했다. 독립 수식20곳과 본문/색인 수식27곳이다. 분수선·근호·상하 첨자·합 기호의 범위·평균 윗줄·조건별 식·회전/영상/필터 행렬을 표현한다. 텍스트 수식을 이미지로 바꾸지 않았으며, HTML 자체에 표시 구조와 선형 설명을 보관한다. 소스 코드 블록과 수식은 구분했다.

수식 상자는 흰 배경과 시스템 수학 글꼴을 사용한다. 긴 수식/색인 표는 키보드 초점과 가로 스크롤을 제공하고 인쇄용 크기/배경을 설정했다. 브라우저의 기본 MathML 지원을 사용하므로 CDN·웹 폰트·수식 렌더링 스크립트를 추가하지 않았다. 표기 기준은 [MDN MathML](https://developer.mozilla.org/en-US/docs/Web/MathML)과 [행렬/수식 정렬](https://developer.mozilla.org/en-US/docs/Web/MathML/Tutorials/For_beginners/Tables)을 확인했다. 기존의 기호·단위·손계산 설명을 유지하고 Sobel의 커널 Kx/Ky와 계산 결과 Gx/Gy를 구별해 적었다.

- PASS: MathML47개 XML/namespace/자식 수/행렬 열 수/선형 설명 일치, 독립 수식20곳 모두 MathML 포함. 이진화 경계, Sobel 계수, 회전 행렬 부호, NCC 분모·합 범위, LOD 분수, 커널 합 범위의6개 전사 확인.
- PASS: HTML13페이지·내부 참조335개 정적 검사, 본문 실제 코드16곳의 원본 일치 재확인.
- BLOCKED: 기존 file:// 도구 접근 제한으로 실제 브라우저 수식 배치·인쇄·보조공학 결과는 미검증. 정적 구조 검사와 구별하며 우회하지 않음.
- NOT_RUN: 제품 재빌드·회귀·FPS. 제품 C#·실습 실행 코드·학습 JavaScript에 변경이 없어 이번 표기 작업에서 다시 실행하지 않음.

로컬 변환/검증 자료는 `artifacts/manual-refactor/format_math.py`, `before-math`, `verify_math.py`, `math-check.json`에 보관했다. 이번 후속은 HTML/CSS 표기만 변경하므로 앞선 SOLID5·GRASP9의 생산 경계/계약 검토가 유지되며 새 설계 예외는 없다. SRP·High Cohesion은 공통 수식 스타일과 장별 설명으로 유지하고 나머지 원칙의 생산 적용점은 바꾸지 않았다. 로컬 수정만 수행했고 커밋·푸시하지 않았다.
