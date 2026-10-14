# 신입용 가독성 정리와 HTML 매뉴얼 검증

## 2026-10-14 후속: 직접 조작하는 ROI 드래그 그림

5장 앞에 SVG 사각형의 직접 드래그와 자동 재생·일시정지·한 단계 진행·Escape 취소·초기화를 추가했다. 확정 중심, 미리보기 이동량, 보이는 중심과 이력 수를 표시한다. 단계별로 클래스 호출 경로와 실제 C# 발췌4개를 갱신한다. 기본 동기 경로의 교육 모형이며 실제 C#/WPF/GPU를 실행하는 것으로 표현하지 않는다. 2배 가상 DIP 화면·선택 사각형1개·이미지 경계 제한을 명시하고 Shift/다중 선택 등 생략 범위를 설명했다.

모델은 Up의 마지막 위치 재계산,2DIP 미만 이동 무시, 실제 변경 때만 이력 증가, 취소 시 미리보기 제거를 표현한다. UI는 포인터 캡처/해제·취소·Escape·창 비활성화·페이지 비표시에서 수명을 처리한다. 캡처 해제 전에 활성 포인터를 비워 재진입 취소가 확정을 덮지 않게 했다. 자동 재생은 사용자 시작만 허용하고 정지 가능하며 움직임 줄이기 설정을 존중한다. 정적 그림과 기존 시퀀스는 유지한다.

- PASS:모델/DOM 대역37개 확인. 마지막 Up·취소·무변경·시작점 복귀·연속 드래그·경계·직접 포인터/캡처·Escape·재생/정지·blur·움직임 줄이기 검증. SVGPoint처럼 x/y가 프로토타입 접근자인 좌표도 명시적 성분 읽기로 확인.
- PASS:단계별 실제 C# 발췌4개가 현재 파일의 연속 부분과 일치. 색상 전후 문자 보존. 기존 실제 소스55개·75개 코드창·학습 JS25개·MathML510개 회귀 확인.
- PASS:HTML11개/로컬 참조298개, 새 JS 문법·정적 대체 표시·키보드 제어 구조 확인.
- NOT_RUN:실제 브라우저 화면/포인터·터치·보조공학·인쇄. 기존 file URL 도구 제한을 우회하지 않았다. 교육 모형과 실제 앱의 모든 입력 경로 동등성, 독자의 학습 효과도 미검증이다. 제품 코드나 의존성은 변경하지 않았다.

검사 도구는 `artifacts/manual-refactor/check_roi_drag.py`와 `check_roi_drag.cjs`다. 제품 성능/FPS 결과로 사용하지 않는다.

## 2026-10-14 후속: 읽기 순서와 구체적인 사례

독자가 내용을 따라가기 어렵다는 피드백에 따라 1~9장의 진입을 상황 하나 → 기억할 문장 → 세 단계 → 결과 예상/풀이 → 본문 세 곳 순서로 바꿨다. 2장의 드래그 거리 계산을 5장의 확정/취소와 7장의 표시로 이어 읽는다. 기호표를 맨 앞에서 빼고, 용어/확장 설명19곳을 필요할 때 펼치도록 했다. 수식 바로 옆의 기호 설명과 기존 코드·수식은 보존했다.

기존 본문 순서도 변경했다. 입력 우선순위와 그리기/취소 설명을 기본 드래그 뒤로 옮겼고, 9장은 Foundations → InputLifecycle → CanvasHost 순서로 읽도록 배치했다. 뒤늦게 추가된 5장 소스가 pager/footer 뒤에 있던 배치도 본문 안으로 모았다. 반복되는 장말 읽기 지시는 줄였다. 코드가 참고 설명 안에 있어도 참조 클릭·해시 이동으로 바깥 접기까지 열린다.

