// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/PartModelPool.cs
using System.Buffers.Binary;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Manages an accumulating pool of taught part masters (<c>#1</c>, <c>#2</c>, <c>#3</c>, ...), automatically
/// merging duplicate or rotated re-teachings via 64-byte <see cref="RotationInvariantSignature"/> + <see cref="PatMaxEngine"/>
/// alignment verification, and providing fast first-stage candidate filtering.
/// </summary>
public sealed class PartModelPool : IDisposable
{
    public const float DefaultMergeSignatureThreshold = 0.91f;
    public const float DefaultMergePatMaxScoreThreshold = 0.62f;
    private const int SyntheticPerturbationSamples = 12;

    private readonly List<PartPoolEntry> _entries = [];
    private bool _disposed;

    public int Count => _entries.Count;
    public IReadOnlyList<PartPoolEntry> Entries => _entries;

    /// <summary>
    /// Teaches a part ROI on <paramref name="image"/> anchored at <paramref name="teachPose"/>.
    /// If an equivalent part already exists in the pool (even at a different rotation angle), automatically merges
    /// into the existing <see cref="PartModelId"/> instead of creating a duplicate ID.
    /// </summary>
    public PartTeachRegistrationResult RegisterOrMergeOnTeach(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D teachPose,
        float roiWidth,
        float roiHeight,
        string preferredName,
        ReadOnlySpan<ChildRoiTemplateSpec> childSpecs,
        VisionWorkspace workspace,
        float mergeSignatureThreshold = DefaultMergeSignatureThreshold)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        image.EnsureValid();
        teachPose.EnsureValid();
        ArgumentNullException.ThrowIfNull(workspace);

        RotationInvariantSignature candSig = RotationInvariantSignature.Extract(image, in teachPose, roiWidth, roiHeight);

        // Check existing pool entries for rotation-invariant signature + geometric/appearance match
        for (int i = 0; i < _entries.Count; i++)
        {
            PartPoolEntry existing = _entries[i];
            float sizeRatioW = MathF.Min(roiWidth, existing.ReferenceWidth) / MathF.Max(roiWidth, existing.ReferenceWidth);
            float sizeRatioH = MathF.Min(roiHeight, existing.ReferenceHeight) / MathF.Max(roiHeight, existing.ReferenceHeight);
            if (MathF.Min(sizeRatioW, sizeRatioH) < 0.78f)
            {
                continue;
            }

            float sigSim = candSig.CosineSimilarity(existing.Signature);
            if (sigSim >= mergeSignatureThreshold)
            {
                existing.MergeRetaughtSpecs(in candSig, childSpecs);
                return new PartTeachRegistrationResult(
                    existing.Id,
                    WasMergedIntoExisting: true,
                    sigSim,
                    _entries.Count);
            }
        }

        // Extract local crop around teachPose using exact LocalToWorld centered at (cropW * 0.5f, cropH * 0.5f)
        int pad = 12;
        int cropW = Math.Max(24, (int)MathF.Ceiling(roiWidth) + (2 * pad));
        int cropH = Math.Max(24, (int)MathF.Ceiling(roiHeight) + (2 * pad));
        Span<byte> cropBytes = workspace.GetByteBufferA(checked(cropW * cropH));
        float halfCropW = cropW * 0.5f;
        float halfCropH = cropH * 0.5f;
        for (int cy = 0; cy < cropH; cy++)
        {
            float ly = cy - halfCropH;
            int rowOff = cy * cropW;
            for (int cx = 0; cx < cropW; cx++)
            {
                float lx = cx - halfCropW;
                var wpt = teachPose.LocalToWorld(lx, ly);
                float sample = SubpixelInterpolator.SampleBilinear(image, wpt.X, wpt.Y);
                cropBytes[rowOff + cx] = (byte)Math.Clamp((int)MathF.Round(sample), 0, 255);
            }
        }

        PatMaxTrainParams trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 4,
            fineGranularity: 1,
            edgeThreshold: 14.0f,
            elasticityPixels: 2.0f);

        PatMaxModel patMaxModel = PatMaxTrainer.Train(
            cropBytes,
            cropW,
            cropH,
            cropW,
            in trainParams,
            customOriginX: halfCropW,
            customOriginY: halfCropH);

        // Extract canonical 16x16 CL2N appearance prototype and fit Ledoit-Wolf Mahalanobis covariance
        float[] prototype = new float[PartPoolEntry.AppearanceDimension];
        ExtractAppearanceVector(image, in teachPose, roiWidth, roiHeight, prototype);

        LedoitWolfMahalanobis mahalanobis = FitLocalAppearanceMahalanobis(
            image,
            in teachPose,
            roiWidth,
            roiHeight,
            prototype);

        PartModelId newId = new(_entries.Count + 1);
        PartPoolEntry newEntry = new(
            newId,
            preferredName,
            roiWidth,
            roiHeight,
            candSig,
            patMaxModel,
            prototype,
            mahalanobis,
            childSpecs.ToArray());

        newEntry.BuildIcgnTemplate(image, in teachPose, roiWidth, roiHeight);
        _entries.Add(newEntry);
        return new PartTeachRegistrationResult(
            newId,
            WasMergedIntoExisting: false,
            MatchedSimilarity: 1.0f,
            _entries.Count);
    }

    /// <summary>
    /// Extracts the unit-L2 zero-mean $16 \times 16$ canonical appearance vector for a candidate part pose.
    /// </summary>
    public static void ExtractAppearanceVector(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D pose,
        float roiWidth,
        float roiHeight,
        Span<float> appearanceOut)
    {
        const int gs = PartPoolEntry.AppearanceGridSize;
        const int len = PartPoolEntry.AppearanceDimension;

        float halfW = roiWidth * 0.5f;
        float halfH = roiHeight * 0.5f;
        float sum = 0f;

        for (int gy = 0; gy < gs; gy++)
        {
            float ly = (((gy + 0.5f) / gs) * roiHeight) - halfH;
            for (int gx = 0; gx < gs; gx++)
            {
                float lx = (((gx + 0.5f) / gs) * roiWidth) - halfW;
                var worldPt = pose.LocalToWorld(lx, ly);
                float val = SubpixelInterpolator.SampleBilinear(image, worldPt.X, worldPt.Y);
                appearanceOut[(gy * gs) + gx] = val;
                sum += val;
            }
        }

        float mean = sum / len;
        float normSq = 0f;
        for (int i = 0; i < len; i++)
        {
            float centered = appearanceOut[i] - mean;
            appearanceOut[i] = centered;
            normSq += centered * centered;
        }

        float invNorm = normSq > 1e-8f ? (1.0f / MathF.Sqrt(normSq)) : 0f;
        for (int i = 0; i < len; i++)
        {
            appearanceOut[i] *= invNorm;
        }
    }

    /// <summary>
    /// Serializes the pool metadata and 64-byte rotation-invariant signatures to a compact binary byte array.
    /// </summary>
    public byte[] SerializeMetadataToBytes()
    {
        using MemoryStream ms = new();
        using BinaryWriter bw = new(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(0x50504F4C); // 'PPOL' magic
        bw.Write(_entries.Count);

        foreach (PartPoolEntry entry in _entries)
        {
            bw.Write(entry.Id.Value);
            bw.Write(entry.Name);
            bw.Write(entry.ReferenceWidth);
            bw.Write(entry.ReferenceHeight);
            bw.Write(entry.TeachMergeCount);
            ReadOnlySpan<float> sig = entry.Signature.AsSpan();
            for (int i = 0; i < sig.Length; i++)
            {
                bw.Write(sig[i]);
            }

            bw.Write(entry.ChildSpecs.Count);
            for (int c = 0; c < entry.ChildSpecs.Count; c++)
            {
                ChildRoiTemplateSpec cs = entry.ChildSpecs[c];
                bw.Write(cs.ChildName);
                bw.Write((byte)cs.ToolKind);
                bw.Write(cs.LocalOffsetPose.OriginX);
                bw.Write(cs.LocalOffsetPose.OriginY);
                bw.Write(cs.LocalOffsetPose.AngleRad);
                bw.Write(cs.Width);
                bw.Write(cs.Height);
            }
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static LedoitWolfMahalanobis FitLocalAppearanceMahalanobis(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D basePose,
        float roiWidth,
        float roiHeight,
        ReadOnlySpan<float> meanPrototype)
    {
        const int dim = PartPoolEntry.AppearanceDimension;
        float[] residuals = new float[SyntheticPerturbationSamples * dim];
        Span<float> sampleVec = stackalloc float[dim];

        // Sample sub-pixel pose perturbations around the taught part to capture realistic interpolation/noise covariance
        ReadOnlySpan<(float dx, float dy, float dAng)> offsets =
        [
            (-0.35f, -0.35f, -0.01f),
            (0.35f, -0.35f, 0.01f),
            (-0.35f, 0.35f, 0.01f),
            (0.35f, 0.35f, -0.01f),
            (-0.20f, 0.0f, 0.0f),
            (0.20f, 0.0f, 0.0f),
            (0.0f, -0.20f, 0.0f),
            (0.0f, 0.20f, 0.0f),
            (0.15f, 0.15f, 0.008f),
            (-0.15f, -0.15f, -0.008f),
            (0.25f, -0.20f, 0.005f),
            (-0.25f, 0.20f, -0.005f)
        ];

        for (int n = 0; n < SyntheticPerturbationSamples; n++)
        {
            var (dx, dy, dAng) = offsets[n];
            FixturePose2D perturbed = new(
                basePose.OriginX + dx,
                basePose.OriginY + dy,
                basePose.AngleRad + dAng,
                basePose.Scale);

            ExtractAppearanceVector(image, in perturbed, roiWidth, roiHeight, sampleVec);
            Span<float> resRow = residuals.AsSpan(n * dim, dim);
            for (int d = 0; d < dim; d++)
            {
                resRow[d] = sampleVec[d] - meanPrototype[d];
            }
        }

        return LedoitWolfMahalanobis.FitFromCenteredResiduals(residuals, SyntheticPerturbationSamples, dim);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (PartPoolEntry entry in _entries)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }
}
