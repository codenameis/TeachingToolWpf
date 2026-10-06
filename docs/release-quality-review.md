# 교육·릴리즈 전 코드 품질 점검 — 2026-10-05~06

기준은 `main`의 `d8b6b226fe56c5bb99f32b99428ee1311ee51bf6`과 이번 로컬 수정이다. 성능 구현을 보존하면서 컨벤션, 오류 경계, 자원 수명, 교육 문서와 배포 구성을 검토했다. 이번 점검은 커밋·푸시·릴리즈 태그 생성을 포함하지 않는다. 작업 전부터 존재한 미추적 `dotnet-anti-slop/`는 수정하지 않았다.

## 수정한 항목

| 항목 | 문제와 수정 | 검증 범위 |
|---|---|---|
| C# 컨벤션 | 기존 6개 파일의 중괄호·들여쓰기·공백과 테스트 수식 2개의 괄호를 저장소 규칙에 맞춤. 선택 전체 준비의 람다를 명시적 분기로 정리 | 전체 `Format-Code.ps1 -Verify`, 기존 회귀 |
| 반복문 가독성 | 57개 파일의 117개 `for` 헤더에서 조건·증감식이 왼쪽 끝으로 분리되던 형태를 정리 | Roslyn 토큰 종류·문자열 비교로 해당 수정 전후 코드 토큰과 리터럴 동일 확인. 생성·외부 코드는 제외 |
| ROI 목록 | 깊은 유효 계층에서 재귀 호출 스택이 고갈될 수 있었음. 깊이별 형제 열거자를 명시적 스택으로 관리 | 깊이 30,000, 전위 순서·형제 순서·Depth·Kind, 기존 취소/목록 회귀 |
| 렌더 재시작 | 중복 `RestartAsync`가 서로 다른 worker를 시작하거나, 재시작 중 HWND가 worker보다 먼저 파괴될 수 있었음 | 동시 요청이 같은 Task를 공유하고 실제 Dispose는 이전 worker 종료 이후 HWND를 파괴. 실제 WPF/WARP 테스트 3개 |
| GPU 초기화 실패 | 파이프라인·타이머 생성 도중 실패하면 앞서 생성한 root signature·descriptor heap·query heap이 남을 수 있었음 | 생성자 소유권 경로 정적 검토. 업로드 Map/등록 실패도 자원을 회수하고 원래 예외를 전파 |
| 상대 캐시 경로 | 상대 경로의 staging에서 드라이브 여유 공간 검사에 빈 root가 전달될 수 있었음 | 캐시 경로를 진입점에서 절대 경로로 정규화. 실제 PNG 최초 열기·재열기·원본 경계 픽셀 검증 |
| 비동기 명령 | 시작 상태 알림의 구독자 예외로 실행 중 플래그가 복구되지 않을 수 있었음 | 상태 알림도 try/finally 안에서 실행. 같은 예외 전파·재실행 가능 여부의 실패→수정→성공 회귀 |
| 교육·배포 문서 | 분리 전 Core 경로, CUDA 책임, 폴리곤 제한과 과거 성능 결과가 현행 설명과 섞여 있었음 | 현재 프로젝트 경계·GPU 후보 계산·지원 도형·런타임 요구사항으로 정정하고 과거 실측과 현재 FAIL을 구분 |

`unsafe`/Span, 불변 스냅샷과 공간 블록 identity, GPU 페이지 배치·fence 이후 lease 반환, CPU/GPU 캐시, 풀 버퍼와 SIMD는 유지했다. 읽기 쉽게 만든다는 이유로 매 프레임 복사·할당·중계 계층을 추가하지 않았다. 성능상 설계 원칙 예외를 새로 적용하지 않았다.

## 검증 결과

작업 로그는 `artifacts/final-quality-review`에 두고, 재검토용 로그·진단 목록·캡처는 [증거 ZIP](data/release-quality-review-20261005.zip)에 보관한다. 최종 실행 결과는 아래와 같다.

