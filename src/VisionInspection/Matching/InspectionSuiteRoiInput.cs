using System.Collections.Immutable;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 캔버스 ROI 입력 정보 및 명시적 실행 모드.
/// </summary>
public sealed record InspectionSuiteRoiInput(
    Guid RoiId,
    string Name,
    PointD Center,
    double Width,
    double Height,
    double AngleRadians,
    ImmutableArray<PointD> Outline,
    int ClassLabel = 0,
    RoiSuiteExecutionMode? ExplicitExecutionMode = null)
{
    public RoiSuiteExecutionMode ExecutionMode => ExplicitExecutionMode ?? LegacyRoiModeMigration.FromRoiName(Name);
}
