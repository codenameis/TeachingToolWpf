using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>일괄 생성의 명시적 부모 인덱스. 부모는 같은 배치에서 먼저 정의해야 한다.</summary>
public sealed record RoiCreation(RoiGeometry Geometry, int? ParentIndex = null);
