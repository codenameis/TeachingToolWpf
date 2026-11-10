// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/MultiPartPoolRecognizer.cs
using VisionAlgorithm.OpenSrc.BlobRle;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Recognizes multiple distinct part types from a <see cref="PartModelPool"/> in a single image using a 3-stage
/// proposal-guided architecture:
/// <list type="number">
/// <item><description>Coarse stride-4 edge-energy clustering via <see cref="RleMorphology.ClosingBox"/> and <see cref="RleBlobAnalyzer.AnalyzeComponents"/> to skip empty background (&gt;85% of pixels).</description></item>
/// <item><description>64-byte <see cref="RotationInvariantSignature"/> multi-grid pre-screening to reject unknown distractors and prune non-matching part models before <c>PatQuick</c> rotation scanning.</description></item>
/// <item><description>Tight local-window <see cref="PatMaxEngine"/> geometric alignment + canonical Inverse-Compositional Gauss-Newton (ICGN) sub-pixel refinement + <see cref="LedoitWolfMahalanobis"/> OOD verification and Cross-Model 2D NMS.</description></item>
/// </list>
/// </summary>
public static class MultiPartPoolRecognizer
{
    public const float DefaultMinGeometricScore = 0.62f;
    public const float DefaultMinAppearanceCosine = 0.86f;
    public const float DefaultMinSignatureCosine = 0.84f;
    public const float DefaultMaxMahalanobisDistance = 38.0f;

    private const float ProposalPreScreenSignatureFloor = 0.72f;
    private const float ProposalModelRunnerUpMargin = 0.05f;
    private const int CoarseGridStride = 4;
    private const int CoarseEdgeGradientThreshold = 18;
    private const int CoarseClosingRadius = 7;
    private const int LocalWindowPaddingPixels = 48;
    private const int MaxLocalWindowSize = 248;
    private const int MaxCandidatesPerWindow = 3;

    private readonly record struct ProposalCandidateIndex(
        int EntryIndex,
        float RefinedCenterX,
        float RefinedCenterY,
        float PreScreenSignatureCosine);

    private readonly record struct RawPartDetection(
        PartPoolEntry Entry,
        FixturePose2D Pose,
        float GeometricScore,
        float AppearanceCosine,
        float MahalanobisDistance,
        float CombinedRankScore);

    /// <summary>
    /// Searches <paramref name="image"/> for all parts registered in <paramref name="pool"/> using coarse edge-cluster
    /// object proposals, <see cref="RotationInvariantSignature"/> pre-screening, local-window <see cref="PatMaxEngine"/>,
    /// and dual-gate OOD rejection.
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

        sharedPyramidLevels = 3;

        // Stage 1: Extract coarse edge-energy object proposals (skips >85% empty background and small noise/distractors)
        float minModelSize = float.MaxValue;
        for (int i = 0; i < pool.Entries.Count; i++)
        {
            PartPoolEntry e = pool.Entries[i];
            float minDim = MathF.Min(e.ReferenceWidth, e.ReferenceHeight);
            if (minDim < minModelSize)
            {
                minModelSize = minDim;
            }
        }

        List<(float CenterX, float CenterY)> proposals = ExtractCoarseEdgeProposals(image, minModelSize, workspace);
        if (proposals.Count == 0)
        {
            return [];
        }

        // Stage 2: Pre-screen each (Proposal, PartPoolEntry) pair using 64-byte RotationInvariantSignature
        List<ProposalCandidateIndex> survivingPairs = new(proposals.Count * 2);
        Span<ProposalCandidateIndex> perProposalBuffer = stackalloc ProposalCandidateIndex[Math.Min(pool.Count, 32)];

        for (int p = 0; p < proposals.Count; p++)
        {
            var (propX, propY) = proposals[p];
            int countForProp = PreScreenProposalWithSignatures(image, propX, propY, pool.Entries, perProposalBuffer);
            for (int k = 0; k < countForProp; k++)
            {
                survivingPairs.Add(perProposalBuffer[k]);
            }
        }

