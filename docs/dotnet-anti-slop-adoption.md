# dotnet-anti-slop 도입 기록

2026-10-05 사용자 요청: 기존 AGENTS.md를 유지하고 중복을 제외한 .NET 규칙만 병합했다. 제품 코드는 변경하지 않았다.

## 설치

- 공식 출처: https://github.com/devmobasa/dotnet-anti-slop
- 고정 revision: `6d4188203bbc6dfdee6159204b8b73b76e5ee4ff`, 버전 `0.1.0-preview.1`.
- 전역 스킬: `~/.codex/skills/install-dotnet-anti-slop`. 다음 턴부터 사용 가능하다.
- 프로젝트 분석기: `.dotnet-anti-slop/analyzer`, MIT, netstandard2.0. Microsoft.CodeAnalysis.CSharp 4.8.0 및 Microsoft.CodeAnalysis.Analyzers 5.6.0은 빌드 전용이며 앱 런타임 의존성이 아니다.
- SDK: 10.0.401. upstream default 프로필과 프로젝트별 `build/DotNetAntiSlop.globalconfig`를 사용한다. ASP.NET/Blazor/EF 진단만 제외했다.
- 출처 메타데이터 경고: upstream 스킬 manifest와 설치 계산값이 불일치하여 INSTALLATION.md는 `unversioned-source` / `modified-skill`로 기록되었다. 이를 정상 revision으로 덮어쓰지 않았다. 위 공식 revision의 스킬 assets와 설치된 assets를 줄바꿈 정규화 후 비교한 차이는 0개다. 설치 내용 SHA-256은 `008080132b0b87801e6b8497fed2d657843cd69abb4a198a8f6be171860d288a`다.

## 충돌·중복 처리

| 추가 지침 | 기존 프로젝트 우선 처리 |
|---|---|
| 동기 대기 금지 | UI/일반 비동기 경로에 적용한다. 기존 worker 종료·네이티브/GPU 수명 경계는 일괄 비동기화하지 않는다. GPU fence와 Task 대기를 구별한다. |
| 반복 열거의 구체화 | 한 번 순회를 우선하며 crop·타일·풀 버퍼 예산을 넘는 일괄 배열화는 하지 않는다. 조건부 적용 범위 조정이다. |
| hosted service·DI scope 기반 백그라운드 작업 | 웹 전용 부분은 제외하고 WPF의 명시적 worker·Dispatcher·장치 수명을 유지한다. |
| 기본 분석기 활성화·경고 없는 빌드 | 기존 Core의 TreatWarningsAsErrors와 새 진단이 결합해 오류 40개가 발생했다. 엄격도를 낮추거나 대량 억제하지 않고 명시적 감사 모드로 설치했다. 자동 강제 적용은 미완료다. |
| 일반 스타일 적용 범위 | 자체 수정 코드에 적용하며 생성/공급자 코드는 일괄 수정하지 않는다. |

취소 토큰, 이벤트 외 async void 금지, 예외 무시 금지, 제한된 병렬 실행, 자원 소유권, 측정 우선 최적화, nullable·스타일·테스트 원칙은 기존 규칙과 중복되어 재기록하지 않았다. 새 항목은 Task delegate, ValueTask 단일 소비, TaskCompletionSource 비동기 continuation, 문자열/boxing/열거 비용, 존재 조회·초기 용량, 비동기 테스트 반환형, 국소 억제 근거다. 웹·EF 및 데이터베이스 전용 테스트 지침은 병합하지 않았다.

## 검증과 실행

추가 AGENTS 지침은 항상 적용한다. 분석기는 명시적으로 실행한다:

```powershell
dotnet build TeachingToolWpf.slnx -c Release -p:EnableDotNetAntiSlop=true
```

- PASS: 추가 섹션만 제거한 AGENTS 내용과 설치 직전 백업이 동일하다(줄바꿈 정규화). 사용자가 추가한 일반 지침과 루트 `dotnet-anti-slop/` 폴더도 보존했다. 이 폴더는 빌드용 `.dotnet-anti-slop/`와 별개다.
- FAIL: 초기 분석기 전체 감사에서 오류40/경고56. Core의 CA1062 38개, DAS1006 1개, DAS1010 1개가 오류다. Core 실패로 모든 의존 프로젝트 진단이 수집된 것은 아니다. `artifacts/dotnet-anti-slop-adoption/initial-build.log`에 기록했다. 설치 완료는 기존 진단 해결 완료가 아니다.
- PASS: 격리된 probe의 async void가 DAS1003 오류로 검출됐다. Task 반환으로 수정 후 경고0/오류0으로 통과했다. probe는 artifacts에만 존재하며 제품에 포함되지 않는다.
- PASS: 일반 전체 Release 빌드 및 기존 6개 테스트 프로젝트 검증. 상세 수치는 아래 최종 로그를 따른다.

검증 명령은 `dotnet test TeachingToolWpf.slnx -c Release --artifacts-path artifacts/anti-slop-validation -m:1 -v:minimal`, `dotnet build TeachingToolWpf.slnx -c Release --artifacts-path artifacts/anti-slop-validation --no-restore -m:1 -v:minimal`이다. 로그는 `artifacts/dotnet-anti-slop-adoption/{validation,build,probe,probe-fixed}.log`에 있다. 첫 제한 환경 실행은 SDK 경로 접근 권한 오류였으며 권한을 갖춘 재실행으로 검증했다. 실행 중인 앱은 중단하지 않았다. 검사 정확도/125ms 성능이 개선됐다는 의미가 아니다.

INSTALLATION.md의 Force 갱신 명령을 무조건 실행하지 않는다. 내용 비교와 기존 삭제 안전 지침을 먼저 따르고 감사 모드 조건 및 웹/EF 제외 설정을 재검토한다.

## 설계 검토

SOLID: SRP는 빌드 정책과 제품 동작 분리, OCP는 선택형 import, LSP는 런타임 대체 구현 변경 없음, ISP는 공개 API 변경 없음, DIP는 제품 런타임 의존성 추가 없음으로 확인했다.

GRASP: Information Expert·Creator·Controller는 도메인/생성/업무 제어 변경 없음, Low Coupling·High Cohesion은 별도 빌드 설정, Polymorphism은 공급자 구현 변경 없음, Pure Fabrication은 빌드 도구 격리, Indirection은 MSBuild import, Protected Variations는 upstream 원본과 프로젝트 override 분리로 검토했다. 설계 원칙 예외를 추가하지 않았다.
