namespace CpuMatching.Core;

/// <summary>템플릿 평균을 뺀 명암 표본. 위치는 템플릿 중심 기준이다.</summary>
public readonly record struct CorrelationSample(double X, double Y, double Value);
