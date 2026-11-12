using System.Collections.Immutable;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>하나의 ROI가 편집 전후에 어떻게 달라졌는지 보관한다. null은 해당 ROI가 없음을 뜻한다.</summary>
public sealed record RoiChange(Guid Id, Roi? Before, Roi? After);
