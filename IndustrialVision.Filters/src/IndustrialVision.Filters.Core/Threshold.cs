namespace IndustrialVision.Filters.Core;

/// <summary>유효 픽셀에 value > Level이면 255, 아니면 0. 출력은 Gray8.</summary>
public sealed record Threshold : FilterSpec
{
    public float Level
    {
        get;
    }
    public override int Radius => 0;

    public Threshold(float level)
    {
        if (!float.IsFinite(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
        Level = level;
    }
}
