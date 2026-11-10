// File: src/VisionAlgorithm/OpenSrc/ToolBlock/MissingPolicy.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Specifies how <see cref="ScalarSpecJudgeNode"/> handles an upstream terminal whose <c>IsValid</c> flag is <see langword="false"/>.
/// </summary>
public enum MissingPolicy
{
    ForceNg = 0,
    UseFallbackValue = 1,
    Ignore = 2
}
