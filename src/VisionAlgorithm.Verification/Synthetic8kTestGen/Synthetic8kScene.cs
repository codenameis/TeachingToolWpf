using System.Buffers;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.Synthetic8kTestGen;

/// <summary>
/// Holds a pooled $8192 \times 8192$ (64 Megapixel) procedural inspection scene and its exact analytical ground-truth annotations.
/// </summary>
public sealed class Synthetic8kScene : IDisposable
{
    public const int Resolution8k = 8192;
    public const int TotalPixels8k = Resolution8k * Resolution8k;

    private byte[]? _pooledBuffer;

    public int Width => Resolution8k;
    public int Height => Resolution8k;
    public int Stride => Resolution8k;
    public byte[] Pixels => _pooledBuffer ?? throw new ObjectDisposedException(nameof(Synthetic8kScene));

    // Ground-truth annotations for Algorithm 1 (Metrology)
    public FixturePose2D MetrologyTracePose { get; init; }
    public float GroundTruthTraceWidthPixels { get; init; }
    public Vector2 GroundTruthCircleCenter { get; init; }
    public float GroundTruthCircleRadiusPixels { get; init; }
    public float GroundTruthLineAngleRad { get; init; }

    // Ground-truth annotations for Algorithm 2 (Golden Variation) & Algorithm 6 (PaDiM Patch Anomaly)
    public FixturePose2D[] NormalDiePoses { get; init; } = [];
    public FixturePose2D DefectiveDiePose { get; init; }
    public Vector2 DefectLocalOffsetInDie { get; init; }
    public int DieRoiWidth { get; init; }
    public int DieRoiHeight { get; init; }

    // Ground-truth annotations for Algorithm 3 (RLE Blob & Morphology)
    public (int Left, int Top, int Width, int Height) BlobRegionWindow { get; init; }
    public Vector2 GroundTruthBlobCentroidLocal { get; init; }
    public float GroundTruthBlobRadius { get; init; }

    // Ground-truth annotations for Algorithm 4 (Curvilinear Steger / Bead Continuity)
    public Vector2[] ContinuousBeadWaypoints { get; init; } = [];
    public Vector2[] GappedBeadWaypoints { get; init; } = [];
    public float GroundTruthBeadWidthPixels { get; init; }

    public Synthetic8kScene(byte[] pooledBuffer)
    {
        ArgumentNullException.ThrowIfNull(pooledBuffer);
        _pooledBuffer = pooledBuffer;
    }

    public void Dispose()
    {
        if (_pooledBuffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer, clearArray: false);
            _pooledBuffer = null;
        }
    }
}