- PASS:9개 시작 사례·27단계·27개 읽기 대상·19개 기본 접힘 참고 설명의 구조와 대상 확인. HTML11개/로컬 참조297개 검사 통과.
- PASS: 기존 실제 소스55개 일치·75개 코드 색상 전후 문자 보존. 접힌 참고 설명 안의 소스 열기를 포함한 DOM 대역 동작 확인. 기존 학습 JavaScript25개, MathML510개/독립 수식20개/주요 전사6개 유지.
- NOT_RUN: 실제 브라우저 배치·키보드/인쇄/보조공학, 신입 독자의 회상·독립 구현 평가. 이전 file URL 도구 제한을 우회하지 않았다. 내용을 재배치한 사실을 학습 효과가 입증된 것으로 표현하지 않는다. 제품 코드 변경 없이 문서와 표시 스크립트만 수정했다.

## 2026-10-14 후속: 소스 파일 링크 자체를 본문 코드창으로 교체

앞선 코드창 스타일 변경은 기존 본문 예제만 처리해 `RenderWorker.Run · Dx12Surface · ScenePainter` 등 원본 파일 링크가 남았다. 사용자의 정정에 따라 전체 소스30개를 설명 자리의 접이식 코드창으로 추가했다. 기존 전체 코드가 있는 파일은 해당 코드창을 재사용한다. 메타데이터의 자기 파일 링크는 경로 텍스트로 바꾸고, 나머지 소스 참조21개는 페이지 안의 코드창을 열도록 연결했다. 직접 `.cs`/`.csproj`/`.hlsl`/`.xaml`로 이동하는 링크는0개다. 셰이더 HLSL 어휘 색상도 추가했다. 코드는 HTML에 저장해 file URL에서 fetch 없이 읽는다.

- PASS: HTML11개·로컬 참조270개·모든 본문 소스 참조 대상 존재. 코드창75개 기본 접힘·제목·복사/상태 표시 구조 확인.
- PASS: 실제 파일과 본문 소스55개 일치. 색상 처리 전후75개 코드 문자열의 모든 문자 보존. MathML510개와 기존 학습 JS25개 회귀 통과.
- PASS: DOM 대역에서 소스 참조 클릭 시 코드창 열기·summary 초점·해시 이동·보조키 클릭 보존·복사 성공/실패 확인. 기존 첫 펼침 색상과 재펼침 중복 방지 유지.
- NOT_RUN: 실제 브라우저 표시·키보드·클립보드·인쇄. 기존 file URL 보안 제한을 우회하지 않았다. 생산 코드/의존성/동작 변경이 없어 제품 빌드와 FPS는 재실행하지 않았다.

이 후속은 소스 참조의 표시 책임만 변경한다. 기존 C# 설계와 소스 내용을 보존하며 원칙 예외는 없다. 이하45개/25개 등의 수치는 이전 검증 시점의 이력이다.

## 2026-10-14 후속: 본문 안의 접이식 코드창

사용자 요청에 따라45개 코드/명령/다이어그램 원문을 기본 접힘 코드창으로 통일했다. 언어·제목·파일명·줄 수를 보여 주고 C#/XML 색상·복사 버튼을 추가했다. 원본 링크는 보조 정보로 옮겼다. 기존 native details의 키보드 동작과 JavaScript 없는 열람을 유지한다. 코드 색상은 처음 펼칠 때만 적용하고, 긴 코드만 내부 스크롤한다. 새 외부 패키지나 CDN은 없다. 제품 C# 코드는 수정하지 않았다.

- PASS: HTML11개·로컬 참조299개.45개 기본 접힘·summary·복사 버튼·상태 안내·코드 키보드 초점의 정적 구조 확인.
- PASS: 코드창45개의 색상 처리 전후 모든 문자 일치. 기존 현재 소스25곳 일치, MathML510개/독립 수식20개 및 전사6개, 기존 학습 JS25개 재검증.
- PASS: DOM 대역으로 첫 펼침 처리·재펼침 중복 방지·복사 성공·권한 거부·클립보드 API 없는 환경의 선택/직접 복사 안내 확인. 새 JS 문법 검사 통과.
- NOT_RUN: 실제 브라우저 시각 배치·클립보드·키보드·인쇄/보조공학. 앞선 로컬 file URL 보안 제한을 우회하지 않았으며 DOM 대역 검사를 실제 화면 검증으로 표현하지 않는다. 신입 독자 평가와 제품 빌드/FPS 재실행도 이번 범위에 포함하지 않았다.

