namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Immutable trained font model containing golden character templates, polarity metadata, and the SimpleShot
/// global mean vector $\bar{x} \in \mathbb{R}^{384}$ used for CL2N inner-product matching.
/// </summary>
public sealed class IndustrialOcrFontModel
{
    private readonly IndustrialOcrGlyphTemplate[] _templates;
    private readonly float[] _globalMean;
    private readonly Dictionary<char, int> _charToIndex;

    public ReadOnlySpan<IndustrialOcrGlyphTemplate> Templates => _templates;
    public ReadOnlySpan<float> GlobalMean => _globalMean;
    public int Count => _templates.Length;
    public bool DarkOnLight { get; }

    public IndustrialOcrFontModel(
        IndustrialOcrGlyphTemplate[] templates,
        float[] globalMean,
        bool darkOnLight = true)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(globalMean);
        if (templates.Length == 0)
        {
            throw new ArgumentException("Font model must contain at least one character template.", nameof(templates));
        }

        if (globalMean.Length != IndustrialOcrGlyphTemplate.GridLength)
        {
            throw new ArgumentException(
                $"Global mean vector must have length {IndustrialOcrGlyphTemplate.GridLength}.",
                nameof(globalMean));
        }

        _templates = templates;
        _globalMean = globalMean;
        DarkOnLight = darkOnLight;
        _charToIndex = new Dictionary<char, int>(templates.Length);
        for (int i = 0; i < templates.Length; i++)
        {
            _charToIndex[templates[i].Character] = i;
        }
    }

    public bool TryGetTemplate(char ch, out IndustrialOcrGlyphTemplate? template)
    {
        if (_charToIndex.TryGetValue(ch, out int idx))
        {
            template = _templates[idx];
            return true;
        }

        template = null;
        return false;
    }
}
