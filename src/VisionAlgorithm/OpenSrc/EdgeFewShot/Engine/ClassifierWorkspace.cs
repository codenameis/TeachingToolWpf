// File: src/VisionAlgorithm/OpenSrc/EdgeFewShot/Engine/ClassifierWorkspace.cs
using System.Buffers;
using VisionAlgorithm.OpenSrc.EdgeFewShot.Backbone;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Reusable <see cref="ArrayPool{T}"/>-backed workspace guaranteeing 0-byte GC heap allocation
/// during steady-state <see cref="EdgeFewShotClassifier.Predict"/> execution.
/// </summary>
public sealed class ClassifierWorkspace : IDisposable
{
    private float[]? _rentedBuffer;
    private int _roiFloats;
    private int _layer2Floats;
    private int _layer3Floats;
    private int _scratchFloats;
    private int _featureDimension;
    private int _prototypeCapacity;
    private bool _disposed;

    public int TotalFloatCapacity { get; private set; }

    public void EnsureCapacity(
        int roiWidth,
        int roiHeight,
        IFeatureMapExtractor extractor,
        int prototypeCount,
        int featureDimension = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roiHeight);
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prototypeCount);

        var (w2, h2) = extractor.GetLayer2Dimensions(roiWidth, roiHeight);
        var (w3, h3) = extractor.GetLayer3Dimensions(roiWidth, roiHeight);

        var roiFloats = checked(roiWidth * roiHeight);
        var layer2Floats = checked(extractor.Layer2Channels * w2 * h2);
        var layer3Floats = checked(extractor.Layer3Channels * w3 * h3);
        var upsampleScratch = checked(w2 * h2);
        var extractorScratch = extractor.GetRequiredScratchFloats(roiWidth, roiHeight);
        var scratchFloats = System.Math.Max(upsampleScratch, extractorScratch);
        var resolvedFeatureDim = featureDimension > 0
            ? featureDimension
            : GemSpatialPooler.GetPooledFeatureDimension(extractor.Layer2Channels, extractor.Layer3Channels);

        // [Perf Why]: Single contiguous pooled array partitioned into 8 non-overlapping Span<float> slices for L1/L2 cache locality.
        var requiredTotal = checked(
            roiFloats
            + layer2Floats
            + layer3Floats
            + scratchFloats
            + (resolvedFeatureDim * 3)
            + prototypeCount);

        _roiFloats = roiFloats;
        _layer2Floats = layer2Floats;
        _layer3Floats = layer3Floats;
        _scratchFloats = scratchFloats;
        _featureDimension = resolvedFeatureDim;
        _prototypeCapacity = prototypeCount;

        if (_rentedBuffer is not null && (TotalFloatCapacity >= requiredTotal))
        {
            return;
        }

        if (_rentedBuffer is not null)
        {
            ArrayPool<float>.Shared.Return(_rentedBuffer);
        }

        _rentedBuffer = ArrayPool<float>.Shared.Rent(requiredTotal);
        TotalFloatCapacity = _rentedBuffer.Length;
    }

    public Span<float> RoiSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _rentedBuffer.AsSpan(0, _roiFloats);
        }
    }

    public Span<float> Layer2Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _rentedBuffer.AsSpan(_roiFloats, _layer2Floats);
        }
    }

    public Span<float> Layer3Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _rentedBuffer.AsSpan(_roiFloats + _layer2Floats, _layer3Floats);
        }
    }

    public Span<float> ScratchSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _rentedBuffer.AsSpan(_roiFloats + _layer2Floats + _layer3Floats, _scratchFloats);
        }
    }

    public Span<float> RawFeatureSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var offset = _roiFloats + _layer2Floats + _layer3Floats + _scratchFloats;
            return _rentedBuffer.AsSpan(offset, _featureDimension);
        }
    }

    public Span<float> NormalizedFeatureSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var offset = _roiFloats + _layer2Floats + _layer3Floats + _scratchFloats + _featureDimension;
            return _rentedBuffer.AsSpan(offset, _featureDimension);
        }
    }

    public Span<float> DifferenceScratchSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var offset = _roiFloats + _layer2Floats + _layer3Floats + _scratchFloats + (_featureDimension * 2);
            return _rentedBuffer.AsSpan(offset, _featureDimension);
        }
    }

    public Span<float> PrototypeDistancesSpan
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var offset = _roiFloats + _layer2Floats + _layer3Floats + _scratchFloats + (_featureDimension * 3);
            return _rentedBuffer.AsSpan(offset, _prototypeCapacity);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var buf = _rentedBuffer;
        _rentedBuffer = null;
        TotalFloatCapacity = 0;
        if (buf is not null)
        {
            ArrayPool<float>.Shared.Return(buf);
        }
    }
}
