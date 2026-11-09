// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Domain/RoiFixturePose.cs
namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Domain;

/// <summary>
/// 2D affine/similarity pose <c>(CenterX, CenterY, AngleRad, ScaleX, ScaleY)</c> supplied by an upstream geometric matcher to fixture an inspection ROI.
/// </summary>
public readonly record struct RoiFixturePose
{
    // [Math Why]: Prevent degenerate zero or near-zero scale inversion when mapping ROI grid coordinates to source image space.
    public const float MinScale = 0.05f;
    public const float MaxScale = 20.0f;

    private readonly bool _isInitialized;

    public float CenterX { get; }
    public float CenterY { get; }
    public float AngleRad { get; }
    public float Scale { get; }
    public float ScaleX { get; }
    public float ScaleY { get; }
    public bool IsInitialized => _isInitialized;
    public bool IsEmpty => !_isInitialized;

    private RoiFixturePose(float centerX, float centerY, float angleRad, float scaleX, float scaleY, bool isInitialized)
    {
        CenterX = centerX;
        CenterY = centerY;
        AngleRad = angleRad;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Scale = MathF.Max(scaleX, scaleY);
        _isInitialized = isInitialized;
    }

    public static RoiFixturePose Empty => new(0f, 0f, 0f, 0f, 0f, isInitialized: false);

    public static RoiFixturePose Create(float centerX, float centerY, float angleRad = 0f, float scale = 1f)
    {
        if (!float.IsFinite(scale) || (scale < MinScale) || (scale > MaxScale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), $"Scale must be finite and within [{MinScale}, {MaxScale}].");
        }

        return CreateAnisotropic(centerX, centerY, angleRad, scale, scale);
    }

    public static RoiFixturePose CreateAnisotropic(float centerX, float centerY, float angleRad, float scaleX, float scaleY)
    {
        if (!float.IsFinite(centerX))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "CenterX must be finite.");
        }

        if (!float.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerY), "CenterY must be finite.");
        }

        if (!float.IsFinite(angleRad))
        {
            throw new ArgumentOutOfRangeException(nameof(angleRad), "AngleRad must be finite.");
        }

        if (!float.IsFinite(scaleX) || (scaleX < MinScale) || (scaleX > MaxScale))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX), $"ScaleX must be finite and within [{MinScale}, {MaxScale}].");
        }

        if (!float.IsFinite(scaleY) || (scaleY < MinScale) || (scaleY > MaxScale))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleY), $"ScaleY must be finite and within [{MinScale}, {MaxScale}].");
        }

        return new RoiFixturePose(centerX, centerY, angleRad, scaleX, scaleY, isInitialized: true);
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("RoiFixturePose must be created via RoiFixturePose.Create() or CreateAnisotropic() rather than default(RoiFixturePose).");
        }
    }

    /// <summary>
    /// Maps a centered canonical ROI grid offset <c>(localDx, localDy)</c> into continuous source image coordinates.
    /// </summary>
    public (float SourceX, float SourceY) TransformLocalToSource(float localDx, float localDy)
    {
        EnsureInitialized();
        var (sin, cos) = MathF.SinCos(AngleRad);
        var scaledDx = localDx * ScaleX;
        var scaledDy = localDy * ScaleY;
        var sourceX = CenterX + ((scaledDx * cos) - (scaledDy * sin));
        var sourceY = CenterY + ((scaledDx * sin) + (scaledDy * cos));
        return (sourceX, sourceY);
    }
}
