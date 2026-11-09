// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/PartPoolEntry.cs
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Immutable master entry in <see cref="PartModelPool"/> storing a part's unique <see cref="PartModelId"/>,
/// 64-byte <see cref="RotationInvariantSignature"/>, trained <see cref="PatMaxModel"/>, canonical $16 \times 16$
/// appearance prototype and <see cref="LedoitWolfMahalanobis"/> OOD covariance model, and relative <see cref="ChildRoiTemplateSpec"/> list.
/// </summary>
public sealed class PartPoolEntry : IDisposable
{
    public const int AppearanceGridSize = 16;
    public const int AppearanceDimension = AppearanceGridSize * AppearanceGridSize; // 256

    private readonly float[] _appearancePrototype;
    private readonly List<ChildRoiTemplateSpec> _childSpecs;
    private bool _disposed;

    public PartModelId Id { get; }
    public string Name { get; }
    public float ReferenceWidth { get; }
    public float ReferenceHeight { get; }
    public RotationInvariantSignature Signature { get; private set; }
    public PatMaxModel GeometricModel { get; }
    public ReadOnlySpan<float> AppearancePrototype => _appearancePrototype;
    public LedoitWolfMahalanobis MahalanobisModel { get; }
    public IReadOnlyList<ChildRoiTemplateSpec> ChildSpecs => _childSpecs;
    public int TeachMergeCount { get; private set; } = 1;

    public PartPoolEntry(
        PartModelId id,
        string name,
        float referenceWidth,
        float referenceHeight,
        RotationInvariantSignature signature,
        PatMaxModel geometricModel,
        float[] appearancePrototype,
        LedoitWolfMahalanobis mahalanobisModel,
        IEnumerable<ChildRoiTemplateSpec>? childSpecs = null)
    {
        ArgumentNullException.ThrowIfNull(geometricModel);
        ArgumentNullException.ThrowIfNull(appearancePrototype);
        ArgumentNullException.ThrowIfNull(mahalanobisModel);

        if (appearancePrototype.Length != AppearanceDimension)
        {
            throw new ArgumentException($"Appearance prototype must have length {AppearanceDimension}.", nameof(appearancePrototype));
        }

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? $"Part_{id}" : name;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        Signature = signature;
        GeometricModel = geometricModel;
        _appearancePrototype = appearancePrototype;
        MahalanobisModel = mahalanobisModel;
        _childSpecs = childSpecs is not null ? new List<ChildRoiTemplateSpec>(childSpecs) : [];
    }

    internal void MergeRetaughtSpecs(
        in RotationInvariantSignature newSignature,
        ReadOnlySpan<ChildRoiTemplateSpec> additionalChildSpecs)
    {
        TeachMergeCount++;

        // Blend signature with running average and L2-renormalize
        Span<float> blended = stackalloc float[RotationInvariantSignature.FloatCount];
        ReadOnlySpan<float> oldSpan = Signature.AsSpan();
        ReadOnlySpan<float> newSpan = newSignature.AsSpan();
        float wOld = (float)(TeachMergeCount - 1) / TeachMergeCount;
        float wNew = 1.0f / TeachMergeCount;
        float normSq = 0f;

        for (int i = 0; i < RotationInvariantSignature.FloatCount; i++)
        {
            float v = (oldSpan[i] * wOld) + (newSpan[i] * wNew);
            blended[i] = v;
            normSq += v * v;
        }

        float invNorm = normSq > 1e-12f ? (1.0f / MathF.Sqrt(normSq)) : 1.0f;
        for (int i = 0; i < RotationInvariantSignature.FloatCount; i++)
        {
            blended[i] *= invNorm;
        }

        Signature = new RotationInvariantSignature(blended);

        for (int i = 0; i < additionalChildSpecs.Length; i++)
        {
            ChildRoiTemplateSpec candidate = additionalChildSpecs[i];
            bool exists = false;
            for (int j = 0; j < _childSpecs.Count; j++)
            {
                if (string.Equals(_childSpecs[j].ChildName, candidate.ChildName, StringComparison.Ordinal))
                {
                    _childSpecs[j] = candidate;
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _childSpecs.Add(candidate);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GeometricModel.Dispose();
    }
}
