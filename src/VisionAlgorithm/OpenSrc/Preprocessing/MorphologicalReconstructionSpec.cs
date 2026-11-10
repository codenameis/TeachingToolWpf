// File: src/VisionAlgorithm/OpenSrc/Preprocessing/MorphologicalReconstructionSpec.cs
namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Immutable specification for Grayscale Morphological Reconstruction and Hole/Peak Suppression
/// (HALCON <c>gray_erosion_shape</c> + <c>fill_up</c> / Vincent 1993 hybrid sequential + queue reconstruction).
/// </summary>
public readonly record struct MorphologicalReconstructionSpec
{
    public const int DefaultMarkerRadius = 3;
    private const int MaxSupportedMarkerRadius = 31;

    private readonly bool _isInitialized;

    public int MarkerRadius { get; }
    public bool IsFillDarkHoles { get; }
    public bool IsInitialized => _isInitialized;

    private MorphologicalReconstructionSpec(int markerRadius, bool isFillDarkHoles)
    {
        MarkerRadius = markerRadius;
        IsFillDarkHoles = isFillDarkHoles;
        _isInitialized = true;
    }

    public static MorphologicalReconstructionSpec Create(
        int markerRadius = DefaultMarkerRadius,
        bool isFillDarkHoles = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(markerRadius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(markerRadius, MaxSupportedMarkerRadius);

        return new MorphologicalReconstructionSpec(markerRadius, isFillDarkHoles);
    }

    public void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException($"{nameof(MorphologicalReconstructionSpec)} must be constructed via {nameof(Create)}.");
        }
    }
}
