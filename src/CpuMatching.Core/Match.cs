namespace CpuMatching.Core;

/// <summary>픽셀 인덱스 기준 중심, 영상 시계 방향 도, 균일 배율.</summary>
public readonly record struct Match(double X, double Y, double AngleDegrees, double Scale, double Score);
