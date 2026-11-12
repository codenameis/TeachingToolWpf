using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>Mask는 원본 픽셀마다 0/비0 한 바이트. 그래디언트 계산 후 특징 선택과 추가 윤곽 점수의 유효 영역에 적용한다.</summary>
public sealed record TrainingRegion(ReadOnlyMemory<byte> Mask, ImagePoint ReferencePoint, double ReferenceAngle = 0);
