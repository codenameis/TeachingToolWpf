using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Sequential 1-based identifier assigned to each unique part master in a <see cref="PartModelPool"/> (<c>#1</c>, <c>#2</c>, <c>#3</c>, ...).
/// </summary>
public readonly record struct PartModelId(int Value)
{
    public bool IsValid => Value > 0;
    public override string ToString() => $"#{Value}";
}
