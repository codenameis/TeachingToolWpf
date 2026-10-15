# CpuMatching.Line2D

GPU/네이티브 의존성 없는 .NET10 LINE-2D 계열 방향 응답 matcher. Core만 ProjectReference한다.

`new Line2DMatcher(template).Find(image, options, cancellationToken)`으로 호출한다. 피라미드 방향 응답·SIMD 행 누적과 원본 경계 자세 보정을 사용한다. 원문 전체 LINEMOD 포팅은 아니다.

[호출 예제·논문 대응·테스트 데이터·실측](../../docs/cpu-template-matching.md). 독립 빌드: `dotnet build src/CpuMatching.Line2D/CpuMatching.Line2D.csproj -c Release`.
