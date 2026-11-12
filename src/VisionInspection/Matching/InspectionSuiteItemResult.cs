using System.Collections.Immutable;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 개별 ROI 검사 결과 및 캔버스 시각화 기하 정보.
/// </summary>
public sealed record InspectionSuiteItemResult(
    Guid RoiId,
    string Name,
    bool IsPass,
    string BadgeLabel,
    string DetailLine,
    PointD ReferencePoint,
    double AngleRadians,
    ImmutableArray<PointD> Bounds,
    ImmutableArray<ImmutableArray<PointD>> AdditionalContours,
    ImmutableArray<PointD> PrimaryPoints,
    ImmutableArray<PointD> SecondaryPoints,
    ImmutableArray<PointD> DefectOrOutlierPoints,
    double ElapsedMicroseconds,
    ImmutableArray<ImmutableArray<PointD>> GuideContours = default,
    OcrDiagnosticSnapshot? OcrDiagnostic = null);
