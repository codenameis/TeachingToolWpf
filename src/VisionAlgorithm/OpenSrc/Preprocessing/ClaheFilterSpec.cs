namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Validated specification for Contrast-Limited Adaptive Histogram Equalization (CLAHE, Zuiderveld 1994).
/// </summary>
public readonly record struct ClaheFilterSpec
{
    private const int MinTileGrid = 2;
    private const int MaxTileGrid = 32;

    private readonly bool _isInitialized;

    public int TileGridX { get; }
    public int TileGridY { get; }
    public float ClipLimit { get; }
    public bool IsInitialized => _isInitialized;

    public ClaheFilterSpec(int tileGridX = 4, int tileGridY = 4, float clipLimit = 2.5f)
    {
        if (tileGridX < MinTileGrid || tileGridX > MaxTileGrid)
        {
            throw new ArgumentOutOfRangeException(nameof(tileGridX), "TileGridX must be in [2, 32].");
        }
        if (tileGridY < MinTileGrid || tileGridY > MaxTileGrid)
        {
            throw new ArgumentOutOfRangeException(nameof(tileGridY), "TileGridY must be in [2, 32].");
        }
        if (!float.IsFinite(clipLimit) || clipLimit < 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(clipLimit), "ClipLimit must be finite and >= 1.0.");
        }

        TileGridX = tileGridX;
        TileGridY = tileGridY;
        ClipLimit = clipLimit;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(ClaheFilterSpec)} must be constructed via its parameterized constructor.");
        }
    }
}
