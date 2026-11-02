# TeachingToolWpf — 프로젝트 장기 메모

## 빌드·검사 게이트 (2026-10-19 확인)

- 스타일 게이트는 `pwsh -File scripts/Format-Code.ps1` / `-Verify` 하나뿐이다(`dotnet format whitespace` + `style --verify-no-changes`, IDE1006·IDE0003·IDE0011·IDE0047·IDE0048·IDE0161·IDE0065).
- `EnforceCodeStyleInBuild`가 없고 `EnableDotNetAntiSlop`은 기본 off다. 따라서 `dotnet build`의 "경고 0"은 컴파일러 경고만 뜻하며 스타일 준수 근거가 아니다. anti-slop은 `-p:EnableDotNetAntiSlop=true` 감사 모드로만 돌고 초기부터 오류 40/경고 56이 남아 있다.
- 저장소 전체 포맷 검증은 현재 실패 상태다. 원인은 코드 규칙이 아니라 줄바꿈이다(.editorconfig `end_of_line = crlf` vs LF로 커밋된 69개 파일). 특정 변경을 검사할 때는 `dotnet format <whitespace|style> TeachingToolWpf.slnx --no-restore --verify-no-changes --include <파일...>`로 범위를 좁힌다.
- `dotnet format`은 네트워크가 막혀 있으면 복원 경고를 내므로 `--no-restore`를 붙인다.

## 코드 규칙 요약

- private/internal 정적 필드는 `s_camelCase`, 인스턴스 필드는 `_camelCase`, 상수는 PascalCase.
- 파일 범위 namespace, Allman 중괄호, 한 줄 블록 허용(`csharp_preserve_single_line_blocks = true`), `System` using 먼저.
- 식별자는 영어, 새 이유·제약 주석은 한국어. 주석은 "무엇"이 아니라 "왜"를 적는다.
