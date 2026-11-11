// File: src/VisionOcr.PpOcr/PpOcrInferenceService.cs
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;
using VisionGeometry;

namespace VisionOcr.PpOcr;

/// <summary>
/// Production PP-OCRv4 ONNX Runtime (1.24.4) inference service with explicit session lifetime,
/// deterministic CPU default, optional CUDA execution provider with logged CPU fallback,
/// dynamic width-bucketed multi-ROI / multi-quad batch recognition, pooled zero-allocation tensor buffers,
/// official DBNet perspective crop rectification, and genuine softmax CTC token-masked decoding.
/// </summary>
public sealed class PpOcrInferenceService : IPpOcrInferenceService
{
    private const int ChannelCount = 3;
    private const int RecTensorRank = 4;
    private const int QuadCornerCount = 4;
    private const int LastQuadCornerIndex = 3;
    private const int CtcBlankIndex = 0;
    private const int MinRoiDimensionPx = 4;
    private const int MinWarpedCropDimensionPx = 2;
    private const int MinIntraOpThreads = 1;
    private const int MaxIntraOpThreads = 16;
    private const int CpuL2CacheOptimalBatchSize = 2;
    private const int MaxAllowedBatchSize = 128;
    private const int MaxBucketWidthSpreadPx = 64;
    private const int MultiLineMinHeightPx = 52;
    private const int ProjectionScanMinHeightPx = 34;
    private const int MinBandGapRows = 3;
    private const int MinTextBandHeightRows = 5;
    private const byte BinaryForegroundMaskValue = byte.MaxValue;

    private const float VerticalAspectTransposeRatio = 1.50f;
    private const float MultiLineTallAspectRatio = 1.25f;
    private const float MultiLineCompactAspectRatio = 3.40f;
    private const float HighConfidenceShortCircuit = 0.910f;
    private const float SingleLineAcceptShortCircuit = 0.850f;
    private const float ProbabilityRowSumTolerance = 0.025f;
    private const float ProbabilityMinFloor = -0.001f;
    private const float ProbabilityMaxCeiling = 1.001f;
    private const float SameLineRowMergeFactor = 0.55f;
    private const float UpsideDownCheckThreshold = 0.720f;
    private const float InteractiveEnglishShortCircuit = 0.880f;
    private const float MeanConfidenceScoreWeight = 0.80f;
    private const float MinConfidenceScoreWeight = 0.20f;
    private const float PolaritySwitchScoreMargin = 0.015f;
    private const float HangulSelectionMargin = 0.010f;
    private const float NonHangulSelectionMargin = 0.055f;
    private const float GuaranteedEnglishWinScore = 1.0f - HangulSelectionMargin; // [Math Why]: koScore <= 1.0f, so koScore > enScore + 0.010f is impossible when enScore >= 0.990f
    private const float MinRowEdgeEnergy = 4.0f;
    private const float BandEnergyRatio = 0.25f;
    private const float MinLineRowHeightPx = 4.0f;
    private const float DarkBackgroundBorderMeanCutoff = 110.0f;
    private const double ApproxPolyPerimeterFraction = 0.02;
    private const double ConvexQuadAreaFillFraction = 0.78;
    private const float DegenerateVectorEpsilon = 1e-3f;

    private readonly Action<string>? _logger;
    private readonly object _inferenceGate = new();
    private SessionOptions? _sessionOptions;
    private InferenceSession? _detSession;
    private RecognizerSlot? _englishSlot;
    private RecognizerSlot? _koreanSlot;
    private string _detInputName = string.Empty;
    private string _detOutputName = string.Empty;
    private int _sessionCreationCount;
    private bool _disposed;

    public PpOcrModelStatus Status { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    public bool IsModelInstalled => Status == PpOcrModelStatus.Ready;

    public bool HasKoreanModel => _koreanSlot is not null;

    public PpOcrExecutionDevice RequestedDevice { get; }

    public PpOcrExecutionDevice ActiveDevice { get; private set; }

    public string? CudaFallbackReason { get; private set; }

    public int SessionCreationCount => _sessionCreationCount;

    public PpOcrInferenceService(PpOcrModelOptions options, Action<string>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        RequestedDevice = options.Device;
        ActiveDevice = PpOcrExecutionDevice.Cpu;

        List<string> missing = CollectMissingRequiredFiles(options);
        if (missing.Count > 0)
        {
            Status = PpOcrModelStatus.MissingModel;
            StatusMessage = $"모델 미설치 ({string.Join(", ", missing)})";
            return;
        }

        bool includeKorean = !string.IsNullOrWhiteSpace(options.KoreanRecModelPath)
            || !string.IsNullOrWhiteSpace(options.KoreanDictionaryPath);

        if (includeKorean &&
            (string.IsNullOrWhiteSpace(options.KoreanRecModelPath) ||
             string.IsNullOrWhiteSpace(options.KoreanDictionaryPath) ||
             !File.Exists(options.KoreanRecModelPath) ||
             !File.Exists(options.KoreanDictionaryPath)))
        {
            Status = PpOcrModelStatus.MissingModel;
            StatusMessage = "모델 미설치 (한국어 PP-OCR 모델 또는 사전 파일 누락)";
            return;
        }

        if (options.Device == PpOcrExecutionDevice.Cuda)
        {
            if (TryInitializeSessions(options, includeKorean, useCuda: true, out Exception? cudaError))
            {
                ActiveDevice = PpOcrExecutionDevice.Cuda;
                Status = PpOcrModelStatus.Ready;
                StatusMessage = $"PP-OCRv4 ONNX 준비 완료 (CUDA:{options.CudaDeviceId})";
                return;
            }

            CudaFallbackReason = cudaError?.Message ?? "알 수 없는 CUDA 초기화 오류";
            _logger?.Invoke($"[PP-OCR CUDA 폴백] CUDA:{options.CudaDeviceId} 초기화 실패로 CPU 세션으로 전환합니다: {CudaFallbackReason}");
            ReleaseSessionsOnly();
        }

        try
        {
            InitializeSessionsOrThrow(options, includeKorean, useCuda: false);
            ActiveDevice = PpOcrExecutionDevice.Cpu;
            Status = PpOcrModelStatus.Ready;
            StatusMessage = CudaFallbackReason is null
                ? "PP-OCRv4 ONNX 준비 완료 (CPU)"
                : $"PP-OCRv4 ONNX 준비 완료 (CPU 폴백: {CudaFallbackReason})";
        }
        catch (Exception ex)
        {
            ReleaseSessionsOnly();
            Status = PpOcrModelStatus.LoadFailed;
            StatusMessage = $"PP-OCRv4 ONNX 모델 로드 실패: {ex.Message}";
            throw new InvalidDataException(StatusMessage, ex);
        }
    }

    public PpOcrRecognitionResult RecognizeRoi(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PpOcrRunOptions runOpts = options ?? PpOcrRunOptions.Default;

        lock (_inferenceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (Status != PpOcrModelStatus.Ready || _detSession is null || _englishSlot is null)
            {
                return CreateNotReadyResult();
            }

            if (!roiView.IsValid || roiView.Width < MinRoiDimensionPx || roiView.Height < MinRoiDimensionPx)
            {
                return CreateTooSmallResult();
            }

            bool darkBgPrior = EstimateIsLightOnDark(roiView);
            ReadOnlySpan<bool> polarityOrder = ResolvePolarityOrder(runOpts.PolarityMode, darkBgPrior, runOpts.FastInteractivePreview);

            return runOpts.PipelineMode switch
            {
                PpOcrPipelineMode.RecOnly => ExecuteRecOnlyWithPolarity(roiView, runOpts, polarityOrder),
                PpOcrPipelineMode.DetAndRec => ExecuteDetAndRecWithPolarity(roiView, runOpts, polarityOrder),
                _ => ExecuteAutoPipeline(roiView, runOpts, polarityOrder),
            };
        }
    }

    public IReadOnlyList<PpOcrRecognitionResult> RecognizeBatch(
        IReadOnlyList<PpOcrRoiPatch> patches,
        PpOcrRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (patches.Count == 0)
        {
            return [];
        }

        PpOcrRunOptions runOpts = options ?? PpOcrRunOptions.Default;

        lock (_inferenceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var results = new PpOcrRecognitionResult[patches.Count];
            if (Status != PpOcrModelStatus.Ready || _detSession is null || _englishSlot is null)
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseSessionsOnly();
        }
    }

    private PpOcrRecognitionResult RecognizeRoiUnlocked(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions runOpts)
    {
        bool darkBgPrior = EstimateIsLightOnDark(roiView);
        ReadOnlySpan<bool> polarityOrder = ResolvePolarityOrder(runOpts.PolarityMode, darkBgPrior, runOpts.FastInteractivePreview);

        return runOpts.PipelineMode switch
        {
            PpOcrPipelineMode.RecOnly => ExecuteRecOnlyWithPolarity(roiView, runOpts, polarityOrder),
            PpOcrPipelineMode.DetAndRec => ExecuteDetAndRecWithPolarity(roiView, runOpts, polarityOrder),
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
            bool darkBgPrior = EstimateIsLightOnDark(roiView);
            ReadOnlySpan<bool> polarityOrder = ResolvePolarityOrder(options.PolarityMode, darkBgPrior, options.FastInteractivePreview);
            primaryInverts[i] = polarityOrder[0];
            hasSecondaryPolarity[i] = polarityOrder.Length > 1;

            if (options.PipelineMode == PpOcrPipelineMode.DetAndRec)
            {
                results[i] = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder);
            }
            else if (options.PipelineMode == PpOcrPipelineMode.RecOnly || !IsLikelyMultiLineOrVerticalLayout(roiView))
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

        // 1) Primary polarity batched RecOnly execution across all candidate ROIs simultaneously.
        var primaryPatches = new PpOcrRoiPatch[singleLineCandidateIndices.Count];
        var primaryFlags = new bool[singleLineCandidateIndices.Count];
        for (int k = 0; k < singleLineCandidateIndices.Count; k++)
        {
            int roiIdx = singleLineCandidateIndices[k];
            primaryPatches[k] = patches[roiIdx];
            primaryFlags[k] = primaryInverts[roiIdx];
        }

        DecodedCropLine[] primaryDecoded = RecognizeCropsWithOrientationBatch(primaryPatches, primaryFlags, options);

        // 2) Collect only low-confidence survivors that need a secondary polarity pass.
        var secondaryBatchMap = new List<int>();
        var secondaryPatches = new List<PpOcrRoiPatch>();
        var secondaryFlags = new List<bool>();

        for (int k = 0; k < singleLineCandidateIndices.Count; k++)
        {
            int roiIdx = singleLineCandidateIndices[k];
            DecodedCropLine primLine = primaryDecoded[k];
            PpOcrRecognitionResult primRes = BuildSingleCropResult(patches[roiIdx], primLine, primaryInverts[roiIdx], options);
            results[roiIdx] = primRes;

            bool satisfied = primRes.Region.Characters.Count > 0 && primRes.Region.MeanConfidence >= HighConfidenceShortCircuit;
            if (!satisfied && hasSecondaryPolarity[roiIdx])
            {
                secondaryBatchMap.Add(roiIdx);
                secondaryPatches.Add(patches[roiIdx]);
                secondaryFlags.Add(!primaryInverts[roiIdx]);
            }
        }

        if (secondaryBatchMap.Count > 0)
        {
            DecodedCropLine[] secondaryDecoded = RecognizeCropsWithOrientationBatch(secondaryPatches, secondaryFlags, options);
            for (int s = 0; s < secondaryBatchMap.Count; s++)
            {
                int roiIdx = secondaryBatchMap[s];
                PpOcrRecognitionResult secRes = BuildSingleCropResult(patches[roiIdx], secondaryDecoded[s], secondaryFlags[s], options);
                results[roiIdx] = SelectHigherConfidenceResult(results[roiIdx], secRes);
            }
        }

        // 3) In Auto mode (non-interactive), any single-line candidate below SingleLineAcceptShortCircuit falls back to Det+Rec.
        if (options.PipelineMode == PpOcrPipelineMode.Auto && !options.FastInteractivePreview)
        {
            for (int k = 0; k < singleLineCandidateIndices.Count; k++)
            {
                int roiIdx = singleLineCandidateIndices[k];
                PpOcrRecognitionResult recOnlyCandidate = results[roiIdx];
                if (recOnlyCandidate.Region.Characters.Count > 0 &&
                    recOnlyCandidate.Region.MeanConfidence >= SingleLineAcceptShortCircuit)
                {
                    continue;
                }

                ReadOnlyImageView2D<byte> roiView = patches[roiIdx].AsView();
                bool darkBgPrior = EstimateIsLightOnDark(roiView);
                ReadOnlySpan<bool> polarityOrder = ResolvePolarityOrder(options.PolarityMode, darkBgPrior, options.FastInteractivePreview);
                PpOcrRecognitionResult detAndRecCandidate = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder);
                results[roiIdx] = SelectHigherConfidenceResult(detAndRecCandidate, recOnlyCandidate);
            }
        }
    }

