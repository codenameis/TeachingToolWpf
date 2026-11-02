using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace TeachingToolWpf.Models;

public sealed record PrimitivePolarityOption(ForegroundPolarity Value, string Name);

public enum InspectionSpeedProfile
{
    // 이전 값 0은 호환을 위해 남기되 선택 시 기본 후보 예산으로 전환한다.
    Fast = 0,
    Balanced = 1,
    Precise = 2,
    Custom = 3
}

public sealed record InspectionSpeedOption(InspectionSpeedProfile Profile, string Name);
public sealed record InspectionComputeOption(InspectionComputeMode Mode, string Name);

public enum PatternMatchingMethod
{
    Shape,
    OpenCvTemplate,
    PoseMatchTdcm = 3,
    CpuLine2D = 4,
    CpuSubNcc = 5,
    CpuDirectionalChamfer = 6
}

public sealed record PatternMethodOption(PatternMatchingMethod Method, string Name);

public enum InspectionToolKind
{
    Pattern,
    PrimitiveShapes
}

public sealed record InspectionToolOption(InspectionToolKind Kind, string Name);

