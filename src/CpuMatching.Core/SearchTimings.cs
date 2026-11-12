namespace CpuMatching.Core;

/// <summary>같은 검색 안의 경과 시간(ms). 창 입력은 원본 정밀 보정과 검증을 이어서 수행하며 둘 다 Verification에 포함한다.</summary>
public sealed record SearchTimings(double Preparation, double CoarseSearch, double Refinement, double Verification, double Suppression);