| 검사 | 상태 | 근거 |
|---|---|---|
| 전체 Release 빌드 | PASS | `build-final.log`, 경고 0 / 오류 0 |
| 전체 회귀 | PASS | `tests-final.log`, 1,046개 통과 / 실패 0 / 건너뜀 0. Core 232 / 앱 249 / Canvas 158 / Algorithm 248 / Inspection 107 / Compute 52 |
| 전체 명명·서식·style | PASS | `format-final.log`, `layout-verify.log`. 기존 전체 서식 검사 실패를 해소 |
| WPF/WARP 재시작·Dispose | PASS | `viewport-lifetime.log`, 3개 |
| 알려진 NuGet 취약점 조회 | PASS(조회 범위) | `dependency-audit.json`, 전이 패키지 포함 20개 프로젝트에 취약 패키지 보고 없음. 네이티브 코드 전체 보안 감사를 뜻하지 않음 |
| 문서 링크·diff | PASS | `document-links.log`, 6개 문서의 상대 링크 190개 존재 확인. `git diff --check` 통과 |
| 선택형 .NET 분석기 감사 | FAIL | `analyzer-summary.json`, `analyzer-diagnostics.json`, `analyzers/*.log`. 고유 진단 196개: 오류 40 / 경고 156 |
| 일반 배포 폴더 | PASS | `publish.log`, `artifacts/final-quality-review/publish`. native DLL·셰이더·샘플 이미지·라이선스 고지 포함 |
| 배포본 실제 DX12 실행 | PASS | 독립 작업 폴더 `smoke-gpu`에서 `--smoke --gpu-debug`, RTX 3060 Laptop / Debug: True / exit 0. 편집·계층·그룹·Undo/Redo·렌더 재시작·OpenCV 필터·8K 라이브 시작/종료 통과. 로그에 D3D12 경고/오류·렌더 오류 없음 |

초기 WPF 종료 테스트는 `Window.Close()`를 HwndHost의 동기 Dispose로 잘못 가정하여 실패했다. [WPF HwndHost 소스](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Interop/HwndHost.cs)와 실제 실행을 확인하여 `HwndHost.Dispose()`와 부모 창을 먼저 닫은 경우를 각각 검사하도록 고쳤다. 테스트의 worker 종료·HWND 순서 기준은 유지했다. GPU 메모리 고갈이나 네이티브 생성 실패를 강제로 주입하는 시험은 수행하지 않았다.

배포본 smoke가 생성한 앱 캡처에서 원본·ROI 윤곽·회전 핸들·한글 라벨·목록/속성 표시를 확인했다. 자동 smoke는 가시창의 물리 입력 지연/60FPS 수용 시험이 아니다. 배포 스크립트가 복사한 과거 `publish/artifacts` 로그와 이번 `smoke-gpu/artifacts` 결과는 구분했다.

## 선택형 분석기 진단의 해석

일반 빌드와 `EnableDotNetAntiSlop=true` 감사는 다른 검사다. 감사는 기존 정책을 완화하거나 억제를 추가하지 않고 수행했다. 솔루션 감사가 선행 프로젝트 오류로 중단되어, 일반 빌드 참조를 준비한 뒤 20개 프로젝트를 `BuildProjectReferences=false`로 개별 감사했다. 파일·행·열·규칙·심각도가 같은 중복 출력을 제거한 수치다. 196개 진단 전체가 196개 실제 결함이라는 뜻도, 전부 오탐이라는 뜻도 아니다.

대표 경로를 검토한 결과 다음 구현은 계약상 유지한다.

- `AsyncCommand.Execute`의 `async void`: WPF `ICommand.Execute`의 void 계약을 잇는 경계다. 작업은 Task 반환 `ExecuteAsync`로 분리하고 호출 경계에서 오류를 전달한다.
- 취소 토큰 필드와 `using` 지역 소유자: 필드는 진행 중 요청을 취소하기 위한 참조다. 작업 종료를 기다리는 지역 소유자보다 먼저 토큰을 Dispose하면 안 된다.
- 제한된 crop worker의 종료 대기: reader·장치·버퍼를 반환하기 전에 이미 시작한 작업을 회수해야 한다. 정리 continuation의 `CancellationToken.None`과 완료 확인 이후 결과 조회를 일반 UI 블로킹과 구별한다.
- `RoiPaste`의 ROI별 이름 생성: 각 이름을 한 번 연결하는 코드이며 누적 문자열 확장이 아니다. 일괄 StringBuilder 교체 근거가 없다.
- 픽셀 버퍼의 소유권 이전: 반환 객체/프레임으로 넘긴 버퍼를 지역 `using`으로 먼저 해제하면 소비 중인 데이터를 깨뜨린다. 실제 실패 회수 경로를 확인해야 한다.

남은 실제 개선 후보는 공개 API의 null 경계(`CommitPrepared`, `EditorSession.Paste`, `RoiClipboard.Copy`, 기하 포함 검사 등 CA1062)와 `RoiTransforms` 경계 탐색에서 `ImmutableArray<Guid>`가 `IEnumerable<Guid>`로 전달되는 작은 boxing이다. 전자는 예외 계약 명확화, 후자는 별도 할당 실측과 구체 타입 경로 검토가 필요하다. **성능을 이유로 null 검증 누락을 정당화하지 않는다.** 이번에는 API 전체 변경이나 분석기 일괄 억제를 섞지 않았으며, 선택형 감사 완료는 선언하지 않는다. 대표 사례 외의 진단별 처분도 남아 있다.

## 설계 원칙 검토

