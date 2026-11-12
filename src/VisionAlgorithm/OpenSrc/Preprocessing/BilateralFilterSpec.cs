namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Validated specification for edge-preserving bilateral filtering (Tomasi &amp; Manduchi, ICCV 1998).
/// </summary>
public readonly record struct BilateralFilterSpec
{
    private const int MaxSupportedRadius = 31;

    private readonly bool _isInitialized;

    public int Radius { get; }
    public float SpatialSigma { get; }
    public float RangeSigma { get; }
    public bool IsInitialized => _isInitialized;

    public BilateralFilterSpec(int radius = 3, float spatialSigma = 2.0f, float rangeSigma = 25.0f)
    {
        if (radius < 1 || radius > MaxSupportedRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Bilateral radius must be in [1, 31].");
        }
        if (!float.IsFinite(spatialSigma) || spatialSigma <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(spatialSigma), "SpatialSigma must be finite and > 0.");
        }
        if (!float.IsFinite(rangeSigma) || rangeSigma <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeSigma), "RangeSigma must be finite and > 0.");
        }

        Radius = radius;
        SpatialSigma = spatialSigma;
        RangeSigma = rangeSigma;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(BilateralFilterSpec)} must be constructed via its parameterized constructor.");
        }
    }
}
