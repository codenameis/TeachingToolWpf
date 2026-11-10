// File: src/VisionAlgorithm/OpenSrc/Preprocessing/PolynomialShadingSpec.cs
namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Immutable specification for 2D Biquadratic Polynomial Background Illumination Estimation and Shading Correction
/// (HALCON <c>estimate_background_illumination</c> / Cognex <c>CogSceneIlluminationCorrection</c> parity).
/// </summary>
public readonly record struct PolynomialShadingSpec
{
    public const byte DefaultTargetBaseGray = 128;
    public const int DefaultSubsampleStep = 4;
    private const int MaxSubsampleStep = 32;

    private readonly bool _isInitialized;

    public byte TargetBaseGray { get; }
    public int SubsampleStep { get; }
    public bool IsMultiplicativeGain { get; }
    public bool IsInitialized => _isInitialized;

    private PolynomialShadingSpec(byte targetBaseGray, int subsampleStep, bool isMultiplicativeGain)
    {
        TargetBaseGray = targetBaseGray;
        SubsampleStep = subsampleStep;
        IsMultiplicativeGain = isMultiplicativeGain;
        _isInitialized = true;
    }

    public static PolynomialShadingSpec Create(
        byte targetBaseGray = DefaultTargetBaseGray,
        int subsampleStep = DefaultSubsampleStep,
        bool isMultiplicativeGain = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(subsampleStep, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subsampleStep, MaxSubsampleStep);

        return new PolynomialShadingSpec(targetBaseGray, subsampleStep, isMultiplicativeGain);
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(PolynomialShadingSpec)} must be constructed via {nameof(Create)}.");
        }
    }
}