    private PpOcrRecognitionResult ExecuteAutoPipeline(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions options,
        ReadOnlySpan<bool> polarityOrder)
    {
        bool likelyComplexLayout = IsLikelyMultiLineOrVerticalLayout(roiView);
        if (options.FastInteractivePreview)
        {
            if (!likelyComplexLayout)
            {
                return ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            }

            PpOcrRecognitionResult fastDetAndRec = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder);
            if (fastDetAndRec.Region.Characters.Count > 0 && fastDetAndRec.Region.MeanConfidence >= SingleLineAcceptShortCircuit)
            {
                return fastDetAndRec;
            }

            PpOcrRecognitionResult fastRecOnly = ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            return SelectHigherConfidenceResult(fastDetAndRec, fastRecOnly);
        }

        PpOcrRecognitionResult? recOnlyCandidate = null;

        if (!likelyComplexLayout)
        {
            recOnlyCandidate = ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            if (recOnlyCandidate.Region.Characters.Count > 0 &&
                recOnlyCandidate.Region.MeanConfidence >= SingleLineAcceptShortCircuit)
            {
                return recOnlyCandidate;
            }
        }

        PpOcrRecognitionResult detAndRecCandidate = ExecuteDetAndRecWithPolarity(roiView, options, polarityOrder);
        if (recOnlyCandidate is null)
        {
            if (detAndRecCandidate.Region.Characters.Count > 0)
            {
                return detAndRecCandidate;
            }

            return ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
        }

