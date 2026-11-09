// File: src/VisionAlgorithm/OpenSrc/Preprocessing/PreprocessingSpecs.cs
namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Selects the geometric transformation model used by <see cref="ImageWarpingEngine"/>.
/// </summary>
public enum WarpTransformKind
{
    Affine = 0,
    PerspectiveKeystone = 1,
    PolarAnnularUnwrap = 2,
    BrownConradyUndistort = 3
}

/// <summary>
/// Validated specification for edge-preserving bilateral filtering (Tomasi &amp; Manduchi, ICCV 1998).
/// </summary>
public readonly record struct BilateralFilterSpec
{
    private const int MaxSupportedRadius = 31;

    private readonly bool _isInitialized;

    public int Radius { get; }
    public float SpatialSigma { get; }
    public float RangeSigma { get; }
    public bool IsInitialized => _isInitialized;

    public BilateralFilterSpec(int radius = 3, float spatialSigma = 2.0f, float rangeSigma = 25.0f)
    {
        if (radius < 1 || radius > MaxSupportedRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Bilateral radius must be in [1, 31].");
        }
        if (!float.IsFinite(spatialSigma) || spatialSigma <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(spatialSigma), "SpatialSigma must be finite and > 0.");
        }
        if (!float.IsFinite(rangeSigma) || rangeSigma <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeSigma), "RangeSigma must be finite and > 0.");
        }

        Radius = radius;
        SpatialSigma = spatialSigma;
        RangeSigma = rangeSigma;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(BilateralFilterSpec)} must be constructed via its parameterized constructor.");
        }
    }
}

/// <summary>
/// Validated specification for $O(1)$-per-pixel box-integral Guided Image Filtering (He et al., IEEE TPAMI 2013).
/// </summary>
public readonly record struct GuidedFilterSpec
{
    private const int MaxSupportedRadius = 64;

    private readonly bool _isInitialized;

    public int Radius { get; }
    public float RegularizationEpsilon { get; }
    public bool IsInitialized => _isInitialized;

    public GuidedFilterSpec(int radius = 4, float regularizationEpsilon = 400.0f)
    {
        if (radius < 1 || radius > MaxSupportedRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Guided filter radius must be in [1, 64].");
        }
        if (!float.IsFinite(regularizationEpsilon) || regularizationEpsilon <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(regularizationEpsilon), "RegularizationEpsilon must be finite and > 0.");
        }

        Radius = radius;
        RegularizationEpsilon = regularizationEpsilon;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(GuidedFilterSpec)} must be constructed via its parameterized constructor.");
        }
    }
}

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

/// <summary>
/// Validated specification for integral-image Sauvola / local mean adaptive thresholding.
/// </summary>
public readonly record struct AdaptiveThresholdSpec
{
    private const int MaxWindowRadius = 128;

    private readonly bool _isInitialized;

    public int WindowRadius { get; }
    public float OffsetConstant { get; }
    public float SauvolaSensitivityK { get; }
    public bool UseSauvolaStdWeighting { get; }
    public bool InvertPolarity { get; }
    public bool IsInitialized => _isInitialized;

    public AdaptiveThresholdSpec(
        int windowRadius = 7,
        float offsetConstant = 8.0f,
        float sauvolaSensitivityK = 0.2f,
        bool useSauvolaStdWeighting = false,
        bool invertPolarity = false)
    {
        if (windowRadius < 1 || windowRadius > MaxWindowRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(windowRadius), "WindowRadius must be in [1, 128].");
        }
        if (!float.IsFinite(offsetConstant))
        {
            throw new ArgumentOutOfRangeException(nameof(offsetConstant), "OffsetConstant must be finite.");
        }
        if (!float.IsFinite(sauvolaSensitivityK) || sauvolaSensitivityK < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sauvolaSensitivityK), "SauvolaSensitivityK must be finite and >= 0.");
        }

        WindowRadius = windowRadius;
        OffsetConstant = offsetConstant;
        SauvolaSensitivityK = sauvolaSensitivityK;
        UseSauvolaStdWeighting = useSauvolaStdWeighting;
        InvertPolarity = invertPolarity;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(AdaptiveThresholdSpec)} must be constructed via its parameterized constructor.");
        }
    }
}

/// <summary>
/// Validated specification for unsharp masking and Sobel gradient magnitude enhancement.
/// </summary>
public readonly record struct UnsharpSobelSpec
{
    private const int MaxRadius = 31;

    private readonly bool _isInitialized;

    public int Radius { get; }
    public float Sigma { get; }
    public float Amount { get; }
    public bool IsInitialized => _isInitialized;

    public UnsharpSobelSpec(int radius = 3, float sigma = 1.5f, float amount = 1.5f)
    {
        if (radius < 1 || radius > MaxRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be in [1, 31].");
        }
        if (!float.IsFinite(sigma) || sigma <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), "Sigma must be finite and > 0.");
        }
        if (!float.IsFinite(amount) || amount < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be finite and >= 0.");
        }

        Radius = radius;
        Sigma = sigma;
        Amount = amount;
        _isInitialized = true;
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(UnsharpSobelSpec)} must be constructed via its parameterized constructor.");
        }
    }
}

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
