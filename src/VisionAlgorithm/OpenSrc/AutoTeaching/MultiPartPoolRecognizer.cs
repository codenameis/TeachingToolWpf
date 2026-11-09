// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/MultiPartPoolRecognizer.cs
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Recognizes multiple distinct part types from a <see cref="PartModelPool"/> in a single image by building
/// a shared <see cref="GranularityPyramid"/> once, evaluating Gate 1 (<see cref="PatMaxEngine"/> geometric score)
/// and Gate 2 (<see cref="RotationInvariantSignature"/> + <see cref="LedoitWolfMahalanobis"/> OOD appearance verification),
/// and resolving spatial overlaps via Cross-Model 2D NMS.
/// </summary>
public static class MultiPartPoolRecognizer
{
    public const float DefaultMinGeometricScore = 0.62f;
    public const float DefaultMinAppearanceCosine = 0.86f;
    public const float DefaultMinSignatureCosine = 0.84f;
    public const float DefaultMaxMahalanobisDistance = 38.0f;
    private const int MaxCandidatesPerModel = 32;

    private readonly record struct RawPartDetection(
        PartPoolEntry Entry,
        FixturePose2D Pose,
        float GeometricScore,
        float AppearanceCosine,
        float MahalanobisDistance,
        float CombinedRankScore);

