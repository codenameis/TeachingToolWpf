using System.Collections.Immutable;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 개별 ROI 검사 결과(<see cref="InspectionSuiteItemResult"/>) 구성을 표준화하는 빌더.
/// </summary>
public sealed class InspectionItemResultBuilder
{
    private readonly Guid _roiId;
    private readonly string _name;
    private PointD _referencePoint;
    private double _angleRadians;
    private ImmutableArray<PointD> _bounds;
    private readonly ImmutableArray<ImmutableArray<PointD>>.Builder _contours = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
    private readonly ImmutableArray<PointD>.Builder _primaryPoints = ImmutableArray.CreateBuilder<PointD>();
    private readonly ImmutableArray<PointD>.Builder _secondaryPoints = ImmutableArray.CreateBuilder<PointD>();
    private readonly ImmutableArray<PointD>.Builder _defectPoints = ImmutableArray.CreateBuilder<PointD>();
    private ImmutableArray<ImmutableArray<PointD>> _guideContours;
    private OcrDiagnosticSnapshot? _ocrDiagnostic;

    public InspectionItemResultBuilder(InspectionSuiteRoiInput target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _roiId = target.RoiId;
        _name = target.Name;
        _referencePoint = target.Center;
        _angleRadians = target.AngleRadians;
        _bounds = target.Outline;
    }

    public InspectionItemResultBuilder(Guid roiId, string name, PointD referencePoint, double angleRadians, ImmutableArray<PointD> bounds)
    {
        ArgumentNullException.ThrowIfNull(name);
        _roiId = roiId;
        _name = name;
        _referencePoint = referencePoint;
        _angleRadians = angleRadians;
        _bounds = bounds;
    }

    public int PrimaryPointCount => _primaryPoints.Count;

    public int DefectPointCount => _defectPoints.Count;

    public InspectionItemResultBuilder WithPose(PointD referencePoint, double angleRadians)
    {
        _referencePoint = referencePoint;
        _angleRadians = angleRadians;
        return this;
    }

    public InspectionItemResultBuilder WithBounds(ImmutableArray<PointD> bounds)
    {
        _bounds = bounds;
        return this;
    }

    public InspectionItemResultBuilder AddContour(ImmutableArray<PointD> contour)
    {
        _contours.Add(contour);
        return this;
    }

    public InspectionItemResultBuilder AddPrimaryPoint(PointD point)
    {
        _primaryPoints.Add(point);
        return this;
    }

    public InspectionItemResultBuilder AddPrimaryPoints(ImmutableArray<PointD> points)
    {
        _primaryPoints.AddRange(points);
        return this;
    }

    public InspectionItemResultBuilder AddSecondaryPoint(PointD point)
    {
        _secondaryPoints.Add(point);
        return this;
    }

    public InspectionItemResultBuilder AddSecondaryPoints(ImmutableArray<PointD> points)
    {
        _secondaryPoints.AddRange(points);
        return this;
    }

    public InspectionItemResultBuilder AddDefectPoint(PointD point)
    {
        _defectPoints.Add(point);
        return this;
    }

    public InspectionItemResultBuilder WithGuideContours(ImmutableArray<ImmutableArray<PointD>> guideContours)
    {
        _guideContours = guideContours;
        return this;
    }

    public InspectionItemResultBuilder WithOcrDiagnostic(OcrDiagnosticSnapshot? diagnostic)
    {
        _ocrDiagnostic = diagnostic;
        return this;
    }

    public InspectionSuiteItemResult Build(bool isPass, string badgeLabel, string detailLine, double elapsedMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(badgeLabel);
        ArgumentNullException.ThrowIfNull(detailLine);

        return new InspectionSuiteItemResult(
            RoiId: _roiId,
            Name: _name,
            IsPass: isPass,
            BadgeLabel: badgeLabel,
            DetailLine: detailLine,
            ReferencePoint: _referencePoint,
            AngleRadians: _angleRadians,
            Bounds: _bounds,
            AdditionalContours: _contours.ToImmutable(),
            PrimaryPoints: _primaryPoints.ToImmutable(),
            SecondaryPoints: _secondaryPoints.ToImmutable(),
            DefectOrOutlierPoints: _defectPoints.ToImmutable(),
            ElapsedMicroseconds: elapsedMicroseconds,
            GuideContours: _guideContours,
            OcrDiagnostic: _ocrDiagnostic);
    }
}
