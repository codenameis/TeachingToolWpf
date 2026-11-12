using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public enum InspectionComputeMode
{
    Cpu,
    /// <summary>기존 직렬화 이름을 유지한다. 공급자가 지원하면 GPU 후보 점수와 CPU 원본 정밀 검증을 함께 사용한다.</summary>
    GpuPreprocessing
}