| 원칙 | 이번 변경의 판단 |
|---|---|
| SRP | 목록 순회는 목록 생성자, 재시작은 viewport, GPU 실패 회수는 GPU 소유자에서 처리 |
| OCP | 기존 소스/도구 확장 계약 유지. 새 구현 분기나 공급자 요구를 Core에 추가하지 않음 |
| LSP | 기존 취소·소유권·순서 유지. WARP 대체 경로의 실제 수명 회귀 검사. 공개 null 경계 진단은 잔여 항목으로 명시 |
| ISP | 공개 인터페이스 확대 없음. 테스트 때문에 생산용 수명 인터페이스를 만들지 않음 |
| DIP | 순수 기하·영상 계약·편집 Core·표시·검사 참조 방향 유지. 새 서비스 로케이터나 의존성 없음 |
| Information Expert | ROI 순서는 목록 생성이, native 파괴 순서는 HWND/worker 소유자가 결정 |
| Creator | 생성 실패 전에 확보한 GPU 자원을 생성 책임자가 반환 |
| Controller | 명령 실행 상태와 렌더 수명만 해당 조정자에서 처리. 알고리즘을 UI에 옮기지 않음 |
| Low Coupling | 파일 경로 정규화는 이미지 로더 내부. 검사/Canvas 사이 새 참조 없음 |
| High Cohesion | 기존 타입 안에서 해당 실패 경로만 수정. 공용 유틸리티/관리자 추가 없음 |
| Polymorphism | 기존 decoder·frame·viewport 계약 유지. 새 변형을 도입하는 작업은 아님 |
| Pure Fabrication | 기존 GPU arena·캐시·이력의 기술 책임과 성능 배치를 보존 |
| Indirection | 기존 호스트 조립/어댑터를 유지. 추가 중계 계층 없음 |
| Protected Variations | WPF·DX12 native 수명은 Canvas, NetVips 경로는 Imaging 안에 유지 |

## 교육과 릴리즈에 남은 범위

현재 코드 읽기 순서는 [코드 안내](code-guide.md), 표기 규칙은 [C# 컨벤션](csharp-code-conventions.md)을 따른다. 기능 교육에는 구현한 동작과 미검증 범위를 함께 설명한다.

- 전체 작업 중 2D 60FPS는 기존 [그룹·회전 검증](group-freeze-validation.md)의 **FAIL** 상태다. 이번 품질 점검은 새 성능 달성 증거가 아니다.
- CPU 125ms / CUDA 80ms 형상 검사 목표는 [별도 검사 검증](paper-shape-125ms-validation.md)의 **FAIL**이며 화면 프레임 예산과 섞지 않는다.
- 실제 사용자 문서·물리 입력/모니터 표시·장시간 편집·별도 저사양 PC·깨끗한 새 Windows 설치 환경은 이번에 검증하지 않았다.
- 따라서 컨벤션 통과와 기능 회귀 통과를 성능 보장 릴리즈 또는 결함 없음의 보증으로 표현하지 않는다. 공개 배포 전 진단별 잔여 검토와 위 사용 환경 검증이 필요하다.

## 재현 명령

```powershell
dotnet build TeachingToolWpf.slnx -c Release --no-restore
dotnet test TeachingToolWpf.slnx -c Release --no-build --no-restore
pwsh -NoProfile -File scripts/Format-Code.ps1 -Verify
dotnet list TeachingToolWpf.slnx package --vulnerable --include-transitive --no-restore --format json
dotnet build TeachingToolWpf.slnx -c Release --no-restore -p:EnableDotNetAntiSlop=true
pwsh -NoProfile -File scripts/Publish-Sample.ps1 -OutputDirectory artifacts/final-quality-review/publish
```

감사 명령의 실패를 일반 빌드 실패로 해석하거나 성공으로 바꿔 기록하지 않는다. 선행 프로젝트 오류에 막히지 않고 이번처럼 20개 프로젝트의 진단을 수집하려면 일반 Release 빌드 이후 아래 명령을 사용한다. 기존 경고 정책은 유지하며, 선행 프로젝트는 앞서 만든 일반 빌드 참조를 사용한다.

```powershell
dotnet build .dotnet-anti-slop/analyzer/DotNetAntiSlop.Analyzers.csproj -c Release --no-restore
[xml]$solution = Get-Content -LiteralPath TeachingToolWpf.slnx -Raw
foreach ($project in $solution.SelectNodes('//Project')) {
    dotnet build $project.Path -c Release --no-restore -p:EnableDotNetAntiSlop=true -p:BuildProjectReferences=false
}
```

프로젝트별 성공/실패는 개별 로그로 확인한다. 마지막 프로젝트의 exit code만으로 전체 감사 성공을 판단하지 않는다.
