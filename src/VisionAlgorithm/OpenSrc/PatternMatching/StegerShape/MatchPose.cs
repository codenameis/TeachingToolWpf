using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>원본 이미지 기준점 위치, 이미지 기준 절대 회전각(시계 방향 라디안), 양수 균일 배율.</summary>
public readonly record struct MatchPose(double X, double Y, double Angle, double Scale = 1);