        if (survivingPairs.Count == 0)
        {
            return [];
        }

        // Stage 3: Run tight local-window PatMaxEngine + ICGN sub-pixel refinement only on surviving (Proposal, Entry) pairs
        List<RawPartDetection> rawDetections = EvaluateSurvivingPairs(
            image,
            pool.Entries,
            survivingPairs,
            minGeometricScore,
            minAppearanceCosine,
            maxMahalanobisDistance);

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

    private static unsafe List<RawPartDetection> EvaluateSurvivingPairs(
        ReadOnlyImageView2D<byte> image,
        IReadOnlyList<PartPoolEntry> entries,
        List<ProposalCandidateIndex> survivingPairs,
        float minGeometricScore,
        float minAppearanceCosine,
        float maxMahalanobisDistance)
    {
        int pairCount = survivingPairs.Count;
        var slotDetections = new RawPartDetection[pairCount];
        var slotValid = new bool[pairCount];

        int imgW = image.Width;
        int imgH = image.Height;
        int imgStride = image.Stride;
        int bufferLen = ((imgH - 1) * imgStride) + imgW;

        fixed (byte* pPixels = image.Pixels)
        {
            nint pixelsAddr = (nint)pPixels;

            if (pairCount == 1)
            {
                using PatMaxEngine singleEngine = new();
                EvaluateSinglePair(
                    pixelsAddr, bufferLen, imgW, imgH, imgStride,
                    entries, survivingPairs[0], singleEngine,
                    minGeometricScore, minAppearanceCosine, maxMahalanobisDistance,
                    out slotDetections[0], out slotValid[0]);
            }
            else
            {
                Parallel.For(
                    0,
                    pairCount,
                    () => new PatMaxEngine(),
                    (s, _, localEngine) =>
                    {
                        EvaluateSinglePair(
                            pixelsAddr, bufferLen, imgW, imgH, imgStride,
                            entries, survivingPairs[s], localEngine,
                            minGeometricScore, minAppearanceCosine, maxMahalanobisDistance,
                            out slotDetections[s], out slotValid[s]);
                        return localEngine;
                    },
                    localEngine => localEngine.Dispose());
            }
        }

        List<RawPartDetection> results = new(pairCount);
        for (int i = 0; i < pairCount; i++)
        {
            if (slotValid[i])
            {
                results.Add(slotDetections[i]);
            }
        }

        return results;
    }