코드 원문은 HTML에 보관하고 표시/복사는 작은 별도 스크립트로 분리했다. 전역 서비스나 제품 도메인 계약 변경 없이 기존 코드와 수식 내용을 보존했다. 로컬 확인 도구는 `artifacts/manual-refactor/check_code_windows.py`와 `check_code_windows.cjs`이며 외부 빌드 의존성으로 추가하지 않았다.

## 2026-10-14 후속: 클래스 그림 분리와 폐기 파일 정리

사용자가 읽기 어려운 클래스 그림 개선과 불필요한 매뉴얼 파일의 즉시 삭제를 요청했다. 아래 개편 당시 남겨 둔 `manual/10-review.html`, `manual/11-refactoring.html` 안내 파일과 교체된 `editing-classes.svg`, `editing-classes.mmd`를 삭제했다. 사용 중인 HTML의 참조를 먼저 제거했으며 한 명시적 파일씩 삭제했다. 이전 URL은 더 이상 제공하지 않는다.

4장의 한 그림을 입력 연결4타입·제스처 상속3타입·문서 구조6타입의 세 그림으로 분리했다. 같은 타입의 재등장을 설명하고 각 그림의 읽는 방향, 참조·상속·인터페이스 구현·이력 소유 관계와 생략 범위를 본문에 적었다. 선은 직각 또는 직선이며 서로 교차하지 않는다. 멤버 글자는18px, 클래스 이름은21px의 SVG 기준 크기다. 클래스 그림에만 반응형 폭을 적용해 기존1100px 강제 폭을 제거했다. 모바일에서는 그림이 축소되므로 동일한 관계를 본문 목록으로도 읽을 수 있다. ROI 시퀀스3개의 기존 배치는 유지했다.

- PASS: HTML11개·로컬 링크/자산288개, 중첩·ID·앵커·외부 실행 자산 검사. 삭제한 파일을 가리키는 학습 페이지 참조 없음.
- PASS: 새 SVG3개 XML,65개 글자의 가로 경계 검사(Segoe UI·맑은 고딕 글꼴 측정). 이는 실제 브라우저 렌더링 검증이 아니다.
- PASS: 본문25개 코드와 현재 소스 일치, MathML510개/독립 수식20개 구조·주요 전사6개, 학습 JavaScript25개 검사 재실행.
- NOT_RUN: 실제 브라우저 배치·인쇄·보조공학. 브라우저 도구가 file URL 접근을 보안 정책으로 거부했으며 우회하지 않았다. 실제 신입 독자의 이해도 평가도 미실행이다.
- NOT_RUN: 제품 빌드·실행 재검증. 이번 후속은 HTML·CSS·SVG·문서만 수정했으며 앞선 제품 검증을 새로 실행했다고 주장하지 않는다.

가정은 기존 학습 내용과 오프라인 실행을 유지하는 것이다. 생산 클래스·계약을 변경하지 않았으므로 SOLID5·GRASP9의 설계 변경 검토는 해당 없음이며, 도식의 책임·소유·의존 관계를 실제 코드와 대조했다. 새 패키지나 원칙 예외는 없다. 결과는 JSON의 `diagramReadabilityFollowup20261014`에 기록한다. 이하 수치와 파일명은 이전 검증 시점의 이력이다.

## 2026-10-14 현재 코드와 구현 흐름 중심 개편

사용자가 코드 리팩터링 뒤 매뉴얼 개선을 재요청했다. 기존10장 리뷰/디버깅/성능과11장 작업 검증을 목차·앞뒤 이동·본문에서 제외했다. 두 URL에는 안내만 남겨 이전 링크가 갑자기 깨지지 않게 했다. 현재 학습 경로는 첫 화면·1~9장·부록11페이지이며 안내2개를 포함한 물리 HTML은13개다.

