// File: src/VisionAlgorithm/OpenSrc/LightweightOcr/LightweightSequenceModel.cs
namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// Immutable sequence recognition model storing character vocabulary (index 0 = CTC Blank, $1 \dots C$ = characters),
/// sliding-column prototype weight matrix $[C \times D]$ for AVX2 inner-product logit evaluation, and global mean vector.
/// </summary>
public sealed class LightweightSequenceModel
{
    public const int BlankTokenIndex = 0;
    public const int FeatureCols = 16;
    public const int FeatureRows = 24;
    public const int SpatialFeatureLength = FeatureCols * FeatureRows; // 384
    public const int ProjectionFeatureLength = FeatureCols + FeatureRows; // 40
    public const int FeatureDimension = SpatialFeatureLength + ProjectionFeatureLength; // 424 (divisible by 8 for AVX2)

    private readonly char[] _charset; // index 0 = '\0' (CTC Blank), 1..K = characters
    private readonly float[] _prototypeWeights; // [NumClasses * FeatureDimension]
    private readonly float[] _globalMean; // [FeatureDimension]

    /// <summary>Total number of CTC output classes including index 0 (Blank).</summary>
    public int NumClasses => _charset.Length;

    /// <summary>Dimensionality $D = 424$ of each column-window feature vector.</summary>
    public int Dimension => FeatureDimension;

    public ReadOnlySpan<char> Charset => _charset;
    public ReadOnlySpan<float> PrototypeWeights => _prototypeWeights;
    public ReadOnlySpan<float> GlobalMean => _globalMean;

    public LightweightSequenceModel(
        char[] charsetWithLeadingBlank,
        float[] prototypeWeights,
        float[] globalMean)
    {
        ArgumentNullException.ThrowIfNull(charsetWithLeadingBlank);
        ArgumentNullException.ThrowIfNull(prototypeWeights);
        ArgumentNullException.ThrowIfNull(globalMean);

        if (charsetWithLeadingBlank.Length < 2)
        {
            throw new ArgumentException("Charset must include index 0 (Blank) and at least one character.", nameof(charsetWithLeadingBlank));
        }

        if (prototypeWeights.Length != checked(charsetWithLeadingBlank.Length * FeatureDimension))
        {
            throw new ArgumentException("Prototype weight matrix size does not match NumClasses * FeatureDimension.", nameof(prototypeWeights));
        }

        if (globalMean.Length != FeatureDimension)
        {
            throw new ArgumentException($"Global mean vector length must equal {FeatureDimension}.", nameof(globalMean));
        }

        _charset = charsetWithLeadingBlank;
        _prototypeWeights = prototypeWeights;
        _globalMean = globalMean;
    }

    public ReadOnlySpan<float> GetClassWeights(int classIndex)
        => _prototypeWeights.AsSpan(classIndex * FeatureDimension, FeatureDimension);

    public int IndexOfChar(char c)
    {
        for (int i = 1; i < _charset.Length; i++)
        {
            if (_charset[i] == c)
            {
                return i;
            }
        }

        return -1;
    }
}