    private static unsafe void EvaluateSinglePair(
        nint pixelsAddr,
        int bufferLen,
        int imgW,
        int imgH,
        int imgStride,
        IReadOnlyList<PartPoolEntry> entries,
        ProposalCandidateIndex pair,
        PatMaxEngine engine,
        float minGeometricScore,
        float minAppearanceCosine,
        float maxMahalanobisDistance,
        out RawPartDetection detectionOut,
        out bool isValidOut)
    {
        detectionOut = default;
        isValidOut = false;

        ReadOnlySpan<byte> fullPixels = new((byte*)pixelsAddr, bufferLen);
        ReadOnlyImageView2D<byte> image = new(fullPixels, imgW, imgH, imgStride);
        PartPoolEntry entry = entries[pair.EntryIndex];

        int desiredW = Math.Clamp((int)MathF.Ceiling(entry.ReferenceWidth) + LocalWindowPaddingPixels, 64, MaxLocalWindowSize);
        int desiredH = Math.Clamp((int)MathF.Ceiling(entry.ReferenceHeight) + LocalWindowPaddingPixels, 64, MaxLocalWindowSize);
        int winW = Math.Min(desiredW, imgW);
        int winH = Math.Min(desiredH, imgH);

        int winLeft = Math.Clamp((int)MathF.Round(pair.RefinedCenterX) - (winW / 2), 0, imgW - winW);
        int winTop = Math.Clamp((int)MathF.Round(pair.RefinedCenterY) - (winH / 2), 0, imgH - winH);

        ReadOnlySpan<byte> winSlice = fullPixels.Slice((winTop * imgStride) + winLeft);
        float minSep = MathF.Min(entry.ReferenceWidth, entry.ReferenceHeight) * 0.65f;

        PatMaxSearchParams searchParams = PatMaxSearchParams.Create(
            acceptThreshold: minGeometricScore,
            angleStartRadians: -MathF.PI,
            angleExtentRadians: 2.0f * MathF.PI,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 14.0f,
            ignorePolarity: false,
            maxCandidates: MaxCandidatesPerWindow,
            maxResults: 1,
            minSeparationPixels: minSep);

        Span<PatMaxResult> windowResults = stackalloc PatMaxResult[2];
        int found = engine.Find(
            entry.GeometricModel,
            winSlice,
            winW,
            winH,
            imgStride,
            in searchParams,
            windowResults);

        if (found <= 0)
        {
            return;
        }

        PatMaxResult r = windowResults[0];
        if (!r.IsValid || r.Score < minGeometricScore)
        {
            return;
        }

        FixturePose2D initialWorldPose = new(winLeft + r.X, winTop + r.Y, r.AngleRadians, r.Scale);
        FixturePose2D candPose = entry.RefinePoseIcgn(image, initialWorldPose);

        RotationInvariantSignature candSig = RotationInvariantSignature.Extract(
            image,
            in candPose,
            entry.ReferenceWidth,
            entry.ReferenceHeight);

        float sigCos = candSig.CosineSimilarity(entry.Signature);
        if (sigCos < DefaultMinSignatureCosine)
        {
            return;
        }

        Span<float> candAppearance = stackalloc float[PartPoolEntry.AppearanceDimension];
        Span<float> whitened = stackalloc float[PartPoolEntry.AppearanceDimension];

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
        }

        if (appCos < minAppearanceCosine)
        {
            return;
        }

        float mahaDist = entry.MahalanobisModel.ComputeDistance(candAppearance, proto, whitened);
        if (mahaDist > maxMahalanobisDistance)
        {
            return;
        }

