# CpuMatching.SubNcc

GPU/네이티브 의존성 없는 .NET10 선택 픽셀 ZNCC matcher. Core만 ProjectReference한다.

`new SubNccMatcher(template).Find(image, options, cancellationToken)`으로 호출한다. coarse 원판/경계·균일 표본, 원본 전체 템플릿 표본, 피라미드/회전/배율 검색을 사용한다. 논문의 stable-pixel 절차 전체 재현과는 구별한다.

[호출 예제·논문 대응·테스트 데이터·실측](../../docs/cpu-template-matching.md). 독립 빌드: `dotnet build src/CpuMatching.SubNcc/CpuMatching.SubNcc.csproj -c Release`.
