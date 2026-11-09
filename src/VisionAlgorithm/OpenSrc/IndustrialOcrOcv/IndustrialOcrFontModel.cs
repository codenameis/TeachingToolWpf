// File: src/VisionAlgorithm/OpenSrc/IndustrialOcrOcv/IndustrialOcrFontModel.cs
namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Immutable per-character golden template storing a $16 \times 24$ normalized glyph grid, horizontal/vertical
/// projection profiles, $4 \times 4$ quadrant stroke coverage densities, and a centered L2-normalized (CL2N) feature vector.
/// </summary>
public sealed class IndustrialOcrGlyphTemplate
{
    public const int GridWidth = 16;
    public const int GridHeight = 24;
    public const int GridLength = GridWidth * GridHeight; // 384
    public const int QuadrantRows = 4;
    public const int QuadrantCols = 4;
    public const int QuadrantCount = QuadrantRows * QuadrantCols; // 16

    private readonly float[] _normalizedGrid;
    private readonly float[] _cl2nFeature;
    private readonly float[] _horizontalProjection;
    private readonly float[] _verticalProjection;
    private readonly float[] _quadrantDensities;

    public char Character { get; }
    public ReadOnlySpan<float> NormalizedGrid => _normalizedGrid;
    public ReadOnlySpan<float> Cl2nFeature => _cl2nFeature;
    public ReadOnlySpan<float> HorizontalProjection => _horizontalProjection;
    public ReadOnlySpan<float> VerticalProjection => _verticalProjection;
    public ReadOnlySpan<float> QuadrantDensities => _quadrantDensities;

    public IndustrialOcrGlyphTemplate(
        char character,
        float[] normalizedGrid,
        float[] cl2nFeature,
        float[] horizontalProjection,
        float[] verticalProjection,
        float[] quadrantDensities)
    {
        ArgumentNullException.ThrowIfNull(normalizedGrid);
        ArgumentNullException.ThrowIfNull(cl2nFeature);
        ArgumentNullException.ThrowIfNull(horizontalProjection);
        ArgumentNullException.ThrowIfNull(verticalProjection);
        ArgumentNullException.ThrowIfNull(quadrantDensities);

        if (normalizedGrid.Length != GridLength || cl2nFeature.Length != GridLength)
        {
            throw new ArgumentException($"Glyph grid and CL2N feature must have length {GridLength}.");
        }

        if (horizontalProjection.Length != GridHeight || verticalProjection.Length != GridWidth)
        {
            throw new ArgumentException("Projection profile dimensions do not match 16x24 glyph grid.");
        }

        if (quadrantDensities.Length != QuadrantCount)
        {
            throw new ArgumentException($"Quadrant densities must have length {QuadrantCount}.");
        }

        Character = character;
        _normalizedGrid = normalizedGrid;
        _cl2nFeature = cl2nFeature;
        _horizontalProjection = horizontalProjection;
        _verticalProjection = verticalProjection;
        _quadrantDensities = quadrantDensities;
    }
}

/// <summary>
/// Immutable trained font model containing all golden character templates and the SimpleShot global mean vector
/// $\bar{x} \in \mathbb{R}^{384}$ used for CL2N inner-product matching.
/// </summary>
public sealed class IndustrialOcrFontModel
{
    private readonly IndustrialOcrGlyphTemplate[] _templates;
    private readonly float[] _globalMean;
    private readonly Dictionary<char, int> _charToIndex;

    public ReadOnlySpan<IndustrialOcrGlyphTemplate> Templates => _templates;
    public ReadOnlySpan<float> GlobalMean => _globalMean;
    public int Count => _templates.Length;

    public IndustrialOcrFontModel(IndustrialOcrGlyphTemplate[] templates, float[] globalMean)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(globalMean);
        if (templates.Length == 0)
        {
            throw new ArgumentException("Font model must contain at least one character template.", nameof(templates));
        }

        if (globalMean.Length != IndustrialOcrGlyphTemplate.GridLength)
        {
            throw new ArgumentException($"Global mean vector must have length {IndustrialOcrGlyphTemplate.GridLength}.", nameof(globalMean));
        }

        _templates = templates;
        _globalMean = globalMean;
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
