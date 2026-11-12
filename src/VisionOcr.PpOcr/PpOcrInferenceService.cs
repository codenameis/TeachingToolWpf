using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionGeometry;

namespace VisionOcr.PpOcr;

/// <summary>
/// Production PP-OCRv4 ONNX Runtime (1.24.4) inference coordinator with deterministic CPU default,
/// optional CUDA execution provider with logged CPU fallback, exact-width-bucketed multi-ROI / multi-quad batching,
/// official DBNet <c>MinAreaRect</c> perspective rectification, and official <c>BaseRecLabelDecode</c> CTC decoding.
/// </summary>
public sealed class PpOcrInferenceService : IPpOcrInferenceService
{
    private const int ChannelCount = 3;
    private const int LastQuadCornerIndex = 3;
    private const int MinRoiDimensionPx = 4;
    private const int MinWarpedCropDimensionPx = 2;
    private const float RecOnlyDetScore = 0.0f;

    private readonly object _inferenceGate = new();
    private readonly PpOcrSessionHost _host;

    public PpOcrModelStatus Status => _host.Status;

    public string StatusMessage => _host.StatusMessage;

    public bool IsModelInstalled => _host.Status == PpOcrModelStatus.Ready;

    public bool HasKoreanModel => _host.HasKoreanModel;

    public PpOcrExecutionDevice RequestedDevice => _host.RequestedDevice;

    public PpOcrExecutionDevice ActiveDevice => _host.ActiveDevice;

    public string? CudaFallbackReason => _host.CudaFallbackReason;

    public int SessionCreationCount => _host.SessionCreationCount;

    public PpOcrInferenceService(PpOcrModelOptions options, Action<string>? logger = null)
    {
        _host = new PpOcrSessionHost(options, logger);
    }

    public PpOcrRecognitionResult RecognizeRoi(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_host.IsDisposed, this);
        PpOcrRunOptions runOpts = options ?? PpOcrRunOptions.Default;

