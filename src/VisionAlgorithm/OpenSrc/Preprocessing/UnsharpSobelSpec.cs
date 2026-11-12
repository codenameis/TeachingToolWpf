namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Validated specification for unsharp masking and Sobel gradient magnitude enhancement.
/// </summary>
public readonly record struct UnsharpSobelSpec
{
    private const int MaxRadius = 31;

    private readonly bool _isInitialized;

    public int Radius { get; }
    public float Sigma { get; }
    public float Amount { get; }
    public bool IsInitialized => _isInitialized;

    public UnsharpSobelSpec(int radius = 3, float sigma = 1.5f, float amount = 1.5f)
    {
        if (radius < 1 || radius > MaxRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be in [1, 31].");
        }
        if (!float.IsFinite(sigma) || sigma <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), "Sigma must be finite and > 0.");
        }
        if (!float.IsFinite(amount) || amount < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be finite and >= 0.");
        }

        Radius = radius;
        Sigma = sigma;
        Amount = amount;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(UnsharpSobelSpec)} must be constructed via its parameterized constructor.");
        }
    }
}
