// File: src/VisionAlgorithm/OpenSrc/Common/FixturePose2D.cs
using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Common;

/// <summary>
/// Represents a 2D similarity or anisotropic affine fixture coordinate system $(C_x, C_y, \theta, s_x, s_y)$
/// anchored in continuous image coordinates, where pixel $(c, r)$ has its optical center at $(c + 0.5, r + 0.5)$.
/// </summary>
public readonly record struct FixturePose2D
{
    // [Math Why]: Minimum positive scale bound prevents singular inverse transformation matrices.
    private const float MinimumPositiveScale = 1e-4f;

    private readonly bool _isInitialized;

    /// <summary>Gets the fixture origin X in continuous image coordinates.</summary>
    public float CenterX { get; }

    /// <summary>Gets the fixture origin X alias in continuous image coordinates.</summary>
    public float OriginX => CenterX;

    /// <summary>Gets the fixture origin Y in continuous image coordinates.</summary>
    public float CenterY { get; }

    /// <summary>Gets the fixture origin Y alias in continuous image coordinates.</summary>
    public float OriginY => CenterY;

    /// <summary>Gets the fixture rotation angle in radians (counter-clockwise in image space).</summary>
    public float AngleRad { get; }

    /// <summary>Gets the horizontal scale factor along the local X axis.</summary>
    public float ScaleX { get; }

    /// <summary>Gets the vertical scale factor along the local Y axis.</summary>
    public float ScaleY { get; }

    /// <summary>Gets the isotropic scale factor (geometric mean when anisotropic, or exact scale when isotropic).</summary>
    public float Scale { get; }

    /// <summary>Gets $\cos(\theta)$ precomputed at construction.</summary>
    public float CosTheta { get; }

    /// <summary>Gets $\sin(\theta)$ precomputed at construction.</summary>
    public float SinTheta { get; }

    /// <summary>Gets a value indicating whether this pose was constructed with validated parameters.</summary>
    public bool IsValid => _isInitialized && ScaleX >= MinimumPositiveScale && ScaleY >= MinimumPositiveScale;

    public FixturePose2D(float centerX, float centerY, float angleRad = 0f, float scale = 1f)
        : this(centerX, centerY, angleRad, scale, scale)
    {
    }

    public FixturePose2D(float centerX, float centerY, float angleRad, float scaleX, float scaleY)
    {
        if (!float.IsFinite(centerX) || !float.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "Fixture center coordinates must be finite.");
        }

        if (!float.IsFinite(angleRad))
        {
            throw new ArgumentOutOfRangeException(nameof(angleRad), "Fixture angle must be finite.");
        }

        if (!float.IsFinite(scaleX) || scaleX < MinimumPositiveScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX), $"Fixture scaleX must be finite and >= {MinimumPositiveScale}.");
        }

        if (!float.IsFinite(scaleY) || scaleY < MinimumPositiveScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scaleY), $"Fixture scaleY must be finite and >= {MinimumPositiveScale}.");
        }

        CenterX = centerX;
        CenterY = centerY;
        AngleRad = angleRad;
        ScaleX = scaleX;
        ScaleY = scaleY;
        Scale = scaleX == scaleY ? scaleX : MathF.Sqrt(scaleX * scaleY);
        (SinTheta, CosTheta) = MathF.SinCos(angleRad);
        _isInitialized = true;
    }

    /// <summary>Gets the identity fixture pose at the image origin $(0, 0)$ with $\theta = 0$ and $s_x = s_y = 1$.</summary>
    public static FixturePose2D Identity => new(0f, 0f, 0f, 1f, 1f);

    /// <summary>Creates an anisotropic fixture pose with independent X and Y scale factors.</summary>
    public static FixturePose2D CreateAnisotropic(float centerX, float centerY, float angleRad, float scaleX, float scaleY)
        => new(centerX, centerY, angleRad, scaleX, scaleY);

    /// <summary>
    /// Maps a local fixture coordinate $(u, v)$ to continuous world image coordinates $(x, y)$:
    /// $x = C_x + (s_x u \cos\theta - s_y v \sin\theta)$, $y = C_y + (s_x u \sin\theta + s_y v \cos\theta)$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 LocalToWorld(float localX, float localY)
    {
        EnsureValid();
        float scaledX = localX * ScaleX;
        float scaledY = localY * ScaleY;
        return new Vector2(
            CenterX + ((scaledX * CosTheta) - (scaledY * SinTheta)),
            CenterY + ((scaledX * SinTheta) + (scaledY * CosTheta)));
    }

    /// <summary>
    /// Maps a continuous world image coordinate $(x, y)$ back to local fixture coordinates $(u, v)$:
    /// $u = s_x^{-1} (\Delta x \cos\theta + \Delta y \sin\theta)$, $v = s_y^{-1} (-\Delta x \sin\theta + \Delta y \cos\theta)$.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 WorldToLocal(float worldX, float worldY)
    {
        EnsureValid();
        float dx = worldX - CenterX;
        float dy = worldY - CenterY;
        return new Vector2(
            ((dx * CosTheta) + (dy * SinTheta)) / ScaleX,
            ((-dx * SinTheta) + (dy * CosTheta)) / ScaleY);
    }

    /// <summary>
    /// Rotates a local unit direction vector $(dx, dy)$ into world image space without translation or scaling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 RotateDirectionToWorld(float localDirX, float localDirY)
    {
        EnsureValid();
        return new Vector2(
            (localDirX * CosTheta) - (localDirY * SinTheta),
            (localDirX * SinTheta) + (localDirY * CosTheta));
    }

    /// <summary>
    /// Composes a local child fixture offset <paramref name="localDelta"/> $(\Delta u, \Delta v, \Delta \theta, s_{cx}, s_{cy})$
    /// onto this parent fixture pose to produce the child's world fixture pose.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixturePose2D Compose(in FixturePose2D localDelta)
    {
        EnsureValid();
        localDelta.EnsureValid();
        Vector2 worldCenter = LocalToWorld(localDelta.CenterX, localDelta.CenterY);
        return new FixturePose2D(
            worldCenter.X,
            worldCenter.Y,
            AngleRad + localDelta.AngleRad,
            ScaleX * localDelta.ScaleX,
            ScaleY * localDelta.ScaleY);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if this struct is an uninitialized <c>default(FixturePose2D)</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("FixturePose2D was not initialized via its constructor.");
        }
    }
}
