// File: src/VisionAlgorithm/OpenSrc/ImageJ/ImageJParticleFilterSpec.cs
using System;

namespace VisionAlgorithm.OpenSrc.ImageJ;

/// <summary>
/// Immutable filter criteria for ImageJ "Analyze &gt; Analyze Particles" (size range, circularity range, edge exclusion).
/// </summary>
public readonly record struct ImageJParticleFilterSpec
{
    public const int DefaultMinAreaPixels = 8;
    public const int DefaultMaxAreaPixels = 10_000_000;
    public const float MinCircularityBound = 0.0f;
    public const float MaxCircularityBound = 1.0f;

    private readonly bool _isInitialized;
    private readonly int _minAreaPixels;
    private readonly int _maxAreaPixels;
    private readonly float _minCircularity;
    private readonly float _maxCircularity;
    private readonly bool _shouldExcludeEdgeParticles;

    // [Domain Why]: Guard against default(ImageJParticleFilterSpec) having MaxAreaPixels = 0 and rejecting all particles.
    public bool IsInitialized => _isInitialized;

    public int MinAreaPixels => _isInitialized
        ? _minAreaPixels
        : throw new InvalidOperationException("ImageJParticleFilterSpec was not initialized via Create.");

    public int MaxAreaPixels => _isInitialized
        ? _maxAreaPixels
        : throw new InvalidOperationException("ImageJParticleFilterSpec was not initialized via Create.");

    public float MinCircularity => _isInitialized
        ? _minCircularity
        : throw new InvalidOperationException("ImageJParticleFilterSpec was not initialized via Create.");

    public float MaxCircularity => _isInitialized
        ? _maxCircularity
        : throw new InvalidOperationException("ImageJParticleFilterSpec was not initialized via Create.");

    public bool ShouldExcludeEdgeParticles => _isInitialized
        ? _shouldExcludeEdgeParticles
        : throw new InvalidOperationException("ImageJParticleFilterSpec was not initialized via Create.");

    private ImageJParticleFilterSpec(
        int minAreaPixels,
        int maxAreaPixels,
        float minCircularity,
        float maxCircularity,
        bool shouldExcludeEdgeParticles)
    {
        _isInitialized = true;
        _minAreaPixels = minAreaPixels;
        _maxAreaPixels = maxAreaPixels;
        _minCircularity = minCircularity;
        _maxCircularity = maxCircularity;
        _shouldExcludeEdgeParticles = shouldExcludeEdgeParticles;
    }

    public static ImageJParticleFilterSpec Create(
        int minAreaPixels = DefaultMinAreaPixels,
        int maxAreaPixels = DefaultMaxAreaPixels,
        float minCircularity = MinCircularityBound,
        float maxCircularity = MaxCircularityBound,
        bool shouldExcludeEdgeParticles = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minAreaPixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAreaPixels, minAreaPixels);

        float clampedMinCirc = Math.Clamp(minCircularity, MinCircularityBound, MaxCircularityBound);
        float clampedMaxCirc = Math.Clamp(maxCircularity, clampedMinCirc, MaxCircularityBound);

        return new ImageJParticleFilterSpec(
            minAreaPixels,
            maxAreaPixels,
            clampedMinCirc,
            clampedMaxCirc,
            shouldExcludeEdgeParticles);
    }

    public static ImageJParticleFilterSpec Default => Create();
}
