using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>템플릿 순서 → 격자 행 → 격자 열 순서의 정수 합. 임계값과 NMS는 아직 적용하지 않는다.</summary>
public sealed record ShapeCandidateScoreBatch(int GridWidth, int GridHeight, int TemplateCount, ushort[] Scores);
