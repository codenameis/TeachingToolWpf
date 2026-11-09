// File: src/VisionAlgorithm/OpenSrc/PatMax/PatMaxModel.cs
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

/// <summary>
/// Trained multi-scale PatMax geometric model owning Coarse-to-Fine dipole levels, stability diagnostics, and pre-generated 2D Attraction Field (US 6,408,109 / US 7,065,262 / US 6,993,177).
/// </summary>
public sealed class PatMaxModel : IDisposable
{
    private readonly PatMaxModelLevel[] _levels;
    private bool _disposed;

    public int PatternWidth { get; }
    public int PatternHeight { get; }
    public float OriginX { get; }
    public float OriginY { get; }
    public float MinDx { get; }
    public float MaxDx { get; }
    public float MinDy { get; }
    public float MaxDy { get; }
    public PatMaxTrainParams TrainParams { get; }
    public StabilityReport Stability { get; }
    public AttractionField FineAttractionField { get; }

    public int LevelCount => _levels.Length;
    public bool IsTrained => !_disposed && _levels.Length > 0 && !_levels[0].Dipoles.IsEmpty && !Stability.HasInsufficientFeatures;

    public PatMaxModelLevel FineLevel => GetLevel(0);
    public PatMaxModelLevel CoarseLevel => GetLevel(_levels.Length - 1);

    internal PatMaxModel(
        int patternWidth,
        int patternHeight,
        float originX,
        float originY,
        in PatMaxTrainParams trainParams,
        in StabilityReport stability,
        PatMaxModelLevel[] levels,
        AttractionField fineAttractionField)
    {
        PatternWidth = patternWidth;
        PatternHeight = patternHeight;
        OriginX = originX;
        OriginY = originY;
        TrainParams = trainParams;
        Stability = stability;
        _levels = levels;
        FineAttractionField = fineAttractionField;

        if (levels.Length > 0 && !levels[0].Dipoles.IsEmpty)
        {
            AttractionFieldBuilder.ComputeDipoleBounds(levels[0].Dipoles, out var minDx, out var maxDx, out var minDy, out var maxDy);
            MinDx = minDx;
            MaxDx = maxDx;
            MinDy = minDy;
            MaxDy = maxDy;
        }
        else
        {
            MinDx = -originX;
            MaxDx = patternWidth - originX;
            MinDy = -originY;
            MaxDy = patternHeight - originY;
        }
    }

    public PatMaxModelLevel GetLevel(int levelIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(levelIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(levelIndex, _levels.Length);
        return _levels[levelIndex];
    }

    /// <summary>
    /// Projects trained model boundary dipoles into image space at a given pose for UI teaching preview.
    /// </summary>
    public int ProjectDipolesToSpan(
        int levelIndex,
        float poseX,
        float poseY,
        float angleRadians,
        float scale,
        Span<EdgeDipole> destination)
    {
        var level = GetLevel(levelIndex);
        var dipoles = level.Dipoles;
        var count = Math.Min(dipoles.Count, destination.Length);
        var (sin, cos) = MathF.SinCos(angleRadians);

        var xSpan = dipoles.X;
        var ySpan = dipoles.Y;
        var cosSpan = dipoles.CosTheta;
        var sinSpan = dipoles.SinTheta;
        var magSpan = dipoles.Magnitude;

        for (var i = 0; i < count; i++)
        {
            var mx = xSpan[i] * scale;
            var my = ySpan[i] * scale;
            var rx = (mx * cos) - (my * sin) + poseX;
            var ry = (mx * sin) + (my * cos) + poseY;

            var nCos = (cosSpan[i] * cos) - (sinSpan[i] * sin);
            var nSin = (cosSpan[i] * sin) + (sinSpan[i] * cos);
            destination[i] = new EdgeDipole(rx, ry, nCos, nSin, magSpan[i]);
        }

        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _levels.Length; i++)
        {
            _levels[i].Dispose();
        }

        FineAttractionField.Dispose();
    }
}
