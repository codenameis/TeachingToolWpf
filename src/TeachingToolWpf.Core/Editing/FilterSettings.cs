namespace TeachingToolWpf.Core.Editing;

public enum RoiFilterKind { None, Binarize, Edge }

/// <summary>ROI recipe values only; native image processing stays outside Core.</summary>
public sealed record FilterSettings(RoiFilterKind Kind, double Threshold = 128, double EdgeLow = 50, double EdgeHigh = 150)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) ||
            (Kind == RoiFilterKind.Binarize && (!double.IsFinite(Threshold) || Threshold is < 0 or > 255)) ||
            (Kind == RoiFilterKind.Edge && (!double.IsFinite(EdgeLow) || !double.IsFinite(EdgeHigh) || EdgeLow < 0 || EdgeHigh > 255 || EdgeLow >= EdgeHigh)))
            throw new ArgumentException("임계값은 0–255, Edge 하한은 상한보다 작아야 합니다.");
    }
}
