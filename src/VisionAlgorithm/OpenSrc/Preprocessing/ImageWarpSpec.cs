namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Validated specification for standalone and ROI-based geometric image warping.
/// </summary>
public readonly record struct ImageWarpSpec
{
    private const float MinScale = 0.05f;
    private const float MaxScale = 20.0f;

    private readonly bool _isInitialized;

    public WarpTransformKind Kind { get; }
    public float AngleDegrees { get; }
    public float ScaleX { get; }
    public float ScaleY { get; }
    public float ShearX { get; }
    public float ShearY { get; }
    public float KeystoneX { get; }
    public float KeystoneY { get; }
    public float PolarInnerRadiusRatio { get; }
    public float RadialK1 { get; }
    public float RadialK2 { get; }
    public bool IsInitialized => _isInitialized;

    public ImageWarpSpec(
        WarpTransformKind kind = WarpTransformKind.Affine,
        float angleDegrees = 0f,
        float scaleX = 1.0f,
        float scaleY = 1.0f,
        float shearX = 0f,
        float shearY = 0f,
        float keystoneX = 0f,
        float keystoneY = 0f,
        float polarInnerRadiusRatio = 0.25f,
        float radialK1 = -0.12f,
        float radialK2 = 0.02f)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (!float.IsFinite(angleDegrees) || !float.IsFinite(shearX) || !float.IsFinite(shearY) ||
            !float.IsFinite(keystoneX) || !float.IsFinite(keystoneY) || !float.IsFinite(radialK1) || !float.IsFinite(radialK2))
        {
            throw new ArgumentException("All warp parameters must be finite floating-point numbers.");
        }
        if (!float.IsFinite(scaleX) || scaleX < MinScale || scaleX > MaxScale ||
            !float.IsFinite(scaleY) || scaleY < MinScale || scaleY > MaxScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Scale factors must be in [0.05, 20.0].");
        }
        if (!float.IsFinite(polarInnerRadiusRatio) || polarInnerRadiusRatio < 0f || polarInnerRadiusRatio >= 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(polarInnerRadiusRatio), "PolarInnerRadiusRatio must be in [0.0, 1.0).");
        }

        Kind = kind;
        AngleDegrees = angleDegrees;
        ScaleX = scaleX;
        ScaleY = scaleY;
        ShearX = shearX;
        ShearY = shearY;
        KeystoneX = keystoneX;
        KeystoneY = keystoneY;
        PolarInnerRadiusRatio = polarInnerRadiusRatio;
        RadialK1 = radialK1;
        RadialK2 = radialK2;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(ImageWarpSpec)} must be constructed via its parameterized constructor.");
        }
    }
}
