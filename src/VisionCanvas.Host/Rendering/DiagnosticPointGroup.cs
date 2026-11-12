using System.Collections.Immutable;
using System.Numerics;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>알고리즘별 특징·진단 점 묶음(색상·반경·좌표 배열). 렌더러 수정 없이 새 알고리즘의 시각적 피드백을 표현한다.</summary>
public readonly record struct DiagnosticPointGroup(Vector4 Color, ImmutableArray<PointD> Points, double HalfSize = 2.2);
