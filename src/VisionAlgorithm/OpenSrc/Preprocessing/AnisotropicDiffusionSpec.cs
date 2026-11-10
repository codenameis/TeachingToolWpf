// File: src/VisionAlgorithm/OpenSrc/Preprocessing/AnisotropicDiffusionSpec.cs
namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Immutable specification for Perona-Malik Anisotropic Diffusion edge-preserving smoothing
/// (HALCON <c>anisotropic_diffusion</c> parity, Perona &amp; Malik IEEE TPAMI 1990).
/// </summary>
public readonly record struct AnisotropicDiffusionSpec
{
    public const int DefaultIterations = 6;
    public const float DefaultKappaContrast = 20.0f;
    public const float DefaultTimeStepLambda = 0.20f;
    private const int MaxSupportedIterations = 30;
    private const float MaxStableLambda2D = 0.25f;

    private readonly bool _isInitialized;

    public int Iterations { get; }
    public float KappaContrast { get; }
    public float TimeStepLambda { get; }
    public bool IsInitialized => _isInitialized;

    private AnisotropicDiffusionSpec(int iterations, float kappaContrast, float timeStepLambda)
    {
        Iterations = iterations;
        KappaContrast = kappaContrast;
        TimeStepLambda = timeStepLambda;
        _isInitialized = true;
    }

    public static AnisotropicDiffusionSpec Create(
        int iterations = DefaultIterations,
        float kappaContrast = DefaultKappaContrast,
        float timeStepLambda = DefaultTimeStepLambda)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxSupportedIterations);

        if (!float.IsFinite(kappaContrast) || kappaContrast <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(kappaContrast), "KappaContrast must be finite and positive.");
        }

        if (!float.IsFinite(timeStepLambda) || timeStepLambda <= 0.0f || timeStepLambda > MaxStableLambda2D)
        {
            throw new ArgumentOutOfRangeException(nameof(timeStepLambda), "TimeStepLambda must be in (0, 0.25] for 2D forward-Euler stability.");
        }

        return new AnisotropicDiffusionSpec(iterations, kappaContrast, timeStepLambda);
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(AnisotropicDiffusionSpec)} must be constructed via {nameof(Create)}.");
        }
    }
}
