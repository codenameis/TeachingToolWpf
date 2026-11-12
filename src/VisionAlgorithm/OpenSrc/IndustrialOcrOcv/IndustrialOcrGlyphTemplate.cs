namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Immutable per-character golden template storing a $16 \times 24$ normalized glyph grid, horizontal/vertical
/// projection profiles, $4 \times 4$ quadrant stroke coverage densities, and a centered L2-normalized (CL2N) feature vector.
/// </summary>
public sealed class IndustrialOcrGlyphTemplate
{
    public const int GridWidth = 16;
    public const int GridHeight = 24;
    public const int GridLength = GridWidth * GridHeight;
    public const int QuadrantRows = 4;
    public const int QuadrantCols = 4;
    public const int QuadrantCount = QuadrantRows * QuadrantCols;

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
            throw new ArgumentException("1D profile lengths do not match 16x24 glyph grid.");
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
