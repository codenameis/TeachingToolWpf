# CpuMatching.DirectionalChamfer

GPU/네이티브 의존성 없는 .NET10 방향성 Chamfer matcher. Core만 ProjectReference한다.

`new DirectionalChamferMatcher(template).Find(image, options, cancellationToken)`으로 호출한다. 방향별 공간 거리, 순환 방향 비용과 원본 경계 자세 보정을 사용한다. FDCM의 선분 적분 가속과 영역 하한 가지치기는 미구현이며 원 논문 속도를 주장하지 않는다.

[호출 예제·논문 대응·테스트 데이터·실측](../../docs/cpu-template-matching.md). 독립 빌드: `dotnet build src/CpuMatching.DirectionalChamfer/CpuMatching.DirectionalChamfer.csproj -c Release`.
