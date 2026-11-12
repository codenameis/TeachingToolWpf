namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Trained boundary dipole level centered at the pattern reference origin (US 6,408,109).
/// </summary>
public sealed class PatMaxModelLevel : IDisposable
{
    public int LevelIndex { get; }
    public float ScaleFactor { get; }
    public EdgeDipoleSoA Dipoles { get; }
    public float TotalMagnitudeWeight { get; }

    internal PatMaxModelLevel(int levelIndex, float scaleFactor, EdgeDipoleSoA dipoles, float totalMagnitudeWeight)
    {
        LevelIndex = levelIndex;
        ScaleFactor = scaleFactor;
        Dipoles = dipoles;
        TotalMagnitudeWeight = totalMagnitudeWeight;
    }

    public void Dispose() => Dipoles.Dispose();
}
