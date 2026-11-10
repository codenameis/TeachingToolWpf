// File: src/VisionAlgorithm/OpenSrc/ToolBlock/ScalarSpecLimit.cs
namespace VisionAlgorithm.OpenSrc.ToolBlock;

/// <summary>
/// Immutable tolerance specification for <see cref="ScalarSpecJudgeNode"/> defining <c>[LowerSpecLimit, UpperSpecLimit]</c>,
/// <see cref="MissingPolicy"/>, fallback value, and optional dual-input arithmetic mode.
/// </summary>
public readonly record struct ScalarSpecLimit
{
    private readonly bool _isInitialized;

    public double LowerSpecLimit { get; }
    public double UpperSpecLimit { get; }
    public MissingPolicy MissingPolicy { get; }
    public double FallbackValue { get; }
    public ScalarCombineMode CombineMode { get; }

    public bool IsValid => _isInitialized && double.IsFinite(LowerSpecLimit) && double.IsFinite(UpperSpecLimit) && LowerSpecLimit <= UpperSpecLimit;

    public ScalarSpecLimit(
        double lowerSpecLimit,
        double upperSpecLimit,
        MissingPolicy missingPolicy = MissingPolicy.ForceNg,
        double fallbackValue = 0.0,
        ScalarCombineMode combineMode = ScalarCombineMode.SingleValue)
    {
        if (!double.IsFinite(lowerSpecLimit) || !double.IsFinite(upperSpecLimit))
        {
            throw new ArgumentOutOfRangeException(nameof(lowerSpecLimit), "Spec limits must be finite numbers.");
        }

        if (upperSpecLimit < lowerSpecLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(upperSpecLimit), "UpperSpecLimit must be >= LowerSpecLimit.");
        }

        if (!double.IsFinite(fallbackValue))
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackValue), "FallbackValue must be finite.");
        }

        LowerSpecLimit = lowerSpecLimit;
        UpperSpecLimit = upperSpecLimit;
        MissingPolicy = missingPolicy;
        FallbackValue = fallbackValue;
        CombineMode = combineMode;
        _isInitialized = true;
    }

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException($"{nameof(ScalarSpecLimit)} was not initialized via its constructor.");
        }
    }
}
