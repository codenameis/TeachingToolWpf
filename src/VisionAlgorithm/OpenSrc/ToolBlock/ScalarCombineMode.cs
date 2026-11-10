// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ScalarCombineMode.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies how <see cref="ScalarSpecJudgeNode"/> combines one or two scalar terminal inputs before tolerance evaluation.
/// </summary>
public enum ScalarCombineMode
{
    SingleValue = 0,
    Difference = 1,
    AbsoluteDifference = 2,
    Ratio = 3
}