4장은 핵심 클래스 관계를, 5장은 마우스 Down/Move/Up·선택 우선순위·초안·미리보기·확정/취소·메인 앱의 비동기 준비를 설명한다. 클래스 관계도1개와 ROI 시퀀스3개는 오프라인 SVG이며 Mermaid 원문도 본문과 파일로 제공한다. 작은 동기 호스트와 콜백을 주입하는 실제 앱을 구별한다. 7장은 물리 픽셀→DIP→원본 좌표, 캡처/포커스/휠과 WndProc 실제 코드를 추가했다. 6장은 NetVips 코드와 .v 설명을 현재 NativeRasterDecoder·RawPyramidSource·TrianglePyramid 경로로 갱신했다.

기호·단위·코드 대응 표를 장 앞과 주요 수식 바로 아래에 두었다. 본문 손계산도 MathML로 표시하고, 동적 실험 출력은 기호식을 평문으로 조립하는 대신 자연어 결과로 설명한다. 새 설명에는 쉬운 비유와 한계, 단계별 구현, 결과 예측, 자기 설명 문제와 풀이를 연결했다. 9장에는 InputLifecycle 콘솔 실습과 현재 API로 컴파일한 사각형/원 그리기 버튼 예제를 추가했다.

| 상태 | 검증 | 범위 |
|---|---|---|
| PASS | HTML13개·로컬 링크/자산319개 | 중첩·ID·앵커·외부 실행 자산 없음·제외 장 링크 없음 |
| PASS | 실제 소스25곳 | 전체 파일 또는 연속 발췌가 현재 파일과 일치. 교육용 추가 코드는 별도 표기 |
| PASS | MathML510개, 독립 수식20개 | 기호 표의 단일 기호 포함. XML/namespace/자식 수/행렬/접근성 설명과 임계값·Sobel·회전·NCC·LOD·합 범위6개 전사 확인 |
| PASS | 내용47개 확인 | 대표 산술10개, 장 링크·옛 코드 제거, SVG 구조/제목, 클래스·시퀀스 원문, 기존 실험5개 유지 |
| PASS | 학습 JavaScript25개 | 변경된 목차10개 카드·결과 문구와 기존 실험 계산. DOM 대역 검사이며 실제 브라우저 배치가 아님 |
| PASS | Foundations11개 + InputLifecycle8개 | 좌표·메모리·계층·이력과 마지막 포인터·변환 미리보기·확정·Undo/Redo·취소를 실제 실행 |
| PASS | CanvasHost 및 버튼 추가 예제 | 현재 참조로 Release 빌드0경고/0오류. 새 InputLifecycle 코드의 공백/CRLF 검사 통과 |
| NOT_RUN | 실제 브라우저·인쇄·보조공학 | 이전 file URL 도구 제한이 있었으며 이번 정적 검사로 실제 표시 품질을 보장하지 않음. SVG 구조 확인도 시각 평가와 구별 |
| NOT_RUN | 신입 학습 수행 평가 | 독자가 설명을 덮고 재설명하거나 독립 구현하는 실제 평가가 필요. 분량 증가나 실행 성공이 이해도 입증은 아님 |
| NOT_RUN | 제품 전체 회귀·FPS | 생산 코드 변경 없는 문서/학습 예제 작업. 앞선 ROI 리팩터링 검증과 구별 |

초기 CanvasHost 복원은 NuGet 취약성 정보 네트워크 조회 오류로 실패했다. 감사/경고 설정을 끄지 않고 일반 권한의 정상 복원·빌드로 통과했다. 교육용 도형 버튼의 잘못된 Ellipse 이름은 실제 API의 Circle로 수정 후 다시 컴파일했다. 타원 마스크와 원 ROI를 구별한다.

결과 요약은 [JSON](beginner-manual-results.json)의 `implementationGuide20261014`, 로컬 확인 도구/원시 결과는 `artifacts/manual-refactor`에 보관한다. 이 기록은 학습 본문에서 제외한 작업 검증 책임을 docs에 유지한다. 원칙 검토: SRP·High Cohesion은 장별 책임으로, Information Expert는 기하/문서/버퍼 소유자로, Controller는 입력 전달 경계로 설명했다. OCP·LSP·ISP·DIP, Creator·Low Coupling·Polymorphism·Pure Fabrication·Indirection·Protected Variations는 기존 생산 계약·생성·결합·확장 경계를 유지하고 본문에서 실제 코드와 대응했다. 생산 설계 변경이나 성능 원칙 예외는 없다. 로컬 수정·검증만 수행했다.

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


