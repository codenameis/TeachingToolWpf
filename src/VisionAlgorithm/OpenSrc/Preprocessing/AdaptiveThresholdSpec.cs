namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Validated specification for integral-image Sauvola / local mean adaptive thresholding.
/// </summary>
public readonly record struct AdaptiveThresholdSpec
{
    private const int MaxWindowRadius = 128;

    private readonly bool _isInitialized;

    public int WindowRadius { get; }
    public float OffsetConstant { get; }
    public float SauvolaSensitivityK { get; }
    public bool UseSauvolaStdWeighting { get; }
    public bool InvertPolarity { get; }
    public bool IsInitialized => _isInitialized;

    public AdaptiveThresholdSpec(
        int windowRadius = 7,
        float offsetConstant = 8.0f,
        float sauvolaSensitivityK = 0.2f,
        bool useSauvolaStdWeighting = false,
        bool invertPolarity = false)
    {
        if (windowRadius < 1 || windowRadius > MaxWindowRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(windowRadius), "WindowRadius must be in [1, 128].");
        }
        if (!float.IsFinite(offsetConstant))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetConstant), "OffsetConstant must be finite.");
        }
        if (!float.IsFinite(sauvolaSensitivityK) || sauvolaSensitivityK < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sauvolaSensitivityK), "SauvolaSensitivityK must be finite and >= 0.");
        }

        WindowRadius = windowRadius;
        OffsetConstant = offsetConstant;
        SauvolaSensitivityK = sauvolaSensitivityK;
        UseSauvolaStdWeighting = useSauvolaStdWeighting;
        InvertPolarity = invertPolarity;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(AdaptiveThresholdSpec)} must be constructed via its parameterized constructor.");
        }
    }
}
