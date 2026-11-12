namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Validated specification for $O(1)$-per-pixel box-integral Guided Image Filtering (He et al., IEEE TPAMI 2013).
/// </summary>
public readonly record struct GuidedFilterSpec
{
    private const int MaxSupportedRadius = 64;

    private readonly bool _isInitialized;

    public int Radius { get; }
    public float RegularizationEpsilon { get; }
    public bool IsInitialized => _isInitialized;

    public GuidedFilterSpec(int radius = 4, float regularizationEpsilon = 400.0f)
    {
        if (radius < 1 || radius > MaxSupportedRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Guided filter radius must be in [1, 64].");
        }
        if (!float.IsFinite(regularizationEpsilon) || regularizationEpsilon <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(regularizationEpsilon), "RegularizationEpsilon must be finite and > 0.");
        }

        Radius = radius;
        RegularizationEpsilon = regularizationEpsilon;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(GuidedFilterSpec)} must be constructed via its parameterized constructor.");
        }
    }
}