        return SelectHigherConfidenceResult(detAndRecCandidate, recOnlyCandidate);
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
            PpOcrRecognitionResult current = RunSingleCropRecognition(roiView, invert, options);
            best = best is null ? current : SelectHigherConfidenceResult(best, current);
            if (best.Region.Characters.Count > 0 && best.Region.MeanConfidence >= HighConfidenceShortCircuit)
            {
                break;
            }
        }

        return best!;
    }

    private PpOcrRecognitionResult ExecuteDetAndRecWithPolarity(
        ReadOnlyImageView2D<byte> roiView,
        PpOcrRunOptions options,
        ReadOnlySpan<bool> polarityOrder)
    {
        DbDetOptions detOpts = options.DetOptions ?? DbDetOptions.Default;
        PpOcrRecognitionResult? best = null;

        for (int i = 0; i < polarityOrder.Length; i++)
        {
            bool invert = polarityOrder[i];
            PpOcrRecognitionResult candidate = RunDetThenRec(roiView, invert, options, detOpts);
            best = best is null ? candidate : SelectHigherConfidenceResult(best, candidate);
            if (best.Region.Characters.Count > 0 && best.Region.MeanConfidence >= HighConfidenceShortCircuit)
            {
                break;
            }
        }

        if (best is null || best.Region.Characters.Count == 0)
        {
            PpOcrRecognitionResult fallbackRec = ExecuteRecOnlyWithPolarity(roiView, options, polarityOrder);
            if (fallbackRec.Region.Characters.Count > 0)
            {
                return fallbackRec;
            }
        }

        return best!;
    }

    private PpOcrRecognitionResult RunSingleCropRecognition(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        DecodedCropLine bestCrop = RecognizeCropWithOrientationCheck(roiView, invertPolarity, options);
        return BuildSingleCropResultFromDimensions(roiView.Width, roiView.Height, bestCrop, invertPolarity, options);
    }

    private PpOcrRecognitionResult BuildSingleCropResult(
        PpOcrRoiPatch patch,
        DecodedCropLine bestCrop,
        bool invertPolarity,
        PpOcrRunOptions options)
        => BuildSingleCropResultFromDimensions(patch.Width, patch.Height, bestCrop, invertPolarity, options);

    private PpOcrRecognitionResult BuildSingleCropResultFromDimensions(
        int roiWidth,
        int roiHeight,
        DecodedCropLine bestCrop,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        if (bestCrop.Characters.Count == 0 || string.IsNullOrWhiteSpace(bestCrop.Text))
        {
            return new PpOcrRecognitionResult(
                PpOcrModelStatus.Ready,
                StatusMessage,
                CreateEmptyRegion(darkOnLight: !invertPolarity),
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

        var box = new PpOcrBoxResult(
            fullPoly,
            bestCrop.Text,
            bestCrop.MeanConfidence,
            bestCrop.MinConfidence,
            DetScore: 1.0f,
            UsedInvertedPolarity: invertPolarity);

        var region = new UniversalOcrRegionResult(
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
        List<DetectedQuad> quads = DetectTextQuads(roiView, invertPolarity, detOpts);
        if (quads.Count == 0)
        {
            return new PpOcrRecognitionResult(
                PpOcrModelStatus.Ready,
                StatusMessage,
                CreateEmptyRegion(darkOnLight: !invertPolarity),
                [],
                ActiveDevice,
                UsedDetectionStage: true,
                UsedInvertedPolarity: invertPolarity);
        }

        List<List<DetectedQuad>> lineBuckets = GroupQuadsIntoReadingLines(quads);
        var warpedEntries = new List<WarpedQuadEntry>(quads.Count);

        try
        {
            using Mat sourceMat = PpOcrPreprocessor.CreateContiguousGrayMat(roiView, invertPolarity: false);

            for (int lineIdx = 0; lineIdx < lineBuckets.Count; lineIdx++)
            {
                List<DetectedQuad> rowQuads = lineBuckets[lineIdx];
                for (int qIdx = 0; qIdx < rowQuads.Count; qIdx++)
                {
                    DetectedQuad quad = rowQuads[qIdx];
                    using Mat warpedCrop = WarpPerspectiveQuadCrop(sourceMat, quad.Points);
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
                    CreateEmptyRegion(darkOnLight: !invertPolarity),
                    [],
                    ActiveDevice,
                    UsedDetectionStage: true,
                    UsedInvertedPolarity: invertPolarity);
            }

            // [Perf Why]: Batch all perspective-warped DBNet word crops into 1-2 width-bucketed ONNX Session.Run calls
            // instead of N sequential [1, 3, 48, W] invocations on multi-word labels/receipts.
            var cropPatches = new PpOcrRoiPatch[warpedEntries.Count];
            var cropInverts = new bool[warpedEntries.Count];
            for (int i = 0; i < warpedEntries.Count; i++)
            {
                cropPatches[i] = warpedEntries[i].Crop.AsPatch();
                cropInverts[i] = invertPolarity;
            }

            DecodedCropLine[] decodedQuads = RecognizeCropsWithOrientationBatch(cropPatches, cropInverts, options);

            var boxes = ImmutableArray.CreateBuilder<PpOcrBoxResult>(warpedEntries.Count);
            var allChars = new List<RecognizedCharBox>();
            var lineTexts = new List<string>(lineBuckets.Count);

            float weightedConfSum = 0f;
            int totalCharCount = 0;
            float overallMinConf = 1.0f;

            int entryCursor = 0;
            for (int lineIdx = 0; lineIdx < lineBuckets.Count; lineIdx++)
            {
                var rowSb = new StringBuilder();
                while (entryCursor < warpedEntries.Count && warpedEntries[entryCursor].LineIndex == lineIdx)
                {
                    WarpedQuadEntry entry = warpedEntries[entryCursor];
                    DecodedCropLine decoded = decodedQuads[entryCursor];
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

                    DetectedQuad quad = entry.Quad;
                    int minX = Math.Clamp((int)MathF.Floor(quad.MinX), 0, roiView.Width - 1);
                    int maxX = Math.Clamp((int)MathF.Ceiling(quad.MaxX), minX, roiView.Width - 1);
                    int minY = Math.Clamp((int)MathF.Floor(quad.MinY), 0, roiView.Height - 1);
                    int maxY = Math.Clamp((int)MathF.Ceiling(quad.MaxY), minY, roiView.Height - 1);
                    float boxSpanW = Math.Max(1, maxX - minX);
                    float cropSpanW = Math.Max(1, entry.Crop.Width);

                    for (int cIdx = 0; cIdx < decoded.Characters.Count; cIdx++)
                    {
                        RecognizedCharBox ch = decoded.Characters[cIdx];
                        int mappedStartX = Math.Clamp(minX + (int)MathF.Round((ch.StartCol / cropSpanW) * boxSpanW), 0, roiView.Width - 1);
                        int mappedEndX = Math.Clamp(minX + (int)MathF.Round((ch.EndCol / cropSpanW) * boxSpanW), mappedStartX, roiView.Width - 1);
                        bool spaceFlag = cIdx == 0 ? (precededByWordGap || ch.PrecededBySpace) : ch.PrecededBySpace;

                        allChars.Add(new RecognizedCharBox(
                            ch.Character,
                            ch.Confidence,
                            lineIdx,
                            mappedStartX,
                            mappedEndX,
                            minY,
                            maxY,
                            spaceFlag));

                        weightedConfSum += ch.Confidence;
                        totalCharCount++;
                        if (ch.Confidence < overallMinConf)
                        {
                            overallMinConf = ch.Confidence;
                        }
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
                        invertPolarity));
                }

                if (rowSb.Length > 0)
                {
                    lineTexts.Add(rowSb.ToString());
                }
            }

            if (totalCharCount == 0 || lineTexts.Count == 0)
            {
                return new PpOcrRecognitionResult(
                    PpOcrModelStatus.Ready,
                    StatusMessage,
                    CreateEmptyRegion(darkOnLight: !invertPolarity),
                    [],
                    ActiveDevice,
                    UsedDetectionStage: true,
                    UsedInvertedPolarity: invertPolarity);
            }

            float meanConf = weightedConfSum / totalCharCount;
            float minConf = overallMinConf <= 1.0f ? overallMinConf : 0f;
            string fullText = string.Join("\n", lineTexts);
            bool isPass = meanConf >= options.MinAcceptConfidence && minConf >= options.MinCharacterConfidence;

            var region = new UniversalOcrRegionResult(
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

    private DecodedCropLine[] RecognizeCropsWithOrientationBatch(
        IReadOnlyList<PpOcrRoiPatch> crops,
        IReadOnlyList<bool> invertFlags,
        PpOcrRunOptions options)
    {
        int count = crops.Count;
        var outputs = new DecodedCropLine[count];
        var horizontalIndices = new List<int>(count);
        var horizontalPatches = new List<PpOcrRoiPatch>(count);
        var horizontalInverts = new List<bool>(count);

        for (int i = 0; i < count; i++)
        {
            PpOcrRoiPatch patch = crops[i];
            if (!patch.IsValid)
            {
                outputs[i] = DecodedCropLine.Empty;
                continue;
            }

            if (patch.Height >= patch.Width * VerticalAspectTransposeRatio)
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

        DecodedCropLine[] uprightLines = RecognizeHorizontalCropsBatch(horizontalPatches, horizontalInverts, options);
        for (int k = 0; k < horizontalIndices.Count; k++)
        {
            outputs[horizontalIndices[k]] = uprightLines[k];
        }

        if (options.FastInteractivePreview || options.SkipOrientationFallback)
        {
            return outputs;
        }

        // Batch 180-degree rotated fallback only for low-confidence upright crops.
        var flippedMap = new List<int>();
        var flippedBuffers = new List<RentedCropBuffer>();
        var flippedPatches = new List<PpOcrRoiPatch>();
        var flippedInverts = new List<bool>();

        try
        {
            for (int k = 0; k < horizontalIndices.Count; k++)
            {
                DecodedCropLine upright = uprightLines[k];
                if (upright.MeanConfidence >= UpsideDownCheckThreshold && upright.Characters.Count > 0)
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
                DecodedCropLine[] flippedLines = RecognizeHorizontalCropsBatch(flippedPatches, flippedInverts, options);
                for (int f = 0; f < flippedMap.Count; f++)
                {
                    int origIdx = flippedMap[f];
                    outputs[origIdx] = SelectBetterCropLine(outputs[origIdx], flippedLines[f]);
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

    private DecodedCropLine RecognizeCropWithOrientationCheck(
        ReadOnlyImageView2D<byte> cropView,
        bool invertPolarity,
        PpOcrRunOptions options)
    {
        if (cropView.Height >= cropView.Width * VerticalAspectTransposeRatio)
        {
            return RecognizeVerticalCrop(cropView, invertPolarity, options);
        }

        DecodedCropLine upright = RecognizeHorizontalCrop(cropView, invertPolarity, options);
        if (options.FastInteractivePreview ||
            options.SkipOrientationFallback ||
            (upright.MeanConfidence >= UpsideDownCheckThreshold && upright.Characters.Count > 0))
        {
            return upright;
        }

        using Mat baseMat = PpOcrPreprocessor.CreateContiguousGrayMat(cropView, invertPolarity: false);
        using var rot180Mat = new Mat();
        Cv2.Rotate(baseMat, rot180Mat, RotateFlags.Rotate180);
        using RentedCropBuffer rot180Buf = RentCropFromMat(rot180Mat);
        DecodedCropLine flipped = RecognizeHorizontalCrop(rot180Buf.AsView(), invertPolarity, options);
        return SelectBetterCropLine(upright, flipped);
    }

    private DecodedCropLine RecognizeVerticalCrop(
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

        DecodedCropLine[] pair = RecognizeHorizontalCropsBatch(
            [ccwBuf.AsPatch(), cwBuf.AsPatch()],
            [invertPolarity, invertPolarity],
            options);
        return SelectBetterCropLine(pair[0], pair[1]);
    }

    private DecodedCropLine RecognizeHorizontalCrop(
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
            if (options.Language == PpOcrLanguage.English || asciiRestricted || _koreanSlot is null)
            {
                return RunRecognizerSlotSingle(_englishSlot!, tensorMemory, targetWidth, resizedContentWidth, cropView.Width, cropView.Height, options.AllowedCharacterSet);
            }

            if (options.Language == PpOcrLanguage.Korean)
            {
                return RunRecognizerSlotSingle(_koreanSlot, tensorMemory, targetWidth, resizedContentWidth, cropView.Width, cropView.Height, options.AllowedCharacterSet);
            }

            DecodedCropLine enLine = RunRecognizerSlotSingle(_englishSlot!, tensorMemory, targetWidth, resizedContentWidth, cropView.Width, cropView.Height, options.AllowedCharacterSet);
            if (ShouldShortCircuitEnglishLine(enLine, options.FastInteractivePreview))
            {
                return enLine;
            }

            DecodedCropLine koLine = RunRecognizerSlotSingle(_koreanSlot, tensorMemory, targetWidth, resizedContentWidth, cropView.Width, cropView.Height, options.AllowedCharacterSet);
            return SelectBetterCropLine(enLine, koLine);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedTensor);
        }
    }

    private DecodedCropLine[] RecognizeHorizontalCropsBatch(
        IReadOnlyList<PpOcrRoiPatch> crops,
        IReadOnlyList<bool> invertFlags,
        PpOcrRunOptions options)
    {
        int count = crops.Count;
        bool asciiRestricted = options.AllowedCharacterSet != OcrAllowedCharacterSet.All;

        if (options.Language == PpOcrLanguage.English || asciiRestricted || _koreanSlot is null)
        {
            return RunSlotAcrossCrops(_englishSlot!, crops, invertFlags, options);
        }

        if (options.Language == PpOcrLanguage.Korean)
        {
            return RunSlotAcrossCrops(_koreanSlot, crops, invertFlags, options);
        }

        DecodedCropLine[] enLines = RunSlotAcrossCrops(_englishSlot!, crops, invertFlags, options);

        // Only evaluate Korean recognizer on crops where Korean can mathematically beat English.
        var koCandidateMap = new List<int>(count);
        var koCandidatePatches = new List<PpOcrRoiPatch>(count);
        var koCandidateInverts = new List<bool>(count);

        for (int i = 0; i < count; i++)
        {
            if (ShouldShortCircuitEnglishLine(enLines[i], options.FastInteractivePreview))
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

        DecodedCropLine[] koLines = RunSlotAcrossCrops(_koreanSlot, koCandidatePatches, koCandidateInverts, options);
        for (int k = 0; k < koCandidateMap.Count; k++)
        {
            int idx = koCandidateMap[k];
            enLines[idx] = SelectBetterCropLine(enLines[idx], koLines[k]);
        }

        return enLines;
    }

    private DecodedCropLine[] RunSlotAcrossCrops(
        RecognizerSlot slot,
        IReadOnlyList<PpOcrRoiPatch> crops,
        IReadOnlyList<bool> invertFlags,
        PpOcrRunOptions options)
    {
        int count = crops.Count;
        var results = new DecodedCropLine[count];
        if (count == 0)
        {
            return results;
        }

        // [Perf Why]: On CPU, SVTR-LCNet intermediate activations at batch=2 (~1.9 MB) stay L2/L3-cache-resident,
        // whereas batch=32 (~31.5 MB) spills to DRAM and doubles per-ROI latency. Full MaxBatchSize is used on CUDA.
        int deviceBatchCap = ActiveDevice == PpOcrExecutionDevice.Cpu
            ? Math.Min(options.MaxBatchSize, CpuL2CacheOptimalBatchSize)
            : options.MaxBatchSize;
        int maxBatch = slot.SupportsDynamicBatch
            ? Math.Clamp(deviceBatchCap, 1, MaxAllowedBatchSize)
            : 1;

        var descriptors = new BatchCropDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            PpOcrRoiPatch patch = crops[i];
            PpOcrPreprocessor.ComputeRecWidths(patch.Width, patch.Height, out int targetWidth, out int resizedContentWidth);
            descriptors[i] = new BatchCropDescriptor(i, targetWidth, resizedContentWidth);
        }

        // [Perf Why]: Sorting by 32-aligned targetWidth packs similar aspect-ratio ROIs into tight mini-batches,
        // avoiding wasted right-padding FLOPs while maximizing ONNX GEMM utilization.
        Array.Sort(descriptors, static (a, b) => a.TargetWidth != b.TargetWidth
            ? a.TargetWidth.CompareTo(b.TargetWidth)
            : a.OriginalIndex.CompareTo(b.OriginalIndex));

        int start = 0;
        while (start < count)
        {
            int firstBucketWidth = descriptors[start].TargetWidth;
            int maxEnd = Math.Min(start + maxBatch, count);
            int end = start + 1;
            while (end < maxEnd && descriptors[end].TargetWidth - firstBucketWidth <= MaxBucketWidthSpreadPx)
            {
                end++;
            }

            int batchSize = end - start;
            int bucketWidth = descriptors[end - 1].TargetWidth;
            int singleItemFloats = ChannelCount * PpOcrRunOptions.OfficialRecImageHeight * bucketWidth;
            int batchTotalFloats = checked(batchSize * singleItemFloats);

            float[] rentedBatch = ArrayPool<float>.Shared.Rent(batchTotalFloats);
            try
            {
                for (int b = 0; b < batchSize; b++)
                {
                    BatchCropDescriptor desc = descriptors[start + b];
                    PpOcrRoiPatch patch = crops[desc.OriginalIndex];
                    PpOcrPreprocessor.FillRecBatchSliceInto(
                        patch.AsView(),
                        invertFlags[desc.OriginalIndex],
                        bucketWidth,
                        desc.ResizedContentWidth,
                        rentedBatch.AsSpan(b * singleItemFloats, singleItemFloats));
                }

                var dense = new DenseTensor<float>(
                    rentedBatch.AsMemory(0, batchTotalFloats),
                    [batchSize, ChannelCount, PpOcrRunOptions.OfficialRecImageHeight, bucketWidth]);
                var inputs = new List<NamedOnnxValue>(1)
                {
                    NamedOnnxValue.CreateFromTensor(slot.InputName, dense),
                };

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> onnxOutputs = slot.Session.Run(inputs);
                Tensor<float> output = onnxOutputs[0].AsTensor<float>();
                int timeSteps = output.Dimensions[1];
                int numClasses = output.Dimensions[2];
                ReadOnlySpan<bool> allowedMask = slot.GetAllowedMask(options.AllowedCharacterSet, numClasses);

                for (int b = 0; b < batchSize; b++)
                {
                    BatchCropDescriptor desc = descriptors[start + b];
                    PpOcrRoiPatch patch = crops[desc.OriginalIndex];
                    results[desc.OriginalIndex] = DecodeRecognizerBatchItem(
                        slot,
                        output,
                        b,
                        timeSteps,
                        numClasses,
                        bucketWidth,
                        desc.ResizedContentWidth,
                        patch.Width,
                        patch.Height,
                        allowedMask);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rentedBatch);
            }

            start = end;
        }

        return results;
    }

    private static DecodedCropLine RunRecognizerSlotSingle(
        RecognizerSlot slot,
        Memory<float> inputTensor,
        int targetWidth,
        int resizedContentWidth,
        int originalWidth,
        int originalHeight,
        OcrAllowedCharacterSet allowedSet)
    {
        var dense = new DenseTensor<float>(
            inputTensor,
            [1, ChannelCount, PpOcrRunOptions.OfficialRecImageHeight, targetWidth]);
        var inputs = new List<NamedOnnxValue>(1)
        {
            NamedOnnxValue.CreateFromTensor(slot.InputName, dense),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = slot.Session.Run(inputs);
        Tensor<float> output = results[0].AsTensor<float>();
        int timeSteps = output.Dimensions[1];
        int numClasses = output.Dimensions[2];
        ReadOnlySpan<bool> allowedMask = slot.GetAllowedMask(allowedSet, numClasses);

        return DecodeRecognizerBatchItem(
            slot,
            output,
            batchIndex: 0,
            timeSteps,
            numClasses,
            targetWidth,
            resizedContentWidth,
            originalWidth,
            originalHeight,
            allowedMask);
    }

    private static DecodedCropLine DecodeRecognizerBatchItem(
        RecognizerSlot slot,
        Tensor<float> output,
        int batchIndex,
        int timeSteps,
        int numClasses,
        int targetWidth,
        int resizedContentWidth,
        int originalWidth,
        int originalHeight,
        ReadOnlySpan<bool> allowedMask)
    {
        float activeStepLimit = Math.Max(1.0f, (resizedContentWidth / (float)targetWidth) * timeSteps);
        int maxValidTimeStep = Math.Clamp((int)MathF.Ceiling(activeStepLimit) + 1, 1, timeSteps);

        var sb = new StringBuilder();
        var chars = new List<RecognizedCharBox>();

        int activeRunClass = CtcBlankIndex;
        int runStartStep = 0;
        int runEndStep = 0;
        float runPeakProb = 0f;
        bool pendingSpace = false;

        DenseTensor<float>? denseOutput = output as DenseTensor<float>;
        ReadOnlySpan<float> batchSpan = denseOutput is not null
            ? denseOutput.Buffer.Span.Slice(batchIndex * timeSteps * numClasses, timeSteps * numClasses)
            : default;

        for (int t = 0; t < maxValidTimeStep; t++)
        {
            int bestIdx = FindBestAllowedClassAndProbability(
                output,
                batchSpan,
                hasContiguousSpan: denseOutput is not null,
                batchIndex,
                t,
                numClasses,
                allowedMask,
                out float bestProb);

            if (bestIdx == activeRunClass && bestIdx != CtcBlankIndex)
            {
                runEndStep = t;
                if (bestProb > runPeakProb)
                {
                    runPeakProb = bestProb;
                }
            }
            else
            {
                if (activeRunClass != CtcBlankIndex)
                {
                    FlushTokenRun(
                        slot,
                        activeRunClass,
                        runStartStep,
                        runEndStep,
                        runPeakProb,
                        timeSteps,
                        targetWidth,
                        resizedContentWidth,
                        originalWidth,
                        originalHeight,
                        sb,
                        chars,
                        ref pendingSpace);
                }

                activeRunClass = bestIdx;
                runStartStep = t;
                runEndStep = t;
                runPeakProb = Math.Clamp(bestProb, 0f, 1f);
            }
        }

        if (activeRunClass != CtcBlankIndex)
        {
            FlushTokenRun(
                slot,
                activeRunClass,
                runStartStep,
                runEndStep,
                runPeakProb,
                timeSteps,
                targetWidth,
                resizedContentWidth,
                originalWidth,
                originalHeight,
                sb,
                chars,
                ref pendingSpace);
        }

        if (chars.Count == 0 || sb.Length == 0)
        {
            return DecodedCropLine.Empty;
        }

        float sumConf = 0f;
        float minConf = 1.0f;
        for (int i = 0; i < chars.Count; i++)
        {
            float c = chars[i].Confidence;
            sumConf += c;
            if (c < minConf)
            {
                minConf = c;
            }
        }

        return new DecodedCropLine(
            sb.ToString(),
            chars,
            sumConf / chars.Count,
            minConf);
    }

    private static int FindBestAllowedClassAndProbability(
        Tensor<float> fallbackTensor,
        ReadOnlySpan<float> batchSpan,
        bool hasContiguousSpan,
        int batchIndex,
        int timeStep,
        int numClasses,
        ReadOnlySpan<bool> allowedMask,
        out float calibratedBestProbability)
    {
        // [Perf Why]: Single-pass scan over the [numClasses] row avoids copying 3,700 floats per timestep.
        // Because exp(z) is strictly monotonic, argmax over raw logits is identical to argmax over softmax probabilities.
        float minVal = float.PositiveInfinity;
        float maxVal = float.NegativeInfinity;
        float sum = 0f;
        int bestIdx = CtcBlankIndex;
        float bestRaw = float.NegativeInfinity;

        if (hasContiguousSpan)
        {
            ReadOnlySpan<float> row = batchSpan.Slice(timeStep * numClasses, numClasses);
            for (int k = 0; k < numClasses; k++)
            {
                float v = row[k];
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
                sum += v;

                if (allowedMask[k] && v > bestRaw)
                {
                    bestRaw = v;
                    bestIdx = k;
                }
            }

            bool isAlreadySoftmax = minVal >= ProbabilityMinFloor
                && maxVal <= ProbabilityMaxCeiling
                && MathF.Abs(sum - 1.0f) <= ProbabilityRowSumTolerance;

            if (isAlreadySoftmax)
            {
                calibratedBestProbability = bestRaw;
                return bestIdx;
            }

            double expSum = 0.0;
            for (int k = 0; k < numClasses; k++)
            {
                expSum += Math.Exp(row[k] - maxVal);
            }

            calibratedBestProbability = expSum > 0.0
                ? (float)(Math.Exp(bestRaw - maxVal) / expSum)
                : (1.0f / numClasses);
            return bestIdx;
        }

        for (int k = 0; k < numClasses; k++)
        {
            float v = fallbackTensor[batchIndex, timeStep, k];
            if (v < minVal) minVal = v;
            if (v > maxVal) maxVal = v;
            sum += v;

            if (allowedMask[k] && v > bestRaw)
            {
                bestRaw = v;
                bestIdx = k;
            }
        }

        bool tensorAlreadySoftmax = minVal >= ProbabilityMinFloor
            && maxVal <= ProbabilityMaxCeiling
            && MathF.Abs(sum - 1.0f) <= ProbabilityRowSumTolerance;

        if (tensorAlreadySoftmax)
        {
            calibratedBestProbability = bestRaw;
            return bestIdx;
        }

        double fallbackExpSum = 0.0;
        for (int k = 0; k < numClasses; k++)
        {
            fallbackExpSum += Math.Exp(fallbackTensor[batchIndex, timeStep, k] - maxVal);
        }

        calibratedBestProbability = fallbackExpSum > 0.0
            ? (float)(Math.Exp(bestRaw - maxVal) / fallbackExpSum)
            : (1.0f / numClasses);
        return bestIdx;
    }

    private static void FlushTokenRun(
        RecognizerSlot slot,
        int classIdx,
        int startStep,
        int endStep,
        float peakProb,
        int totalTimeSteps,
        int targetWidth,
        int resizedContentWidth,
        int originalWidth,
        int originalHeight,
        StringBuilder sb,
        List<RecognizedCharBox> chars,
        ref bool pendingSpace)
    {
        if (!slot.TryGetCharacter(classIdx, out char ch))
        {
            return;
        }

        if (ch == ' ')
        {
            if (sb.Length > 0)
            {
                pendingSpace = true;
            }

            return;
        }

        if (pendingSpace && sb.Length > 0)
        {
            sb.Append(' ');
        }

        sb.Append(ch);

        float stepToTensorX = targetWidth / (float)Math.Max(1, totalTimeSteps);
        float tensorStartX = startStep * stepToTensorX;
        float tensorEndX = (endStep + 1) * stepToTensorX;
        float tensorToOrigX = originalWidth / (float)Math.Max(1, resizedContentWidth);

        int startCol = Math.Clamp((int)MathF.Floor(tensorStartX * tensorToOrigX), 0, originalWidth - 1);
        int endCol = Math.Clamp((int)MathF.Ceiling(tensorEndX * tensorToOrigX), startCol, originalWidth - 1);

        chars.Add(new RecognizedCharBox(
            ch,
            Math.Clamp(peakProb, 0f, 1f),
            LineIndex: 0,
            StartCol: startCol,
            EndCol: endCol,
            TopRow: 0,
            BottomRow: Math.Max(0, originalHeight - 1),
            PrecededBySpace: pendingSpace));

        pendingSpace = false;
    }

    private List<DetectedQuad> DetectTextQuads(
        ReadOnlyImageView2D<byte> roiView,
        bool invertPolarity,
        DbDetOptions detOpts)
    {
        float[] rentedDetTensor = PpOcrPreprocessor.RentDetInputTensor(
            roiView,
            invertPolarity,
            detOpts,
            out int totalDetFloats,
            out int tensorW,
            out int tensorH,
            out float scaleX,
            out float scaleY,
            out int borderPadPx);

        try
        {
            var dense = new DenseTensor<float>(
                rentedDetTensor.AsMemory(0, totalDetFloats),
                [1, ChannelCount, tensorH, tensorW]);
            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(_detInputName, dense),
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _detSession!.Run(inputs);
            Tensor<float> probTensor = outputs[0].AsTensor<float>();

            using var probMat = new Mat(tensorH, tensorW, MatType.CV_32FC1);
            using var binaryMat = new Mat(tensorH, tensorW, MatType.CV_8UC1);

            unsafe
            {
                float* pDst = (float*)probMat.DataPointer;
                byte* bDst = binaryMat.DataPointer;
                long probStep = probMat.Step() / sizeof(float);
                long binStep = binaryMat.Step();

                if (probTensor is DenseTensor<float> denseProb)
                {
                    ReadOnlySpan<float> srcSpan = denseProb.Buffer.Span;
                    for (int y = 0; y < tensorH; y++)
                    {
                        int srcOff = y * tensorW;
                        float* pRow = pDst + (y * probStep);
                        byte* bRow = bDst + (y * binStep);
                        for (int x = 0; x < tensorW; x++)
                        {
                            float p = srcSpan[srcOff + x];
                            pRow[x] = p;
                            bRow[x] = p > detOpts.BinThreshold ? BinaryForegroundMaskValue : (byte)0;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < tensorH; y++)
                    {
                        float* pRow = pDst + (y * probStep);
                        byte* bRow = bDst + (y * binStep);
                        for (int x = 0; x < tensorW; x++)
                        {
                            float p = probTensor[0, 0, y, x];
                            pRow[x] = p;
                            bRow[x] = p > detOpts.BinThreshold ? BinaryForegroundMaskValue : (byte)0;
                        }
                    }
                }
            }

            Cv2.FindContours(binaryMat, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0)
            {
                return [];
            }

            int limit = Math.Min(contours.Length, Math.Max(1, detOpts.MaxCandidates));
            var quads = new List<DetectedQuad>(limit);

            for (int i = 0; i < limit; i++)
            {
                Point[] contour = contours[i];
                if (contour.Length < QuadCornerCount)
                {
                    continue;
                }

                RotatedRect minRect = Cv2.MinAreaRect(contour);
                float minSide = MathF.Min(minRect.Size.Width, minRect.Size.Height);
                if (minSide < detOpts.MinBoxSidePx)
                {
                    continue;
                }

                float boxScore = ComputeContourMeanScore(probMat, contour);
                if (boxScore < detOpts.BoxThreshold)
                {
                    continue;
                }

                Point2f[] unclippedTensorPts = UnclipContourQuad(contour, minRect, detOpts.UnclipRatio);
                Point2f[] orderedOrigPts = MapAndOrderQuadPoints(
                    unclippedTensorPts,
                    scaleX,
                    scaleY,
                    borderPadPx,
                    roiView.Width,
                    roiView.Height);

                float widthTop = Distance(orderedOrigPts[0], orderedOrigPts[1]);
                float heightLeft = Distance(orderedOrigPts[0], orderedOrigPts[LastQuadCornerIndex]);
                if (MathF.Min(widthTop, heightLeft) < detOpts.MinBoxSidePx ||
                    (widthTop * heightLeft) < detOpts.MinBoxAreaPx)
                {
                    continue;
                }

                quads.Add(new DetectedQuad(orderedOrigPts, boxScore));
            }

            return quads;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedDetTensor);
        }
    }

    private static float ComputeContourMeanScore(Mat probMat, Point[] contour)
    {
        int matWidth = probMat.Width;
        int matHeight = probMat.Height;
        Rect bbox = Cv2.BoundingRect(contour);
        int x0 = Math.Clamp(bbox.X, 0, matWidth - 1);
        int y0 = Math.Clamp(bbox.Y, 0, matHeight - 1);
        int x1 = Math.Clamp(bbox.Right, x0 + 1, matWidth);
        int y1 = Math.Clamp(bbox.Bottom, y0 + 1, matHeight);
        int rw = x1 - x0;
        int rh = y1 - y0;

        using var mask = new Mat(rh, rw, MatType.CV_8UC1, Scalar.All(0));
        var shifted = new Point[contour.Length];
        for (int i = 0; i < contour.Length; i++)
        {
            shifted[i] = new Point(contour[i].X - x0, contour[i].Y - y0);
        }

        Cv2.FillPoly(mask, [shifted], Scalar.All(BinaryForegroundMaskValue));
        using Mat subProb = new(probMat, new Rect(x0, y0, rw, rh));
        Scalar mean = Cv2.Mean(subProb, mask);
        return (float)mean.Val0;
    }

    private static Point2f[] UnclipContourQuad(Point[] contour, RotatedRect minRect, float unclipRatio)
    {
        double area = Math.Max(1.0, Cv2.ContourArea(contour));
        double perimeter = Math.Max(1.0, Cv2.ArcLength(contour, closed: true));
        float distance = (float)((area * unclipRatio) / perimeter);

        Point[] approx = Cv2.ApproxPolyDP(contour, ApproxPolyPerimeterFraction * perimeter, closed: true);
        double rectArea = Math.Max(1.0, minRect.Size.Width * minRect.Size.Height);
        if (approx.Length == QuadCornerCount && Cv2.IsContourConvex(approx) && area >= ConvexQuadAreaFillFraction * rectArea)
        {
            if (TryOffsetConvexQuad(approx, distance, out Point2f[]? offsetQuad) && offsetQuad is not null)
            {
                return offsetQuad;
            }
        }

        var expanded = new RotatedRect(
            minRect.Center,
            new Size2f(minRect.Size.Width + (2.0f * distance), minRect.Size.Height + (2.0f * distance)),
            minRect.Angle);
        return expanded.Points();
    }

    private static bool TryOffsetConvexQuad(Point[] quad, float distance, out Point2f[]? result)
    {
        result = null;
        float signedArea = 0f;
        for (int i = 0; i < QuadCornerCount; i++)
        {
            Point a = quad[i];
            Point b = quad[(i + 1) % QuadCornerCount];
            signedArea += (a.X * b.Y) - (b.X * a.Y);
        }

        float normalSign = signedArea >= 0f ? 1.0f : -1.0f;
        Span<Point2f> p0 = stackalloc Point2f[QuadCornerCount];
        Span<Point2f> dir = stackalloc Point2f[QuadCornerCount];

        for (int i = 0; i < QuadCornerCount; i++)
        {
            Point a = quad[i];
            Point b = quad[(i + 1) % QuadCornerCount];
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float len = MathF.Sqrt((dx * dx) + (dy * dy));
            if (len < DegenerateVectorEpsilon)
            {
                return false;
            }

            float ux = dx / len;
            float uy = dy / len;
            float nx = normalSign * uy;
            float ny = -normalSign * ux;
            p0[i] = new Point2f(a.X + (nx * distance), a.Y + (ny * distance));
            dir[i] = new Point2f(ux, uy);
        }

        var corners = new Point2f[QuadCornerCount];
        for (int i = 0; i < QuadCornerCount; i++)
        {
            int prev = (i + LastQuadCornerIndex) % QuadCornerCount;
            float cross = (dir[prev].X * dir[i].Y) - (dir[prev].Y * dir[i].X);
            if (MathF.Abs(cross) < DegenerateVectorEpsilon)
            {
                return false;
            }

            float qpx = p0[i].X - p0[prev].X;
            float qpy = p0[i].Y - p0[prev].Y;
            float t = ((qpx * dir[i].Y) - (qpy * dir[i].X)) / cross;
            corners[i] = new Point2f(p0[prev].X + (t * dir[prev].X), p0[prev].Y + (t * dir[prev].Y));
        }

        result = corners;
        return true;
    }

    private static Point2f[] MapAndOrderQuadPoints(
        Point2f[] tensorPts,
        float scaleX,
        float scaleY,
        int borderPadPx,
        int maxWidth,
        int maxHeight)
    {
        var mapped = new Point2f[QuadCornerCount];
        for (int i = 0; i < QuadCornerCount; i++)
        {
            float ox = (tensorPts[i].X / scaleX) - borderPadPx;
            float oy = (tensorPts[i].Y / scaleY) - borderPadPx;
            mapped[i] = new Point2f(
                Math.Clamp(ox, 0f, maxWidth - 1),
                Math.Clamp(oy, 0f, maxHeight - 1));
        }

        Array.Sort(mapped, static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        Point2f tl = mapped[0].Y <= mapped[1].Y ? mapped[0] : mapped[1];
        Point2f bl = mapped[0].Y <= mapped[1].Y ? mapped[1] : mapped[0];
        Point2f tr = mapped[2].Y <= mapped[LastQuadCornerIndex].Y ? mapped[2] : mapped[LastQuadCornerIndex];
        Point2f br = mapped[2].Y <= mapped[LastQuadCornerIndex].Y ? mapped[LastQuadCornerIndex] : mapped[2];

        return [tl, tr, br, bl];
    }

    private static Mat WarpPerspectiveQuadCrop(Mat srcMat, Point2f[] orderedPts)
    {
        int cropW = Math.Max(MinWarpedCropDimensionPx, (int)MathF.Round(MathF.Max(
            Distance(orderedPts[0], orderedPts[1]),
            Distance(orderedPts[LastQuadCornerIndex], orderedPts[2]))));
        int cropH = Math.Max(MinWarpedCropDimensionPx, (int)MathF.Round(MathF.Max(
            Distance(orderedPts[0], orderedPts[LastQuadCornerIndex]),
            Distance(orderedPts[1], orderedPts[2]))));

        Point2f[] dstPts =
        [
            new Point2f(0f, 0f),
            new Point2f(cropW - 1, 0f),
            new Point2f(cropW - 1, cropH - 1),
            new Point2f(0f, cropH - 1),
        ];

        using Mat homography = Cv2.GetPerspectiveTransform(orderedPts, dstPts);
        var warped = new Mat();
        Cv2.WarpPerspective(
            srcMat,
            warped,
            homography,
            new Size(cropW, cropH),
            InterpolationFlags.Cubic,
            BorderTypes.Replicate);
        return warped;
    }

    private static List<List<DetectedQuad>> GroupQuadsIntoReadingLines(List<DetectedQuad> quads)
    {
        quads.Sort(static (a, b) => a.CenterY != b.CenterY ? a.CenterY.CompareTo(b.CenterY) : a.CenterX.CompareTo(b.CenterX));
        var rows = new List<List<DetectedQuad>>();

        foreach (DetectedQuad q in quads)
        {
            if (rows.Count == 0)
            {
                rows.Add([q]);
                continue;
            }

            List<DetectedQuad> lastRow = rows[^1];
            float rowCenterY = 0f;
            float rowHeight = 0f;
            for (int i = 0; i < lastRow.Count; i++)
            {
                rowCenterY += lastRow[i].CenterY;
                rowHeight += lastRow[i].Height;
            }

            rowCenterY /= lastRow.Count;
            rowHeight = Math.Max(MinLineRowHeightPx, rowHeight / lastRow.Count);

            if (MathF.Abs(q.CenterY - rowCenterY) <= rowHeight * SameLineRowMergeFactor)
            {
                lastRow.Add(q);
            }
            else
            {
                rows.Add([q]);
            }
        }

        for (int r = 0; r < rows.Count; r++)
        {
            rows[r].Sort(static (a, b) => a.CenterX.CompareTo(b.CenterX));
        }

        return rows;
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

    private static bool ShouldShortCircuitEnglishLine(DecodedCropLine enLine, bool fastInteractivePreview)
    {
        if (enLine.Characters.Count == 0)
        {
            return false;
        }

        if (fastInteractivePreview && enLine.MeanConfidence >= InteractiveEnglishShortCircuit)
        {
            return true;
        }

        float enScore = (enLine.MeanConfidence * MeanConfidenceScoreWeight) + (enLine.MinConfidence * MinConfidenceScoreWeight);
        return enScore >= GuaranteedEnglishWinScore;
    }

    private static bool IsLikelyMultiLineOrVerticalLayout(ReadOnlyImageView2D<byte> roiView)
    {
        int width = roiView.Width;
        int height = roiView.Height;
        if (height >= width * MultiLineTallAspectRatio)
        {
            return true;
        }

        if (height >= MultiLineMinHeightPx && (width / (float)height) < MultiLineCompactAspectRatio)
        {
            return true;
        }

        if (height < ProjectionScanMinHeightPx)
        {
            return false;
        }

        float[] rowEnergy = ArrayPool<float>.Shared.Rent(height);
        try
        {
            Span<float> energy = rowEnergy.AsSpan(0, height);
            energy.Clear();
            float maxEnergy = 0f;
            ReadOnlySpan<byte> px = roiView.Pixels;
            int stride = roiView.Stride;

            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> row = px.Slice(y * stride, width);
                int diffSum = 0;
                for (int x = 1; x < width; x++)
                {
                    diffSum += Math.Abs(row[x] - row[x - 1]);
                }

                float e = diffSum / (float)Math.Max(1, width - 1);
                energy[y] = e;
                if (e > maxEnergy)
                {
                    maxEnergy = e;
                }
            }

            if (maxEnergy < MinRowEdgeEnergy)
            {
                return false;
            }

            float bandThreshold = maxEnergy * BandEnergyRatio;
            int bandCount = 0;
            bool inBand = false;
            int bandStart = 0;
            int gapRows = 0;

            for (int y = 0; y < height; y++)
            {
                if (energy[y] >= bandThreshold)
                {
                    if (!inBand)
                    {
                        if (bandCount == 0 || gapRows >= MinBandGapRows)
                        {
                            bandStart = y;
                            inBand = true;
                        }
                        else
                        {
                            inBand = true;
                        }
                    }

                    gapRows = 0;
                }
                else if (inBand)
                {
                    if ((y - bandStart) >= MinTextBandHeightRows)
                    {
                        bandCount++;
                    }

                    inBand = false;
                    gapRows = 1;
                }
                else
                {
                    gapRows++;
                }
            }

            if (inBand && (height - bandStart) >= MinTextBandHeightRows)
            {
                bandCount++;
            }

            return bandCount >= 2;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rowEnergy);
        }
    }

    private static bool EstimateIsLightOnDark(ReadOnlyImageView2D<byte> view)
    {
        int width = view.Width;
        int height = view.Height;
        ReadOnlySpan<byte> px = view.Pixels;
        int stride = view.Stride;

        long borderSum = 0;
        int borderCount = 0;
        ReadOnlySpan<byte> top = px.Slice(0, width);
        ReadOnlySpan<byte> bot = px.Slice((height - 1) * stride, width);
        for (int x = 0; x < width; x++)
        {
            borderSum += top[x] + bot[x];
            borderCount += 2;
        }

        for (int y = 1; y < height - 1; y++)
        {
            ReadOnlySpan<byte> row = px.Slice(y * stride, width);
            borderSum += row[0] + row[width - 1];
            borderCount += 2;
        }

        float borderMean = borderSum / (float)Math.Max(1, borderCount);
        return borderMean < DarkBackgroundBorderMeanCutoff;
    }

    private static ReadOnlySpan<bool> ResolvePolarityOrder(PpOcrPolarityMode mode, bool darkBgPrior, bool fastInteractivePreview)
    {
        if (mode == PpOcrPolarityMode.NormalOnly)
        {
            return [false];
        }

        if (mode == PpOcrPolarityMode.InvertedOnly)
        {
            return [true];
        }

        if (fastInteractivePreview)
        {
            return darkBgPrior ? [true] : [false];
        }

        return darkBgPrior ? [true, false] : [false, true];
    }

    private static PpOcrRecognitionResult SelectHigherConfidenceResult(
        PpOcrRecognitionResult first,
        PpOcrRecognitionResult second)
    {
        if (first.Region.Characters.Count == 0)
        {
            return second;
        }

        if (second.Region.Characters.Count == 0)
        {
            return first;
        }

        float score1 = (first.Region.MeanConfidence * MeanConfidenceScoreWeight) + (first.Region.MinConfidence * MinConfidenceScoreWeight);
        float score2 = (second.Region.MeanConfidence * MeanConfidenceScoreWeight) + (second.Region.MinConfidence * MinConfidenceScoreWeight);
        return score2 > score1 + PolaritySwitchScoreMargin ? second : first;
    }

    private static DecodedCropLine SelectBetterCropLine(DecodedCropLine enLine, DecodedCropLine koLine)
    {
        if (enLine.Characters.Count == 0)
        {
            return koLine;
        }

        if (koLine.Characters.Count == 0)
        {
            return enLine;
        }

        float enScore = (enLine.MeanConfidence * MeanConfidenceScoreWeight) + (enLine.MinConfidence * MinConfidenceScoreWeight);
        float koScore = (koLine.MeanConfidence * MeanConfidenceScoreWeight) + (koLine.MinConfidence * MinConfidenceScoreWeight);
        bool koHasHangul = ContainsHangul(koLine.Text);
        float margin = koHasHangul ? HangulSelectionMargin : NonHangulSelectionMargin;
        return koScore > enScore + margin ? koLine : enLine;
    }

    private static bool ContainsHangul(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((c >= '\uAC00' && c <= '\uD7A3') || (c >= '\u3131' && c <= '\u318E'))
            {
                return true;
            }
        }

        return false;
    }

    private static float Distance(Point2f a, Point2f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private PpOcrRecognitionResult CreateNotReadyResult()
        => new(
            Status,
            StatusMessage,
            CreateEmptyRegion(),
            [],
            ActiveDevice,
            UsedDetectionStage: false,
            UsedInvertedPolarity: false);

    private PpOcrRecognitionResult CreateTooSmallResult()
        => new(
            PpOcrModelStatus.Ready,
            "입력 ROI 크기가 최소 한도(4×4 px) 미만입니다.",
            CreateEmptyRegion(),
            [],
            ActiveDevice,
            UsedDetectionStage: false,
            UsedInvertedPolarity: false);

    private static UniversalOcrRegionResult CreateEmptyRegion(bool darkOnLight = true)
        => new(string.Empty, [], [], 0f, 0f, darkOnLight, IsPass: false);

    private static List<string> CollectMissingRequiredFiles(PpOcrModelOptions options)
    {
        var missing = new List<string>(ChannelCount);
        if (string.IsNullOrWhiteSpace(options.DetectionModelPath) || !File.Exists(options.DetectionModelPath))
        {
            missing.Add("ch_PP-OCRv4_det");
        }

        if (string.IsNullOrWhiteSpace(options.EnglishRecModelPath) || !File.Exists(options.EnglishRecModelPath))
        {
            missing.Add("en_PP-OCRv4_rec");
        }

        if (string.IsNullOrWhiteSpace(options.EnglishDictionaryPath) || !File.Exists(options.EnglishDictionaryPath))
        {
            missing.Add("en_dict.txt");
        }

        return missing;
    }

    private bool TryInitializeSessions(
        PpOcrModelOptions options,
        bool includeKorean,
        bool useCuda,
        out Exception? error)
    {
        try
        {
            InitializeSessionsOrThrow(options, includeKorean, useCuda);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private void InitializeSessionsOrThrow(
        PpOcrModelOptions options,
        bool includeKorean,
        bool useCuda)
    {
        var sessionOpts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = true,
            EnableMemoryPattern = true,
            IntraOpNumThreads = Math.Clamp(options.IntraOpThreads, MinIntraOpThreads, MaxIntraOpThreads),
            InterOpNumThreads = 1,
        };

        if (useCuda)
        {
            sessionOpts.AppendExecutionProvider_CUDA(options.CudaDeviceId);
        }

        _sessionOptions = sessionOpts;
        _detSession = CreateSessionTracked(options.DetectionModelPath, sessionOpts);
        _detInputName = _detSession.InputMetadata.Keys.First();
        _detOutputName = _detSession.OutputMetadata.Keys.First();
        _ = _detOutputName;

        InferenceSession enSession = CreateSessionTracked(options.EnglishRecModelPath, sessionOpts);
        char[] enVocabulary = LoadCtcVocabulary(options.EnglishDictionaryPath);
        _englishSlot = new RecognizerSlot(enSession, enVocabulary);

        if (includeKorean &&
            !string.IsNullOrWhiteSpace(options.KoreanRecModelPath) &&
            !string.IsNullOrWhiteSpace(options.KoreanDictionaryPath))
        {
            InferenceSession koSession = CreateSessionTracked(options.KoreanRecModelPath, sessionOpts);
            char[] koVocabulary = LoadCtcVocabulary(options.KoreanDictionaryPath);
            _koreanSlot = new RecognizerSlot(koSession, koVocabulary);
        }
    }

    private InferenceSession CreateSessionTracked(string modelPath, SessionOptions sessionOptions)
    {
        var session = new InferenceSession(modelPath, sessionOptions);
        _sessionCreationCount++;
        return session;
    }

    private static char[] LoadCtcVocabulary(string dictPath)
    {
        string[] rawLines = File.ReadAllLines(dictPath, Encoding.UTF8);
        var chars = new List<char>(rawLines.Length + 1);
        for (int i = 0; i < rawLines.Length; i++)
        {
            string line = rawLines[i];
            if (line.Length == 0)
            {
                continue;
            }

            chars.Add(line[0]);
        }

        if (chars.Count == 0)
        {
            throw new InvalidDataException($"PP-OCR 사전 파일이 비어 있습니다: {dictPath}");
        }

        // [Domain Why]: Official PaddleOCR BaseRecLabelDecode(use_space_char=True) appends ' ' as the final non-blank CTC class.
        chars.Add(' ');

        return [.. chars];
    }

    private void ReleaseSessionsOnly()
    {
        _koreanSlot?.Dispose();
        _koreanSlot = null;
        _englishSlot?.Dispose();
        _englishSlot = null;
        _detSession?.Dispose();
        _detSession = null;
        _sessionOptions?.Dispose();
        _sessionOptions = null;
    }

    private readonly record struct BatchCropDescriptor(
        int OriginalIndex,
        int TargetWidth,
        int ResizedContentWidth);

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
        DetectedQuad Quad,
        RentedCropBuffer Crop);

    private sealed class RecognizerSlot : IDisposable
    {
        private readonly char[] _vocabulary;
        private readonly Dictionary<(OcrAllowedCharacterSet, int), bool[]> _maskCache = [];
        private readonly object _maskLock = new();

        public InferenceSession Session { get; }

        public string InputName { get; }

        public bool SupportsDynamicBatch { get; }

        public RecognizerSlot(InferenceSession session, char[] vocabulary)
        {
            Session = session;
            InputName = session.InputMetadata.Keys.First();
            int[] dims = session.InputMetadata[InputName].Dimensions;
            SupportsDynamicBatch = dims.Length == RecTensorRank && dims[0] <= 0;
            _vocabulary = vocabulary;
        }

        public bool TryGetCharacter(int ctcClassIndex, out char ch)
        {
            if (ctcClassIndex <= CtcBlankIndex)
            {
                ch = '\0';
                return false;
            }

            int vocabIdx = ctcClassIndex - 1;
            if ((uint)vocabIdx < (uint)_vocabulary.Length)
            {
                ch = _vocabulary[vocabIdx];
                return true;
            }

            ch = '\0';
            return false;
        }

        public ReadOnlySpan<bool> GetAllowedMask(OcrAllowedCharacterSet allowedSet, int numClasses)
        {
            lock (_maskLock)
            {
                if (_maskCache.TryGetValue((allowedSet, numClasses), out bool[]? cached))
                {
                    return cached;
                }

                var mask = new bool[numClasses];
                mask[CtcBlankIndex] = true;
                for (int k = 1; k < numClasses; k++)
                {
                    if (TryGetCharacter(k, out char ch))
                    {
                        mask[k] = ch == ' ' || OcrCharacterSetFilter.Allows(allowedSet, ch);
                    }
                }

                _maskCache[(allowedSet, numClasses)] = mask;
                return mask;
            }
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed record DecodedCropLine(
        string Text,
        IReadOnlyList<RecognizedCharBox> Characters,
        float MeanConfidence,
        float MinConfidence)
    {
        public static DecodedCropLine Empty { get; } = new(string.Empty, [], 0f, 0f);
    }

    private sealed class DetectedQuad
    {
        public Point2f[] Points { get; }

        public float Score { get; }

        public float MinX { get; }

        public float MaxX { get; }

        public float MinY { get; }

        public float MaxY { get; }

        public float CenterX => 0.5f * (MinX + MaxX);

        public float CenterY => 0.5f * (MinY + MaxY);

        public float Height => Math.Max(1.0f, MaxY - MinY);

        public DetectedQuad(Point2f[] points, float score)
        {
            Points = points;
            Score = score;
            MinX = Math.Min(Math.Min(points[0].X, points[1].X), Math.Min(points[2].X, points[LastQuadCornerIndex].X));
            MaxX = Math.Max(Math.Max(points[0].X, points[1].X), Math.Max(points[2].X, points[LastQuadCornerIndex].X));
            MinY = Math.Min(Math.Min(points[0].Y, points[1].Y), Math.Min(points[2].Y, points[LastQuadCornerIndex].Y));
            MaxY = Math.Max(Math.Max(points[0].Y, points[1].Y), Math.Max(points[2].Y, points[LastQuadCornerIndex].Y));
        }
    }
}