        lock (_inferenceGate)
        {
            ObjectDisposedException.ThrowIf(_host.IsDisposed, this);

            if (_host.Status != PpOcrModelStatus.Ready || _host.DetSession is null || _host.EnglishSlot is null)
            {
                return CreateNotReadyResult();
            }

            if (!roiView.IsValid || roiView.Width < MinRoiDimensionPx || roiView.Height < MinRoiDimensionPx)
            {
                return CreateTooSmallResult();
            }

            return RecognizeRoiUnlocked(roiView, runOpts);
        }
    }

    public IReadOnlyList<PpOcrRecognitionResult> RecognizeBatch(
        IReadOnlyList<PpOcrRoiPatch> patches,
        PpOcrRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        ObjectDisposedException.ThrowIf(_host.IsDisposed, this);

        if (patches.Count == 0)
        {
            return [];
        }

        PpOcrRunOptions runOpts = options ?? PpOcrRunOptions.Default;

        lock (_inferenceGate)
        {
            ObjectDisposedException.ThrowIf(_host.IsDisposed, this);

            var results = new PpOcrRecognitionResult[patches.Count];
            if (_host.Status != PpOcrModelStatus.Ready || _host.DetSession is null || _host.EnglishSlot is null)
            {
                PpOcrRecognitionResult notReady = CreateNotReadyResult();
                Array.Fill(results, notReady);
                return results;
            }

            if (patches.Count == 1)
            {
                PpOcrRoiPatch single = patches[0];
                results[0] = single.IsValid && single.Width >= MinRoiDimensionPx && single.Height >= MinRoiDimensionPx
                    ? RecognizeRoiUnlocked(single.AsView(), runOpts)
                    : CreateTooSmallResult();
                return results;
            }

            ExecuteMultiRoiBatchUnlocked(patches, runOpts, results);
            return results;
        }
    }

    public void Dispose()
    {
        lock (_inferenceGate)
        {
            _host.Dispose();
        }
    }

    private PpOcrRecognitionResult RecognizeRoiUnlocked(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions runOpts)
    {
        bool darkBgPrior = PpOcrPolarityRouter.EstimateIsLightOnDark(roiView);
        ReadOnlySpan<bool> polarityOrder = PpOcrPolarityRouter.ResolvePolarityOrder(runOpts.PolarityMode, darkBgPrior, runOpts.FastInteractivePreview);

        return runOpts.PipelineMode switch
        {
            PpOcrPipelineMode.RecOnly => ExecuteRecOnlyWithPolarity(roiView, runOpts, polarityOrder),
            PpOcrPipelineMode.DetAndRec => ExecuteDetAndRecWithPolarity(roiView, runOpts, polarityOrder, allowRecOnlyFallback: true),
            _ => ExecuteAutoPipeline(roiView, runOpts, polarityOrder),
        };
    }

    private void ExecuteMultiRoiBatchUnlocked(
        IReadOnlyList<PpOcrRoiPatch> patches,
        PpOcrRunOptions options,
        PpOcrRecognitionResult[] results)
    {
        int count = patches.Count;
        var singleLineCandidateIndices = new List<int>(count);
        var primaryInverts = new bool[count];
        var hasSecondaryPolarity = new bool[count];

        for (int i = 0; i < count; i++)
        {
            PpOcrRoiPatch patch = patches[i];
            if (!patch.IsValid || patch.Width < MinRoiDimensionPx || patch.Height < MinRoiDimensionPx)
            {
                results[i] = CreateTooSmallResult();
                continue;
            }

            ReadOnlyImageView2D<byte> roiView = patch.AsView();
            bool darkBgPrior = PpOcrPolarityRouter.EstimateIsLightOnDark(roiView);
            ReadOnlySpan<bool> polarityOrder = PpOcrPolarityRouter.ResolvePolarityOrder(options.PolarityMode, darkBgPrior, options.FastInteractivePreview);
            primaryInverts[i] = polarityOrder[0];
            hasSecondaryPolarity[i] = polarityOrder.Length > 1;

            if (options.PipelineMode == PpOcrPipelineMode.DetAndRec)
            {
                results[i] = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder, allowRecOnlyFallback: true);
            }
            else if (options.PipelineMode == PpOcrPipelineMode.RecOnly || !PpOcrPolarityRouter.IsLikelyMultiLineOrVerticalLayout(roiView))
            {
                singleLineCandidateIndices.Add(i);
            }
            else
            {
                results[i] = ExecuteAutoPipeline(roiView, options, polarityOrder);
            }
        }

        if (singleLineCandidateIndices.Count == 0)
        {
            return;
        }

        var primaryPatches = new PpOcrRoiPatch[singleLineCandidateIndices.Count];
        var primaryFlags = new bool[singleLineCandidateIndices.Count];
        for (int k = 0; k < singleLineCandidateIndices.Count; k++)
        {
            int roiIdx = singleLineCandidateIndices[k];
            primaryPatches[k] = patches[roiIdx];
            primaryFlags[k] = primaryInverts[roiIdx];
        }

        PpOcrCtcDecoder.DecodedCropLine[] primaryDecoded = RecognizeCropsWithOrientationBatch(primaryPatches, primaryFlags, options);

        var secondaryBatchMap = new List<int>();
        var secondaryPatches = new List<PpOcrRoiPatch>();
        var secondaryFlags = new List<bool>();

        for (int k = 0; k < singleLineCandidateIndices.Count; k++)
        {
            int roiIdx = singleLineCandidateIndices[k];
            PpOcrRecognitionResult primRes = BuildSingleCropResult(patches[roiIdx], primaryDecoded[k], primaryInverts[roiIdx], options);
            results[roiIdx] = primRes;

            bool satisfied = primRes.Region.Characters.Count > 0 && primRes.Region.MeanConfidence >= PpOcrPolarityRouter.HighConfidenceShortCircuit;
            if (!satisfied && hasSecondaryPolarity[roiIdx])
            {
                secondaryBatchMap.Add(roiIdx);
                secondaryPatches.Add(patches[roiIdx]);
                secondaryFlags.Add(!primaryInverts[roiIdx]);
            }
        }

        if (secondaryBatchMap.Count > 0)
        {
            PpOcrCtcDecoder.DecodedCropLine[] secondaryDecoded = RecognizeCropsWithOrientationBatch(secondaryPatches, secondaryFlags, options);
            for (int s = 0; s < secondaryBatchMap.Count; s++)
            {
                int roiIdx = secondaryBatchMap[s];
                PpOcrRecognitionResult secRes = BuildSingleCropResult(patches[roiIdx], secondaryDecoded[s], secondaryFlags[s], options);
                results[roiIdx] = PpOcrPolarityRouter.SelectHigherConfidenceResult(results[roiIdx], secRes);
            }
        }

        if (options.PipelineMode == PpOcrPipelineMode.Auto && !options.FastInteractivePreview)
        {
            for (int k = 0; k < singleLineCandidateIndices.Count; k++)
            {
                int roiIdx = singleLineCandidateIndices[k];
                PpOcrRecognitionResult recOnlyCandidate = results[roiIdx];
                if (recOnlyCandidate.Region.Characters.Count > 0 &&
                    recOnlyCandidate.Region.MeanConfidence >= PpOcrPolarityRouter.SingleLineAcceptShortCircuit)
                {
                    continue;
                }

                ReadOnlyImageView2D<byte> roiView = patches[roiIdx].AsView();
                bool darkBgPrior = PpOcrPolarityRouter.EstimateIsLightOnDark(roiView);
                ReadOnlySpan<bool> polarityOrder = PpOcrPolarityRouter.ResolvePolarityOrder(options.PolarityMode, darkBgPrior, options.FastInteractivePreview);
                // recOnlyCandidate was already computed above; disable redundant RecOnly fallback inside Det+Rec!
                PpOcrRecognitionResult detAndRecCandidate = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder, allowRecOnlyFallback: false);
                results[roiIdx] = PpOcrPolarityRouter.SelectHigherConfidenceResult(detAndRecCandidate, recOnlyCandidate);
            }
        }
    }

    private PpOcrRecognitionResult ExecuteAutoPipeline(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions options,
        ReadOnlySpan<bool> polarityOrder)
    {
        bool likelyComplexLayout = PpOcrPolarityRouter.IsLikelyMultiLineOrVerticalLayout(roiView);
        if (options.FastInteractivePreview)
        {
            if (!likelyComplexLayout)
            {
                return ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            }

            PpOcrRecognitionResult fastDetAndRec = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder, allowRecOnlyFallback: false);
            if (fastDetAndRec.Region.Characters.Count > 0 &&
                fastDetAndRec.Region.MeanConfidence >= PpOcrPolarityRouter.SingleLineAcceptShortCircuit)
            {
                return fastDetAndRec;
            }

            PpOcrRecognitionResult fastRecOnly = ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            return PpOcrPolarityRouter.SelectHigherConfidenceResult(fastDetAndRec, fastRecOnly);
        }

        if (!likelyComplexLayout)
        {
            PpOcrRecognitionResult recOnlyCandidate = ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            if (recOnlyCandidate.Region.Characters.Count > 0 &&
                recOnlyCandidate.Region.MeanConfidence >= PpOcrPolarityRouter.SingleLineAcceptShortCircuit)
            {
                return recOnlyCandidate;
            }

            PpOcrRecognitionResult detOnlyCandidate = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder, allowRecOnlyFallback: false);
            return PpOcrPolarityRouter.SelectHigherConfidenceResult(detOnlyCandidate, recOnlyCandidate);
        }

        // Complex layout: run Det+Rec with single-pass RecOnly fallback if DBNet finds 0 boxes (never run RecOnly twice).
        return ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder, allowRecOnlyFallback: true);
    }

    private PpOcrRecognitionResult ExecuteRecOnlyWithPolarity(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions options,
        ReadOnlySpan<bool> polarityOrder)
    {
        PpOcrRecognitionResult? best = null;
        for (int i = 0; i < polarityOrder.Length; i++)
        {
            bool invert = polarityOrder[i];
            PpOcrCtcDecoder.DecodedCropLine bestCrop = RecognizeCropWithOrientationCheck(roiView, invert, options);
            PpOcrRecognitionResult current = BuildSingleCropResultFromDimensions(roiView.Width, roiView.Height, bestCrop, invert, options);
            best = best is null ? current : PpOcrPolarityRouter.SelectHigherConfidenceResult(best, current);
            if (best.Region.Characters.Count > 0 && best.Region.MeanConfidence >= PpOcrPolarityRouter.HighConfidenceShortCircuit)
            {
                break;
            }
        }

        return best!;
    }

    private PpOcrRecognitionResult ExecuteDetAndRecWithPolarity(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions options,
        ReadOnlySpan<bool> polarityOrder,
        bool allowRecOnlyFallback)
    {
        DbDetOptions detOpts = options.DetOptions ?? DbDetOptions.Default;
        PpOcrRecognitionResult? best = null;

        for (int i = 0; i < polarityOrder.Length; i++)
        {
            bool invert = polarityOrder[i];
            PpOcrRecognitionResult candidate = RunDetThenRec(roiView, invert, options, detOpts);
            best = best is null ? candidate : PpOcrPolarityRouter.SelectHigherConfidenceResult(best, candidate);
            if (best.Region.Characters.Count > 0 && best.Region.MeanConfidence >= PpOcrPolarityRouter.HighConfidenceShortCircuit)
            {
                break;
            }
        }

        if (allowRecOnlyFallback && (best is null || best.Region.Characters.Count == 0))
        {
            PpOcrRecognitionResult fallbackRec = ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            if (fallbackRec.Region.Characters.Count > 0)
            {
                return fallbackRec;
            }
        }

        return best!;
    }

    private PpOcrRecognitionResult BuildSingleCropResult(
        PpOcrRoiPatch patch,
        PpOcrCtcDecoder.DecodedCropLine bestCrop,
        bool invertPolarity,
        PpOcrRunOptions options)
        => BuildSingleCropResultFromDimensions(patch.Width, patch.Height, bestCrop, invertPolarity, options);

    private PpOcrRecognitionResult BuildSingleCropResultFromDimensions(
        int roiWidth,
        int roiHeight,
        PpOcrCtcDecoder.DecodedCropLine bestCrop,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        if (bestCrop.Characters.Count == 0 || string.IsNullOrWhiteSpace(bestCrop.Text))
        {
            return new PpOcrRecognitionResult(
                PpOcrModelStatus.Ready,
                StatusMessage,
                PpOcrRegionResult.CreateEmpty(darkOnLight: !invertPolarity),
                [],
                ActiveDevice,
                UsedDetectionStage: false,
                UsedInvertedPolarity: invertPolarity);
        }

        bool isPass = bestCrop.MeanConfidence >= options.MinAcceptConfidence
            && bestCrop.MinConfidence >= options.MinCharacterConfidence;

        var fullPoly = ImmutableArray.Create(
            new PointD(0, 0),
            new PointD(roiWidth - 1, 0),
            new PointD(roiWidth - 1, roiHeight - 1),
            new PointD(0, roiHeight - 1));

        // RecOnly did not run DBNet detection: report honest DetScore = 0f and IsDetectedQuad = false.
        var box = new PpOcrBoxResult(
            fullPoly,
            bestCrop.Text,
            bestCrop.MeanConfidence,
            bestCrop.MinConfidence,
            DetScore: RecOnlyDetScore,
            UsedInvertedPolarity: invertPolarity,
            IsDetectedQuad: false);

        var region = new PpOcrRegionResult(
            bestCrop.Text,
            [bestCrop.Text],
            bestCrop.Characters,
            bestCrop.MeanConfidence,
            bestCrop.MinConfidence,
            DarkOnLight: !invertPolarity,
            IsPass: isPass);

        return new PpOcrRecognitionResult(
            PpOcrModelStatus.Ready,
            StatusMessage,
            region,
            [box],
            ActiveDevice,
            UsedDetectionStage: false,
            UsedInvertedPolarity: invertPolarity);
    }

    private PpOcrRecognitionResult RunDetThenRec(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        PpOcrRunOptions options,
        DbDetOptions detOpts)
    {
        List<PpOcrDbPostProcessor.DetectedQuad> quads = PpOcrDbPostProcessor.DetectTextQuads(
            _host.DetSession!,
            _host.DetInputName,
            roiView,
            invertPolarity,
            detOpts);

        if (quads.Count == 0)
        {
            return new PpOcrRecognitionResult(
                PpOcrModelStatus.Ready,
                StatusMessage,
                PpOcrRegionResult.CreateEmpty(darkOnLight: !invertPolarity),
                [],
                ActiveDevice,
                UsedDetectionStage: true,
                UsedInvertedPolarity: invertPolarity);
        }

        List<List<PpOcrDbPostProcessor.DetectedQuad>> lineBuckets = PpOcrDbPostProcessor.GroupQuadsIntoReadingLines(quads);
        var warpedEntries = new List<WarpedQuadEntry>(quads.Count);

        try
        {
            using Mat sourceMat = PpOcrPreprocessor.CreateContiguousGrayMat(roiView, invertPolarity: false);

            for (int lineIdx = 0; lineIdx < lineBuckets.Count; lineIdx++)
            {
                List<PpOcrDbPostProcessor.DetectedQuad> rowQuads = lineBuckets[lineIdx];
                for (int qIdx = 0; qIdx < rowQuads.Count; qIdx++)
                {
                    PpOcrDbPostProcessor.DetectedQuad quad = rowQuads[qIdx];
                    using Mat warpedCrop = PpOcrDbPostProcessor.WarpPerspectiveQuadCrop(sourceMat, quad.Points);
                    if (warpedCrop.Empty() || warpedCrop.Width < MinWarpedCropDimensionPx || warpedCrop.Height < MinWarpedCropDimensionPx)
                    {
                        continue;
                    }

                    RentedCropBuffer rentedCrop = RentCropFromMat(warpedCrop);
                    warpedEntries.Add(new WarpedQuadEntry(lineIdx, quad, rentedCrop));
                }
            }

            if (warpedEntries.Count == 0)
            {
                return new PpOcrRecognitionResult(
                    PpOcrModelStatus.Ready,
                    StatusMessage,
                    PpOcrRegionResult.CreateEmpty(darkOnLight: !invertPolarity),
                    [],
                    ActiveDevice,
                    UsedDetectionStage: true,
                    UsedInvertedPolarity: invertPolarity);
            }

            var cropPatches = new PpOcrRoiPatch[warpedEntries.Count];
            var cropInverts = new bool[warpedEntries.Count];
            for (int i = 0; i < warpedEntries.Count; i++)
            {
                cropPatches[i] = warpedEntries[i].Crop.AsPatch();
                cropInverts[i] = invertPolarity;
            }

            PpOcrCtcDecoder.DecodedCropLine[] decodedQuads = RecognizeCropsWithOrientationBatch(cropPatches, cropInverts, options);

            var boxes = ImmutableArray.CreateBuilder<PpOcrBoxResult>(warpedEntries.Count);
            var allChars = new List<PpOcrCharacterBox>();
            var lineTexts = new List<string>(lineBuckets.Count);

            float weightedConfSum = 0f;
            int totalTokenCount = 0;
            float overallMinConf = 1.0f;

            int entryCursor = 0;
            for (int lineIdx = 0; lineIdx < lineBuckets.Count; lineIdx++)
            {
                var rowSb = new StringBuilder();
                while (entryCursor < warpedEntries.Count && warpedEntries[entryCursor].LineIndex == lineIdx)
                {
                    WarpedQuadEntry entry = warpedEntries[entryCursor];
                    PpOcrCtcDecoder.DecodedCropLine decoded = decodedQuads[entryCursor];
                    entryCursor++;

                    if (string.IsNullOrWhiteSpace(decoded.Text) || decoded.Characters.Count == 0)
                    {
                        continue;
                    }

                    bool precededByWordGap = rowSb.Length > 0;
                    if (precededByWordGap)
                    {
                        rowSb.Append(' ');
                    }

                    rowSb.Append(decoded.Text);

                    int cropTokenCount = Math.Max(1, decoded.Text.Length);
                    weightedConfSum += decoded.MeanConfidence * cropTokenCount;
                    totalTokenCount += cropTokenCount;
                    if (decoded.MinConfidence < overallMinConf)
                    {
                        overallMinConf = decoded.MinConfidence;
                    }

                    PpOcrDbPostProcessor.DetectedQuad quad = entry.Quad;
                    int minX = Math.Clamp((int)MathF.Floor(quad.MinX), 0, roiView.Width - 1);
                    int maxX = Math.Clamp((int)MathF.Ceiling(quad.MaxX), minX, roiView.Width - 1);
                    int minY = Math.Clamp((int)MathF.Floor(quad.MinY), 0, roiView.Height - 1);
                    int maxY = Math.Clamp((int)MathF.Ceiling(quad.MaxY), minY, roiView.Height - 1);
                    float boxSpanW = Math.Max(1, maxX - minX);
                    float boxSpanH = Math.Max(1, maxY - minY);
                    float cropSpanW = Math.Max(1, entry.Crop.Width);
                    float cropSpanH = Math.Max(1, entry.Crop.Height);

                    for (int cIdx = 0; cIdx < decoded.Characters.Count; cIdx++)
                    {
                        PpOcrCharacterBox ch = decoded.Characters[cIdx];
                        int mappedStartX = Math.Clamp(minX + (int)MathF.Round((ch.StartCol / cropSpanW) * boxSpanW), 0, roiView.Width - 1);
                        int mappedEndX = Math.Clamp(minX + (int)MathF.Round((ch.EndCol / cropSpanW) * boxSpanW), mappedStartX, roiView.Width - 1);
                        int mappedTopY = Math.Clamp(minY + (int)MathF.Round((ch.TopRow / cropSpanH) * boxSpanH), 0, roiView.Height - 1);
                        int mappedBotY = Math.Clamp(minY + (int)MathF.Round((ch.BottomRow / cropSpanH) * boxSpanH), mappedTopY, roiView.Height - 1);
                        bool spaceFlag = cIdx == 0 ? (precededByWordGap || ch.PrecededBySpace) : ch.PrecededBySpace;

                        allChars.Add(new PpOcrCharacterBox(
                            ch.Character,
                            ch.Confidence,
                            lineIdx,
                            mappedStartX,
                            mappedEndX,
                            mappedTopY,
                            mappedBotY,
                            spaceFlag));
                    }

                    var poly = ImmutableArray.Create(
                        new PointD(quad.Points[0].X, quad.Points[0].Y),
                        new PointD(quad.Points[1].X, quad.Points[1].Y),
                        new PointD(quad.Points[2].X, quad.Points[2].Y),
                        new PointD(quad.Points[LastQuadCornerIndex].X, quad.Points[LastQuadCornerIndex].Y));

                    boxes.Add(new PpOcrBoxResult(
                        poly,
                        decoded.Text,
                        decoded.MeanConfidence,
                        decoded.MinConfidence,
                        quad.Score,
                        invertPolarity,
                        IsDetectedQuad: true));
                }

                if (rowSb.Length > 0)
                {
                    lineTexts.Add(rowSb.ToString());
                }
            }

            if (totalTokenCount == 0 || lineTexts.Count == 0)
            {
                return new PpOcrRecognitionResult(
                    PpOcrModelStatus.Ready,
                    StatusMessage,
                    PpOcrRegionResult.CreateEmpty(darkOnLight: !invertPolarity),
                    [],
                    ActiveDevice,
                    UsedDetectionStage: true,
                    UsedInvertedPolarity: invertPolarity);
            }

            float meanConf = weightedConfSum / totalTokenCount;
            float minConf = overallMinConf <= 1.0f ? overallMinConf : 0f;
            string fullText = string.Join("\n", lineTexts);
            bool isPass = meanConf >= options.MinAcceptConfidence && minConf >= options.MinCharacterConfidence;

            var region = new PpOcrRegionResult(
                fullText,
                lineTexts,
                allChars,
                meanConf,
                minConf,
                DarkOnLight: !invertPolarity,
                IsPass: isPass);

            return new PpOcrRecognitionResult(
                PpOcrModelStatus.Ready,
                StatusMessage,
                region,
                boxes.ToImmutable(),
                ActiveDevice,
                UsedDetectionStage: true,
                UsedInvertedPolarity: invertPolarity);
        }
        finally
        {
            for (int i = 0; i < warpedEntries.Count; i++)
            {
                warpedEntries[i].Crop.Dispose();
            }
        }
    }

    private PpOcrCtcDecoder.DecodedCropLine[] RecognizeCropsWithOrientationBatch(
        IReadOnlyList<PpOcrRoiPatch> crops,
        IReadOnlyList<bool> invertFlags,
        PpOcrRunOptions options)
    {
        int count = crops.Count;
        var outputs = new PpOcrCtcDecoder.DecodedCropLine[count];
        var horizontalIndices = new List<int>(count);
        var horizontalPatches = new List<PpOcrRoiPatch>(count);
        var horizontalInverts = new List<bool>(count);

        for (int i = 0; i < count; i++)
        {
            PpOcrRoiPatch patch = crops[i];
            if (!patch.IsValid)
            {
                outputs[i] = PpOcrCtcDecoder.DecodedCropLine.Empty;
                continue;
            }

            if (patch.Height >= patch.Width * PpOcrPolarityRouter.VerticalAspectTransposeRatio)
            {
                outputs[i] = RecognizeVerticalCrop(patch.AsView(), invertFlags[i], options);
            }
            else
            {
                horizontalIndices.Add(i);
                horizontalPatches.Add(patch);
                horizontalInverts.Add(invertFlags[i]);
            }
        }

        if (horizontalIndices.Count == 0)
        {
            return outputs;
        }

        PpOcrCtcDecoder.DecodedCropLine[] uprightLines = RecognizeHorizontalCropsBatch(horizontalPatches, horizontalInverts, options);
        for (int k = 0; k < horizontalIndices.Count; k++)
        {
            outputs[horizontalIndices[k]] = uprightLines[k];
        }

        if (options.FastInteractivePreview || options.SkipOrientationFallback)
        {
            return outputs;
        }

        var flippedMap = new List<int>();
        var flippedBuffers = new List<RentedCropBuffer>();
        var flippedPatches = new List<PpOcrRoiPatch>();
        var flippedInverts = new List<bool>();

        try
        {
            for (int k = 0; k < horizontalIndices.Count; k++)
            {
                PpOcrCtcDecoder.DecodedCropLine upright = uprightLines[k];
                if (upright.MeanConfidence >= PpOcrPolarityRouter.UpsideDownCheckThreshold && upright.Characters.Count > 0)
                {
                    continue;
                }

                int origIdx = horizontalIndices[k];
                using Mat baseMat = PpOcrPreprocessor.CreateContiguousGrayMat(crops[origIdx].AsView(), invertPolarity: false);
                using var rot180Mat = new Mat();
                Cv2.Rotate(baseMat, rot180Mat, RotateFlags.Rotate180);
                RentedCropBuffer rentedFlip = RentCropFromMat(rot180Mat);

                flippedMap.Add(origIdx);
                flippedBuffers.Add(rentedFlip);
                flippedPatches.Add(rentedFlip.AsPatch());
                flippedInverts.Add(invertFlags[origIdx]);
            }

            if (flippedPatches.Count > 0)
            {
                PpOcrCtcDecoder.DecodedCropLine[] flippedLines = RecognizeHorizontalCropsBatch(flippedPatches, flippedInverts, options);
                for (int f = 0; f < flippedMap.Count; f++)
                {
                    int origIdx = flippedMap[f];
                    PpOcrRoiPatch origPatch = crops[origIdx];
                    PpOcrCtcDecoder.DecodedCropLine remappedFlip = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
                        flippedLines[f],
                        PpOcrCtcDecoder.CropOrientation.Rotate180,
                        origPatch.Width,
                        origPatch.Height);
                    outputs[origIdx] = PpOcrPolarityRouter.SelectBetterCropLine(outputs[origIdx], remappedFlip);
                }
            }
        }
        finally
        {
            for (int i = 0; i < flippedBuffers.Count; i++)
            {
                flippedBuffers[i].Dispose();
            }
        }

        return outputs;
    }

    private PpOcrCtcDecoder.DecodedCropLine RecognizeCropWithOrientationCheck(
        ReadOnlyImageView2D<byte> cropView,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        if (cropView.Height >= cropView.Width * PpOcrPolarityRouter.VerticalAspectTransposeRatio)
        {
            return RecognizeVerticalCrop(cropView, invertPolarity, options);
        }

        PpOcrCtcDecoder.DecodedCropLine upright = RecognizeHorizontalCrop(cropView, invertPolarity, options);
        if (options.FastInteractivePreview ||
            options.SkipOrientationFallback ||
            (upright.MeanConfidence >= PpOcrPolarityRouter.UpsideDownCheckThreshold && upright.Characters.Count > 0))
        {
            return upright;
        }

        using Mat baseMat = PpOcrPreprocessor.CreateContiguousGrayMat(cropView, invertPolarity: false);
        using var rot180Mat = new Mat();
        Cv2.Rotate(baseMat, rot180Mat, RotateFlags.Rotate180);
        using RentedCropBuffer rot180Buf = RentCropFromMat(rot180Mat);
        PpOcrCtcDecoder.DecodedCropLine flipped = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
            RecognizeHorizontalCrop(rot180Buf.AsView(), invertPolarity, options),
            PpOcrCtcDecoder.CropOrientation.Rotate180,
            cropView.Width,
            cropView.Height);
        return PpOcrPolarityRouter.SelectBetterCropLine(upright, flipped);
    }

    private PpOcrCtcDecoder.DecodedCropLine RecognizeVerticalCrop(
        ReadOnlyImageView2D<byte> cropView,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        using Mat srcMat = PpOcrPreprocessor.CreateContiguousGrayMat(cropView, invertPolarity: false);
        using var ccwMat = new Mat();
        using var cwMat = new Mat();
        Cv2.Rotate(srcMat, ccwMat, RotateFlags.Rotate90Counterclockwise);
        Cv2.Rotate(srcMat, cwMat, RotateFlags.Rotate90Clockwise);

        using RentedCropBuffer ccwBuf = RentCropFromMat(ccwMat);
        using RentedCropBuffer cwBuf = RentCropFromMat(cwMat);

        PpOcrCtcDecoder.DecodedCropLine[] pair = RecognizeHorizontalCropsBatch(
            [ccwBuf.AsPatch(), cwBuf.AsPatch()],
            [invertPolarity, invertPolarity],
            options);

        PpOcrCtcDecoder.DecodedCropLine ccwRemapped = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
            pair[0],
            PpOcrCtcDecoder.CropOrientation.Rotate90Counterclockwise,
            cropView.Width,
            cropView.Height);
        PpOcrCtcDecoder.DecodedCropLine cwRemapped = PpOcrCtcDecoder.RemapOrientationToOriginalFrame(
            pair[1],
            PpOcrCtcDecoder.CropOrientation.Rotate90Clockwise,
            cropView.Width,
            cropView.Height);

        return PpOcrPolarityRouter.SelectBetterCropLine(ccwRemapped, cwRemapped);
    }

    private PpOcrCtcDecoder.DecodedCropLine RecognizeHorizontalCrop(
        ReadOnlyImageView2D<byte> cropView,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        PpOcrPreprocessor.ComputeRecWidths(cropView.Width, cropView.Height, out int targetWidth, out int resizedContentWidth);
        int sliceFloats = ChannelCount * PpOcrRunOptions.OfficialRecImageHeight * targetWidth;
        float[] rentedTensor = ArrayPool<float>.Shared.Rent(sliceFloats);
        try
        {
            PpOcrPreprocessor.FillRecBatchSliceInto(
                cropView,
                invertPolarity,
                targetWidth,
                resizedContentWidth,
                rentedTensor.AsSpan(0, sliceFloats));

            Memory<float> tensorMemory = rentedTensor.AsMemory(0, sliceFloats);
            bool asciiRestricted = options.AllowedCharacterSet != OcrAllowedCharacterSet.All;
            if (options.Language == PpOcrLanguage.English || asciiRestricted || _host.KoreanSlot is null)
            {
                return PpOcrRecBatchPlanner.RunSingleCrop(
                    _host.EnglishSlot!,
                    tensorMemory,
                    targetWidth,
                    resizedContentWidth,
                    cropView.Width,
                    cropView.Height,
                    options.AllowedCharacterSet);
            }

            if (options.Language == PpOcrLanguage.Korean)
            {
                return PpOcrRecBatchPlanner.RunSingleCrop(
                    _host.KoreanSlot,
                    tensorMemory,
                    targetWidth,
                    resizedContentWidth,
                    cropView.Width,
                    cropView.Height,
                    options.AllowedCharacterSet);
            }

            PpOcrCtcDecoder.DecodedCropLine enLine = PpOcrRecBatchPlanner.RunSingleCrop(
                _host.EnglishSlot!,
                tensorMemory,
                targetWidth,
                resizedContentWidth,
                cropView.Width,
                cropView.Height,
                options.AllowedCharacterSet);
            if (PpOcrPolarityRouter.ShouldShortCircuitEnglishLine(enLine, options.FastInteractivePreview))
            {
                return enLine;
            }

            PpOcrCtcDecoder.DecodedCropLine koLine = PpOcrRecBatchPlanner.RunSingleCrop(
                _host.KoreanSlot,
                tensorMemory,
                targetWidth,
                resizedContentWidth,
                cropView.Width,
                cropView.Height,
                options.AllowedCharacterSet);
            return PpOcrPolarityRouter.SelectBetterCropLine(enLine, koLine);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedTensor);
        }
    }

    private PpOcrCtcDecoder.DecodedCropLine[] RecognizeHorizontalCropsBatch(
        IReadOnlyList<PpOcrRoiPatch> crops,
        IReadOnlyList<bool> invertFlags,
        PpOcrRunOptions options)
    {
        int count = crops.Count;
        bool asciiRestricted = options.AllowedCharacterSet != OcrAllowedCharacterSet.All;

        if (options.Language == PpOcrLanguage.English || asciiRestricted || _host.KoreanSlot is null)
        {
            return PpOcrRecBatchPlanner.RunSlotAcrossCrops(_host.EnglishSlot!, ActiveDevice, crops, invertFlags, options);
        }

        if (options.Language == PpOcrLanguage.Korean)
        {
            return PpOcrRecBatchPlanner.RunSlotAcrossCrops(_host.KoreanSlot, ActiveDevice, crops, invertFlags, options);
        }

        PpOcrCtcDecoder.DecodedCropLine[] enLines = PpOcrRecBatchPlanner.RunSlotAcrossCrops(_host.EnglishSlot!, ActiveDevice, crops, invertFlags, options);

        var koCandidateMap = new List<int>(count);
        var koCandidatePatches = new List<PpOcrRoiPatch>(count);
        var koCandidateInverts = new List<bool>(count);

        for (int i = 0; i < count; i++)
        {
            if (PpOcrPolarityRouter.ShouldShortCircuitEnglishLine(enLines[i], options.FastInteractivePreview))
            {
                continue;
            }

            koCandidateMap.Add(i);
            koCandidatePatches.Add(crops[i]);
            koCandidateInverts.Add(invertFlags[i]);
        }

        if (koCandidateMap.Count == 0)
        {
            return enLines;
        }

        PpOcrCtcDecoder.DecodedCropLine[] koLines = PpOcrRecBatchPlanner.RunSlotAcrossCrops(_host.KoreanSlot, ActiveDevice, koCandidatePatches, koCandidateInverts, options);
        for (int k = 0; k < koCandidateMap.Count; k++)
        {
            int idx = koCandidateMap[k];
            enLines[idx] = PpOcrPolarityRouter.SelectBetterCropLine(enLines[idx], koLines[k]);
        }

        return enLines;
    }

    private static RentedCropBuffer RentCropFromMat(Mat grayMat)
    {
        int width = grayMat.Width;
        int height = grayMat.Height;
        int totalBytes = checked(width * height);
        byte[] rented = ArrayPool<byte>.Shared.Rent(totalBytes);
        unsafe
        {
            byte* basePtr = grayMat.DataPointer;
            long step = grayMat.Step();
            for (int y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(basePtr + (y * step), width).CopyTo(rented.AsSpan(y * width, width));
            }
        }

        return new RentedCropBuffer(rented, width, height);
    }

    private PpOcrRecognitionResult CreateNotReadyResult()
        => new(
            Status,
            StatusMessage,
            PpOcrRegionResult.CreateEmpty(),
            [],
            ActiveDevice,
            UsedDetectionStage: false,
            UsedInvertedPolarity: false);

    private PpOcrRecognitionResult CreateTooSmallResult()
        => new(
            PpOcrModelStatus.Ready,
            "입력 ROI 크기가 최소 한도(4×4 px) 미만입니다.",
            PpOcrRegionResult.CreateEmpty(),
            [],
            ActiveDevice,
            UsedDetectionStage: false,
            UsedInvertedPolarity: false);

    private readonly struct RentedCropBuffer(byte[] rentedBytes, int width, int height) : IDisposable
    {
        public int Width => width;

        public int Height => height;

        public ReadOnlyImageView2D<byte> AsView()
            => new(rentedBytes.AsSpan(0, width * height), width, height, width);

        public PpOcrRoiPatch AsPatch()
            => new(rentedBytes.AsMemory(0, width * height), width, height, width);

        public void Dispose()
        {
            if (rentedBytes is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBytes);
            }
        }
    }

    private readonly record struct WarpedQuadEntry(
        int LineIndex,
        PpOcrDbPostProcessor.DetectedQuad Quad,
        RentedCropBuffer Crop);
}
