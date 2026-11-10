// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJContrastSpec.cs
using System;

namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Immutable specification for ImageJ "Image &gt; Adjust &gt; Brightness/Contrast" (Window/Level &amp; Gamma LUT).
/// </summary>
public readonly record struct ImageJContrastSpec
{
    public const float MinAllowedGamma = 0.10f;
    public const float MaxAllowedGamma = 5.00f;
    public const float NeutralGamma = 1.00f;

    private readonly bool _isInitialized;
    private readonly byte _displayMin;
    private readonly byte _displayMax;
    private readonly float _gamma;

    // [Domain Why]: Prevent default(ImageJContrastSpec) from applying an all-zero (0..0, gamma=0) destructive LUT.
    public bool IsInitialized => _isInitialized;

    public byte DisplayMin => _isInitialized
        ? _displayMin
        : throw new InvalidOperationException("ImageJContrastSpec was not initialized via Create.");

    public byte DisplayMax => _isInitialized
        ? _displayMax
        : throw new InvalidOperationException("ImageJContrastSpec was not initialized via Create.");

    public float Gamma => _isInitialized
        ? _gamma
        : throw new InvalidOperationException("ImageJContrastSpec was not initialized via Create.");

    public bool IsIdentity => _isInitialized &&
        _displayMin == byte.MinValue &&
        _displayMax == byte.MaxValue &&
        MathF.Abs(_gamma - NeutralGamma) < 1e-4f;

    private ImageJContrastSpec(byte displayMin, byte displayMax, float gamma)
    {
        _isInitialized = true;
        _displayMin = displayMin;
        _displayMax = displayMax;
        _gamma = gamma;
    }

    public static ImageJContrastSpec Create(byte displayMin, byte displayMax, float gamma = NeutralGamma)
    {
        if (float.IsNaN(gamma) || float.IsInfinity(gamma))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma must be finite.");
        }

        byte low = Math.Min(displayMin, displayMax);
        byte high = Math.Max(displayMin, displayMax);
        float clampedGamma = Math.Clamp(gamma, MinAllowedGamma, MaxAllowedGamma);
        return new ImageJContrastSpec(low, high, clampedGamma);
    }

    public static ImageJContrastSpec Identity => new(byte.MinValue, byte.MaxValue, NeutralGamma);
}