    /// <summary>
    /// Searches <paramref name="image"/> for all parts registered in <paramref name="pool"/> using a single shared
    /// <see cref="GranularityPyramid"/> and dual-gate OOD rejection.
    /// </summary>
    public static List<DetectedPartInstance> RecognizeAllParts(
        ReadOnlyImageView2D<byte> image,
        PartModelPool pool,
        VisionWorkspace workspace,
        out int sharedPyramidLevels,
        float minGeometricScore = DefaultMinGeometricScore,
        float minAppearanceCosine = DefaultMinAppearanceCosine,
        float maxMahalanobisDistance = DefaultMaxMahalanobisDistance)
    {
        image.EnsureValid();
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(workspace);

        if (pool.Count == 0)
        {
            sharedPyramidLevels = 0;
            return [];
        }

        // Build shared GranularityPyramid ONCE for the entire runtime image
        using GranularityPyramid sharedPyramid = new();
        sharedPyramid.Build(
            image.Pixels,
            image.Width,
            image.Height,
            image.Stride,
            fineGranularity: 1,
            coarseGranularity: 4,
            copyBaseLevel: (long)image.Width * image.Height <= 65536);

        sharedPyramidLevels = sharedPyramid.LevelCount;

        using PatMaxEngine engine = new();
        PatMaxResult[] modelResults = new PatMaxResult[MaxCandidatesPerModel];
        List<RawPartDetection> rawDetections = new(pool.Count * 8);

        Span<float> candAppearance = stackalloc float[PartPoolEntry.AppearanceDimension];
        Span<float> residual = stackalloc float[PartPoolEntry.AppearanceDimension];
        Span<float> whitened = stackalloc float[PartPoolEntry.AppearanceDimension];

        foreach (PartPoolEntry entry in pool.Entries)
        {
            float minSep = MathF.Min(entry.ReferenceWidth, entry.ReferenceHeight) * 0.65f;
            PatMaxSearchParams searchParams = PatMaxSearchParams.Create(
                acceptThreshold: minGeometricScore,
                angleStartRadians: -MathF.PI,
                angleExtentRadians: 2.0f * MathF.PI,
                scaleMin: 0.95f,
                scaleMax: 1.05f,
                runtimeEdgeThreshold: 14.0f,
                ignorePolarity: false,
                maxCandidates: MaxCandidatesPerModel * 2,
                maxResults: MaxCandidatesPerModel,
                minSeparationPixels: minSep);

            int found = engine.FindWithSharedPyramid(
                entry.GeometricModel,
                sharedPyramid,
                image.Pixels,
                image.Width,
                image.Height,
                image.Stride,
                in searchParams,
                modelResults);

            for (int i = 0; i < found; i++)
            {
                PatMaxResult r = modelResults[i];
                if (!r.IsValid || r.Score < minGeometricScore)
                {
                    continue;
                }

                FixturePose2D candPose = new(r.X, r.Y, r.AngleRadians, r.Scale);

                // Gate 2A: Rotation-invariant radial harmonic signature verification (rejects unknown shapes with similar outer boxes)
                RotationInvariantSignature candSig = RotationInvariantSignature.Extract(
                    image,
                    in candPose,
                    entry.ReferenceWidth,
                    entry.ReferenceHeight);

                float sigCos = candSig.CosineSimilarity(entry.Signature);
                if (sigCos < DefaultMinSignatureCosine)
                {
                    continue;
                }

                // Gate 2B: Canonical 16x16 appearance cosine + Ledoit-Wolf Mahalanobis OOD distance
                PartModelPool.ExtractAppearanceVector(
                    image,
                    in candPose,
                    entry.ReferenceWidth,
                    entry.ReferenceHeight,
                    candAppearance);

                ReadOnlySpan<float> proto = entry.AppearancePrototype;
                float appCos = 0f;
                for (int d = 0; d < PartPoolEntry.AppearanceDimension; d++)
                {
                    appCos += candAppearance[d] * proto[d];
                    residual[d] = candAppearance[d] - proto[d];
                }

                if (appCos < minAppearanceCosine)
                {
                    continue;
                }

                float mahaDist = entry.MahalanobisModel.ComputeDistance(candAppearance, proto, whitened);
                if (mahaDist > maxMahalanobisDistance)
                {
                    continue;
                }

                float combinedRank = (0.45f * r.Score) + (0.35f * appCos) + (0.20f * sigCos);
                rawDetections.Add(new RawPartDetection(entry, candPose, r.Score, appCos, mahaDist, combinedRank));
            }
        }

        // Sort descending by CombinedRankScore for Cross-Model 2D NMS
        rawDetections.Sort((a, b) => b.CombinedRankScore.CompareTo(a.CombinedRankScore));

        List<DetectedPartInstance> accepted = new(rawDetections.Count);
        for (int i = 0; i < rawDetections.Count; i++)
        {
            RawPartDetection cand = rawDetections[i];
            float suppressionRadius = 0.60f * MathF.Min(cand.Entry.ReferenceWidth, cand.Entry.ReferenceHeight);
            float minDistSq = suppressionRadius * suppressionRadius;

            bool suppressed = false;
            for (int j = 0; j < accepted.Count; j++)
            {
                float dx = accepted[j].WorldPose.OriginX - cand.Pose.OriginX;
                float dy = accepted[j].WorldPose.OriginY - cand.Pose.OriginY;
                if ((dx * dx) + (dy * dy) <= minDistSq)
                {
                    suppressed = true;
                    break;
                }
            }

            if (suppressed)
            {
                continue;
            }

            // Instantiate child ROIs in world space via FixturePose2D.Compose and LocalToWorld
            IReadOnlyList<ChildRoiTemplateSpec> childSpecs = cand.Entry.ChildSpecs;
            var worldChildren = new InstantiatedChildRoi[childSpecs.Count];
            for (int c = 0; c < childSpecs.Count; c++)
            {
                ChildRoiTemplateSpec spec = childSpecs[c];
                FixturePose2D childWorldPose = cand.Pose.Compose(spec.LocalOffsetPose);
                var worldCenter = cand.Pose.LocalToWorld(spec.LocalOffsetPose.OriginX, spec.LocalOffsetPose.OriginY);
                worldChildren[c] = new InstantiatedChildRoi(
                    spec.ChildName,
                    spec.ToolKind,
                    childWorldPose,
                    worldCenter,
                    spec.Width * cand.Pose.ScaleX,
                    spec.Height * cand.Pose.ScaleY);
            }

            accepted.Add(new DetectedPartInstance(
                cand.Entry.Id,
                cand.Entry.Name,
                cand.Pose,
                cand.GeometricScore,
                cand.AppearanceCosine,
                cand.MahalanobisDistance,
                worldChildren));
        }

        // Sort accepted instances deterministically by PartId then top-to-bottom, left-to-right
        accepted.Sort((a, b) =>
        {
            int cmpId = a.PartId.Value.CompareTo(b.PartId.Value);
            if (cmpId != 0)
            {
                return cmpId;
            }

            int cmpY = a.WorldPose.OriginY.CompareTo(b.WorldPose.OriginY);
            return cmpY != 0 ? cmpY : a.WorldPose.OriginX.CompareTo(b.WorldPose.OriginX);
        });

        return accepted;
    }
}
