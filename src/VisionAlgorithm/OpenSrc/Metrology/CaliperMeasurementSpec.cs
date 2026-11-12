using System.Numerics;
using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.Common;
using VisionKernels;

namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Configuration parameters for 1D subpixel caliper measurement.
/// </summary>
public readonly record struct CaliperMeasurementSpec
{
    // [Math Why]: Minimum Gaussian derivative sigma avoids aliasing on discrete 1px grids.
    private const float MinimumGaussianSigma = 0.5f;

    // [Math Why]: Truncating the 1D Gaussian derivative filter at 3.5 sigma captures >99.95% of kernel energy.
    private const float KernelRadiusSigmaMultiplier = 3.5f;

    // [Math Why]: Default sigma = 1.1 px balances sub-pixel step-edge localization against sensor shot noise.
    private const float DefaultGaussianSigma = 1.1f;

    // [Math Why]: Default minimum gradient amplitude (12 DN) rejects background sensor floor fluctuations.
    private const float DefaultMinimumContrast = 12f;

    // [Perf Why]: Default 9 orthogonal projection strips attenuate 2D speckle noise by 3x (sqrt(9)).
    private const int DefaultProjectionStripCount = 9;

    private readonly bool _isInitialized;

    public float GaussianSigma { get; }
    public float MinimumContrast { get; }
    public int ProjectionStripCount { get; }
    public bool IsValid => _isInitialized && GaussianSigma >= MinimumGaussianSigma && ProjectionStripCount >= 1;

    public CaliperMeasurementSpec(
        float gaussianSigma = DefaultGaussianSigma,
        float minimumContrast = DefaultMinimumContrast,
        int projectionStripCount = DefaultProjectionStripCount)
    {
        if (!float.IsFinite(gaussianSigma) || gaussianSigma < MinimumGaussianSigma)
        {
            throw new ArgumentOutOfRangeException(nameof(gaussianSigma), $"GaussianSigma must be >= {MinimumGaussianSigma}.");
        }

        if (!float.IsFinite(minimumContrast) || minimumContrast <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumContrast), "MinimumContrast must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(projectionStripCount, 1);

        GaussianSigma = gaussianSigma;
        MinimumContrast = minimumContrast;
        ProjectionStripCount = projectionStripCount;
        _isInitialized = true;
    }

    public static CaliperMeasurementSpec Default => new(DefaultGaussianSigma, DefaultMinimumContrast, DefaultProjectionStripCount);

    public int ComputeKernelRadius()
        => Math.Max(2, (int)MathF.Ceiling(GaussianSigma * KernelRadiusSigmaMultiplier));

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("CaliperMeasurementSpec was not initialized via its constructor.");
        }
    }
}
