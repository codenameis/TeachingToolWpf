// File: src/VisionAlgorithm/OpenSrc/Metrology/CompositeCaliperSpec.cs
namespace VisionAlgorithm.OpenSrc.Metrology;

/// <summary>
/// Immutable specification for multi-criteria 1D subpixel caliper measurement with First/Last/Composite scoring.
/// </summary>
public readonly record struct CompositeCaliperSpec
{
    private const float MinimumGaussianSigma = 0.5f;
    private const float DefaultGaussianSigma = 1.1f;
    private const float DefaultMinimumContrast = 12.0f;
    private const int DefaultProjectionStripCount = 9;
    private const float DefaultContrastSaturation = 80.0f;
    private const float DefaultPositionToleranceSigma = 12.0f;
    private const float DefaultSpanToleranceSigma = 6.0f;
    private const float DefaultContrastWeight = 0.4f;
    private const float DefaultPositionWeight = 0.3f;
    private const float DefaultSpanWeight = 0.3f;
    private const float MinPositiveSigma = 0.25f;

    private readonly bool _isInitialized;

    public float GaussianSigma { get; }
    public float MinimumContrast { get; }
    public int ProjectionStripCount { get; }
    public CaliperSelectionMode SelectionMode { get; }
    public float ExpectedPosition { get; }
    public float ExpectedSpan { get; }
    public float PositionToleranceSigma { get; }
    public float SpanToleranceSigma { get; }
    public float ContrastSaturation { get; }
    public float ContrastWeight { get; }
    public float PositionWeight { get; }
    public float SpanWeight { get; }

    private CompositeCaliperSpec(
        float gaussianSigma,
        float minimumContrast,
        int projectionStripCount,
        CaliperSelectionMode selectionMode,
        float expectedPosition,
        float expectedSpan,
        float positionToleranceSigma,
        float spanToleranceSigma,
        float contrastSaturation,
        float contrastWeight,
        float positionWeight,
        float spanWeight)
    {
        GaussianSigma = gaussianSigma;
        MinimumContrast = minimumContrast;
        ProjectionStripCount = projectionStripCount;
        SelectionMode = selectionMode;
        ExpectedPosition = expectedPosition;
        ExpectedSpan = expectedSpan;
        PositionToleranceSigma = positionToleranceSigma;
        SpanToleranceSigma = spanToleranceSigma;
        ContrastSaturation = contrastSaturation;
        ContrastWeight = contrastWeight;
        PositionWeight = positionWeight;
        SpanWeight = spanWeight;
        _isInitialized = true;
    }

    /// <summary>
    /// Creates a validated <see cref="CompositeCaliperSpec"/> for multi-criteria caliper edge and pair scoring.
    /// </summary>
    public static CompositeCaliperSpec Create(
        CaliperSelectionMode selectionMode = CaliperSelectionMode.CompositeScore,
        float gaussianSigma = DefaultGaussianSigma,
        float minimumContrast = DefaultMinimumContrast,
        int projectionStripCount = DefaultProjectionStripCount,
        float expectedPosition = float.NaN,
        float expectedSpan = float.NaN,
        float positionToleranceSigma = DefaultPositionToleranceSigma,
        float spanToleranceSigma = DefaultSpanToleranceSigma,
        float contrastSaturation = DefaultContrastSaturation,
        float contrastWeight = DefaultContrastWeight,
        float positionWeight = DefaultPositionWeight,
        float spanWeight = DefaultSpanWeight)
    {
        if (!float.IsFinite(gaussianSigma) || gaussianSigma < MinimumGaussianSigma)
        {
            throw new ArgumentOutOfRangeException(nameof(gaussianSigma));
        }

        if (!float.IsFinite(minimumContrast) || minimumContrast <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumContrast));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(projectionStripCount, 1);

        if (!float.IsFinite(positionToleranceSigma) || positionToleranceSigma < MinPositiveSigma)
        {
            throw new ArgumentOutOfRangeException(nameof(positionToleranceSigma));
        }

        if (!float.IsFinite(spanToleranceSigma) || spanToleranceSigma < MinPositiveSigma)
        {
            throw new ArgumentOutOfRangeException(nameof(spanToleranceSigma));
        }

        if (!float.IsFinite(contrastSaturation) || contrastSaturation <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(contrastSaturation));
        }

        if (!float.IsFinite(contrastWeight) || contrastWeight < 0.0f ||
            !float.IsFinite(positionWeight) || positionWeight < 0.0f ||
            !float.IsFinite(spanWeight) || spanWeight < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(contrastWeight), "Scoring weights must be finite and non-negative.");
        }

        return new CompositeCaliperSpec(
            gaussianSigma,
            minimumContrast,
            projectionStripCount,
            selectionMode,
            expectedPosition,
            expectedSpan,
            positionToleranceSigma,
            spanToleranceSigma,
            contrastSaturation,
            contrastWeight,
            positionWeight,
            spanWeight);
    }

    /// <summary>
    /// Converts the base signal parameters to a <see cref="CaliperMeasurementSpec"/>.
    /// </summary>
    public CaliperMeasurementSpec ToBaseMeasurementSpec()
    {
        EnsureInitialized();
        return new CaliperMeasurementSpec(GaussianSigma, MinimumContrast, ProjectionStripCount);
    }

    internal void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(CompositeCaliperSpec)} must be constructed via {nameof(Create)}.");
        }
    }
}