        float combinedRank = (0.45f * r.Score) + (0.35f * appCos) + (0.20f * sigCos);
        detectionOut = new RawPartDetection(entry, candPose, r.Score, appCos, mahaDist, combinedRank);
        isValidOut = true;
    }

    private static List<(float CenterX, float CenterY)> ExtractCoarseEdgeProposals(
        ReadOnlyImageView2D<byte> image,
        float minModelSize,
        VisionWorkspace workspace)
    {
        int gw = image.Width / CoarseGridStride;
        int gh = image.Height / CoarseGridStride;
        if (gw < 8 || gh < 8)
        {
            return [(image.Width * 0.5f, image.Height * 0.5f)];
        }

        Span<byte> coarseEdgeMask = workspace.GetByteBufferB(checked(gw * gh), clear: true);

        for (int gy = 1; gy < gh - 1; gy++)
        {
            int y = gy * CoarseGridStride;
            ReadOnlySpan<byte> rowM = image.RowSpan(y - 1);
            ReadOnlySpan<byte> rowC = image.RowSpan(y);
            ReadOnlySpan<byte> rowP = image.RowSpan(y + 1);
            int maskRowOff = gy * gw;

            for (int gx = 1; gx < gw - 1; gx++)
            {
                int x = gx * CoarseGridStride;
                int dx = Math.Abs(rowC[x + 1] - rowC[x - 1]);
                int dy = Math.Abs(rowP[x] - rowM[x]);
                if (dx + dy >= CoarseEdgeGradientThreshold)
                {
                    coarseEdgeMask[maskRowOff + gx] = byte.MaxValue;
                }
            }
        }

        RleRegion edgeRegion = RleRegion.Threshold(coarseEdgeMask, gw, gh, gw, minIntensity: 128);
        if (edgeRegion.Runs.Count == 0)
        {
            return [];
        }

        RleRegion closed = RleMorphology.ClosingBox(edgeRegion, CoarseClosingRadius, CoarseClosingRadius);
        List<BlobComponentFeature> blobs = RleBlobAnalyzer.AnalyzeComponents(closed, minimumAreaPixels: 16, workspace);

        float minAllowedSpan = minModelSize * 0.48f;
        float maxAllowedSpan = minModelSize * 1.65f;
        List<(float CenterX, float CenterY)> proposals = new(blobs.Count);

        for (int i = 0; i < blobs.Count; i++)
        {
            BlobComponentFeature b = blobs[i];
            float bw = b.BoundingWidth * CoarseGridStride;
            float bh = b.BoundingHeight * CoarseGridStride;

            // Reject small foreign distractors or tiny speckle blobs before signature/PatMax evaluation
            if (bw < minAllowedSpan || bh < minAllowedSpan || bw > maxAllowedSpan || bh > maxAllowedSpan)
            {
                continue;
            }

            float cx = b.Centroid.X * CoarseGridStride;
            float cy = b.Centroid.Y * CoarseGridStride;
            proposals.Add((cx, cy));
        }

        return proposals;
    }

    private static int PreScreenProposalWithSignatures(
        ReadOnlyImageView2D<byte> image,
        float proposalX,
        float proposalY,
        IReadOnlyList<PartPoolEntry> entries,
        Span<ProposalCandidateIndex> candidatesOut)
    {
        int entryCount = Math.Min(entries.Count, candidatesOut.Length);
        Span<float> bestSimPerEntry = stackalloc float[entryCount];
        Span<float> bestXPerEntry = stackalloc float[entryCount];
        Span<float> bestYPerEntry = stackalloc float[entryCount];
        bestSimPerEntry.Fill(-1.0f);

        // Probe a 3x3 coarse grid (-9..+9 px) around the blob centroid so asymmetric parts (e.g. L-bracket) hit their true ring center
        ReadOnlySpan<float> gridOffsets = [-9f, 0f, 9f];
        float referenceW = entries[0].ReferenceWidth;
        float referenceH = entries[0].ReferenceHeight;

        for (int oy = 0; oy < gridOffsets.Length; oy++)
        {
            float py = Math.Clamp(proposalY + gridOffsets[oy], 16f, image.Height - 16f);
            for (int ox = 0; ox < gridOffsets.Length; ox++)
            {
                float px = Math.Clamp(proposalX + gridOffsets[ox], 16f, image.Width - 16f);
                FixturePose2D probePose = new(px, py, 0f);
                RotationInvariantSignature probeSig = RotationInvariantSignature.Extract(image, in probePose, referenceW, referenceH);

                for (int e = 0; e < entryCount; e++)
                {
                    float sim = probeSig.CosineSimilarity(entries[e].Signature);
                    if (sim > bestSimPerEntry[e])
                    {
                        bestSimPerEntry[e] = sim;
                        bestXPerEntry[e] = px;
                        bestYPerEntry[e] = py;
                    }
                }
            }
        }

        float overallBestSim = -1.0f;
        for (int e = 0; e < entryCount; e++)
        {
            if (bestSimPerEntry[e] > overallBestSim)
            {
                overallBestSim = bestSimPerEntry[e];
            }
        }

        if (overallBestSim < ProposalPreScreenSignatureFloor)
        {
            return 0;
        }

        int outCount = 0;
        float cutoff = MathF.Max(ProposalPreScreenSignatureFloor, overallBestSim - ProposalModelRunnerUpMargin);
        for (int e = 0; e < entryCount; e++)
        {
            if (bestSimPerEntry[e] >= cutoff)
            {
                candidatesOut[outCount++] = new ProposalCandidateIndex(
                    e,
                    bestXPerEntry[e],
                    bestYPerEntry[e],
                    bestSimPerEntry[e]);
            }
        }

        return outCount;
    }
}