## 2026-10-13 후속: 파인만 방식의 설명과 자기 설명 연습

수식 조판만으로는 초보자가 계산 이유를 이해하기 어렵다는 사용자 피드백을 반영했다. HTML13페이지에서 38개 절을 문제 제기·쉬운 비유·기호/단위·중간 계산·잘못 계산한 예로 보강했다. 첫 화면은 쉽게 설명하기 → 빈틈 찾기 → 작은 예로 확인하기 → 다시 설명하기 순환을 안내한다. 1~12장에는 답을 보기 전 자기 말로 설명할 상황, 기본으로 접힌 설명 예시, 막힌 경우 돌아갈 절을 넣었다. 단순히 비유를 읽는 것과 숫자를 바꾸어 설명할 수 있는 것을 구별한다.

주요 보강은 stride의 행/칸 이동, 확대 역변환의 순서, 고정점 확대 식의 유도, 회전 행렬의 행별 계산, 평균·Gaussian·Sobel·보간·마스크, 타일의 올림/내림·제외 끝·가장자리·DPI/LOD 로그·피라미드 면적 합, NDC, NCC의 편차 표와 분자/분모, RMS·IoU, 프레임 간격 평균의 반례다. 설계·이력·GPU 수명도 실제 요청과 실패 상황으로 풀었다. 비유의 한계, 표시용 Linear와 검사 면적 평균, 원본/화면 단위, 취소·세대·소유권·fence, 기존 성능 미달을 보존했다.

| 상태 | 검증 | 결과와 범위 |
|---|---|---|
| PASS | 숫자 예제 | 27묶음의 독립 산술 계산 및 해당 설명의 HTML 존재 확인 |
| PASS | 기존 자료 보존 | 기준 커밋800bcaf와 MathML47개·실제 코드16곳 내용 동일, 상호작용 실험5개 유지 |
| PASS | HTML 및 코드 | 13페이지·로컬 참조335개 정적 구조/링크, 실제 파일과 본문 코드16곳 일치 |
| PASS | MathML | 47개 구조와 기존 주요 전사6개 확인 |
| PASS | 학습 JavaScript | 기존 DOM 계약25개 통과. 실제 브라우저 이벤트 실행과 구별 |
| BLOCKED | 실제 브라우저·인쇄 | 기존 file:// 도구 정책 제한 유지. 우회하지 않았으며 실제 수식 배치·인쇄·보조공학 결과는 미검증 |
| NOT_RUN | 신입 독자 학습 평가 | 실제 독자의 자기 설명·입력 변경 문제·독립 구축 수행은 후속 확인 필요. 문서 확장을 학습 효과 입증으로 표현하지 않음 |
| NOT_RUN | 제품 빌드·회귀·FPS | 생산 C#·실습 코드·JS가 바뀌지 않은 문서 후속이므로 반복 실행하지 않음 |

검증 자료는 무시 대상인 `artifacts/manual-refactor/verify_feynman.py`, `feynman-check.json` 및 기존 HTML/MathML/소스/JS 검증 도구에 있다. 결과 요약은 [JSON](beginner-manual-results.json)의 `feynmanExplanationFollowup`에 남겼다. 문서 설명과 CSS만 바꾸었으며 제품 실행 경로에 연산·할당·의존성을 추가하지 않았다.

SOLID5·GRASP9 검토: SRP·High Cohesion은 각 개념의 풀이와 공통 표시 스타일을 분리해 유지했다. Information Expert는 기하/타일/문서의 기존 계산 소유자를 설명에 연결했다. OCP·LSP·ISP·DIP는 생산 확장 지점·대체 계약·인터페이스·의존성에 변경이 없다. Creator·Controller·Low Coupling·Polymorphism·Pure Fabrication·Indirection·Protected Variations도 생성·제어·결합·변형·기술 책임·연결 경계를 바꾸지 않았으며, 본문에서 해당 책임과 실패 조건을 설명했다. 해당 없음으로 기존 위반을 숨기거나 원칙 예외를 적용한 작업은 없다.
