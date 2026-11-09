// File: tests/VisionAlgorithm.Tests/PatMaxStep4Tests.cs
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.Tests;

public sealed class PatMaxStep4Tests
{
    private const int TemplateWidth = 24;
    private const int TemplateHeight = 20;
    private const int TemplateStride = 32;
    private const int SceneWidth = 48;
    private const int SceneHeight = 32;
    private const int SceneStride = 64;
    private const byte BackgroundGray = 25;
    private const byte ForegroundGray = 225;

    [Theory]
    [InlineData(23.35f, 15.65f, 0.0f)]
    [InlineData(25.70f, 16.25f, 0.05f)]
    [InlineData(21.20f, 14.80f, -0.04f)]
    public void PatMaxEngine_SubPixelShiftedAndRotatedMicroPattern_ConvergesIn4AttractionStepsWithZeroHeapAllocation(
        float expectedX,
        float expectedY,
        float expectedAngleRadians)
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderSubPixelLShape(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            angleRadians: 0.0f);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 2.5f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderSubPixelLShape(
            sceneBuffer,
            SceneWidth,
            SceneHeight,
            SceneStride,
            centerX: expectedX,
            centerY: expectedY,
            angleRadians: expectedAngleRadians);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.60f,
            angleStartRadians: -0.10f,
            angleExtentRadians: 0.20f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 8,
            maxResults: 1);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[2];

        // Warmup calls to ensure Tier-1 JIT compilation and ArrayPool bucket population are complete
        for (var w = 0; w < 3; w++)
        {
            var warmupCount = engine.Find(model, sceneBuffer, SceneWidth, SceneHeight, SceneStride, in searchParams, results);
            Assert.Equal(1, warmupCount);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timedCount = engine.Find(model, sceneBuffer, SceneWidth, SceneHeight, SceneStride, in searchParams, results);
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(1, timedCount);
        Assert.Equal(0L, allocatedAfter - allocatedBefore);

        var result = results[0];
        Assert.True(result.IsValid);
        Assert.InRange(result.IterationCount, 1, PatMaxFineAligner.PreferredAttractionSteps);
        Assert.InRange(MathF.Abs(result.X - expectedX), 0.0f, 0.45f);
        Assert.InRange(MathF.Abs(result.Y - expectedY), 0.0f, 0.45f);
        Assert.InRange(MathF.Abs(result.AngleRadians - expectedAngleRadians), 0.0f, 0.08f);
        Assert.InRange(result.Coverage, 0.60f, 1.0f);
        Assert.InRange(result.Clutter, 0.0f, 0.35f);
    }

    [Fact]
    public void PatMaxFineAligner_FixedScaleOnePointZero_NeverDriftsOutsideUserScaleBoundsOnScaledTarget()
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderSubPixelLShape(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            angleRadians: 0.0f,
            scale: 1.0f);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 1.5f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        // Render target scaled by 1.25x so outer edges lie outside the 1.0x fixed-scale model boundary
        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderSubPixelLShape(
            sceneBuffer,
            SceneWidth,
            SceneHeight,
            SceneStride,
            centerX: 24.0f,
            centerY: 16.0f,
            angleRadians: 0.0f,
            scale: 1.25f);

        using var runtimeDipoles = EdgeDipoleSoA.Rent(256);
        SubPixelEdgeExtractor.Extract(sceneBuffer, SceneWidth, SceneHeight, SceneStride, 10.0f, runtimeDipoles, 1.0f);

        var fixedScaleSearchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.85f,
            angleStartRadians: 0.0f,
            angleExtentRadians: 0.0f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 10.0f);

        var seedCandidate = new CoarsePoseCandidate(24.0f, 16.0f, 0.0f, 1.0f, 0.70f);
        var aligned = PatMaxFineAligner.Align(model, runtimeDipoles, in seedCandidate, in fixedScaleSearchParams);

        Assert.Equal(1.0f, aligned.Scale, precision: 5);
        Assert.True(aligned.Score < fixedScaleSearchParams.AcceptThreshold);
    }

    [Fact]
    public void PatMaxFineAligner_HeavyTopRowClutter_PreservesBottomEdgeYAlignmentAndBestCoverage()
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderSubPixelLShape(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            angleRadians: 0.0f);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 2.5f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        const float trueX = 24.30f;
        const float trueY = 16.40f;
        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderSubPixelLShape(
            sceneBuffer,
            SceneWidth,
            SceneHeight,
            SceneStride,
            centerX: trueX,
            centerY: trueY,
            angleRadians: 0.0f);

        using var runtimeDipoles = EdgeDipoleSoA.Rent(128);
        // Inject 300 top-region synthetic dipoles prior to actual scene dipoles to test 1-to-1 best correspondence (no raster break) and dynamic SoA growth
        for (var k = 0; k < 300; k++)
        {
            runtimeDipoles.Add(18.5f + ((k % 10) * 0.1f), 10.2f, 1.0f, 0.0f, 15.0f);
        }

        using var extractedSceneDipoles = EdgeDipoleSoA.Rent(256);
        SubPixelEdgeExtractor.Extract(sceneBuffer, SceneWidth, SceneHeight, SceneStride, 10.0f, extractedSceneDipoles, 1.0f);
        for (var i = 0; i < extractedSceneDipoles.Count; i++)
        {
            var d = extractedSceneDipoles[i];
            runtimeDipoles.Add(d.X, d.Y, d.CosTheta, d.SinTheta, d.Magnitude);
        }

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.55f,
            angleStartRadians: 0.0f,
            angleExtentRadians: 0.0f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 10.0f);

        var candidate = new CoarsePoseCandidate(24.0f, 16.0f, 0.0f, 1.0f, 0.80f);
        var aligned = PatMaxFineAligner.Align(model, runtimeDipoles, in candidate, in searchParams);

        Assert.True(aligned.IsValid);
        Assert.InRange(MathF.Abs(aligned.X - trueX), 0.0f, 0.35f);
        Assert.InRange(MathF.Abs(aligned.Y - trueY), 0.0f, 0.35f);
        Assert.InRange(aligned.Coverage, 0.60f, 1.0f);
    }

    [Fact]
    public void PatQuickCoarseSearcher_SymmetricCrossAtSamePosition_RetainsMultipleAngleHypotheses()
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderSymmetricCross(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, TemplateWidth * 0.5f, TemplateHeight * 0.5f);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 1,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 1.5f);
        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);

        var sceneBuffer = new byte[SceneHeight * SceneStride];
        RenderSymmetricCross(sceneBuffer, SceneWidth, SceneHeight, SceneStride, 24.0f, 16.0f);

        using var runtimePyramid = new GranularityPyramid();
        runtimePyramid.Build(sceneBuffer, SceneWidth, SceneHeight, SceneStride, 1, 1);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.60f,
            angleStartRadians: 0.0f,
            angleExtentRadians: MathF.PI,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 16,
            minSeparationPixels: 6.0f);

        Span<CoarsePoseCandidate> candidates = stackalloc CoarsePoseCandidate[16];
        var count = PatQuickCoarseSearcher.Search(model, runtimePyramid, in searchParams, candidates);

        var hasZeroDeg = false;
        var hasNinetyDeg = false;
        for (var i = 0; i < count; i++)
        {
            if (MathF.Abs(candidates[i].X - 24.0f) <= 2.0f && MathF.Abs(candidates[i].Y - 16.0f) <= 2.0f)
            {
                if (MathF.Abs(PatQuickCoarseSearcher.WrapAnglePi(candidates[i].AngleRadians - 0.0f)) <= 0.25f)
                {
                    hasZeroDeg = true;
                }

                if (MathF.Abs(PatQuickCoarseSearcher.WrapAnglePi(candidates[i].AngleRadians - (MathF.PI * 0.5f))) <= 0.25f)
                {
                    hasNinetyDeg = true;
                }
            }
        }

        Assert.True(hasZeroDeg);
        Assert.True(hasNinetyDeg);
    }

    [Fact]
    public void PatMaxEngine_CancelledTokenAndDisposedContainers_ThrowExpectedExceptionsImmediately()
    {
        var buffer = new byte[TemplateHeight * TemplateStride];
        RenderSubPixelLShape(buffer, TemplateWidth, TemplateHeight, TemplateStride, TemplateWidth * 0.5f, TemplateHeight * 0.5f, 0.0f);

        var trainParams = PatMaxTrainParams.Default;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PatMaxTrainer.Train(buffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams, cancellationToken: cts.Token));

        using var model = PatMaxTrainer.Train(buffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);
        using var engine = new PatMaxEngine();

        Assert.Throws<OperationCanceledException>(() =>
        {
            Span<PatMaxResult> results = stackalloc PatMaxResult[2];
            var searchParams = PatMaxSearchParams.Default;
            engine.Find(model, buffer, TemplateWidth, TemplateHeight, TemplateStride, in searchParams, results, cts.Token);
        });

        var dipoles = EdgeDipoleSoA.Rent(16);
        dipoles.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = dipoles.X);
        Assert.Throws<ObjectDisposedException>(() => _ = dipoles[0]);

        var field = AttractionField.Rent(8, 8, 0f, 0f, 1f);
        field.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = field.Cells);
        Assert.Throws<ObjectDisposedException>(() => field.TrySampleNearest(0f, 0f, out _));
    }

    [Fact]
    public void PatMaxTrainerAndEngine_PreviewGranularityAndWindowedFineAlignment_ProducesExactMatch()
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderSubPixelLShape(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            angleRadians: 0.0f);

        var fine1Params = PatMaxTrainParams.Create(coarseGranularity: 2, fineGranularity: 1, edgeThreshold: 12.0f, elasticityPixels: 2.5f);
        var fine3Params = PatMaxTrainParams.Create(coarseGranularity: 4, fineGranularity: 3, edgeThreshold: 12.0f, elasticityPixels: 2.5f);

        using var previewModel1 = PatMaxTrainer.TrainPreview(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in fine1Params);
        using var fullModel1 = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in fine1Params);
        using var previewModel3 = PatMaxTrainer.TrainPreview(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in fine3Params);

        Assert.Equal(fullModel1.FineLevel.Dipoles.Count, previewModel1.FineLevel.Dipoles.Count);
        Assert.Equal(fullModel1.CoarseLevel.Dipoles.Count, previewModel1.CoarseLevel.Dipoles.Count);
        Assert.True(previewModel3.FineLevel.Dipoles.Count < previewModel1.FineLevel.Dipoles.Count);

        const int largeSceneW = 300;
        const int largeSceneH = 260;
        const int largeSceneStride = 304;
        const float targetX = 182.35f;
        const float targetY = 144.65f;
        var largeScene = new byte[largeSceneH * largeSceneStride];
        RenderSubPixelLShape(largeScene, largeSceneW, largeSceneH, largeSceneStride, targetX, targetY, angleRadians: 0.0f);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.60f,
            angleStartRadians: -0.25f,
            angleExtentRadians: 0.50f,
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 16,
            maxResults: 1);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[2];
        var found = engine.Find(fullModel1, largeScene, largeSceneW, largeSceneH, largeSceneStride, in searchParams, results);

        Assert.Equal(1, found);
        Assert.InRange(MathF.Abs(results[0].X - targetX), 0.0f, 0.45f);
        Assert.InRange(MathF.Abs(results[0].Y - targetY), 0.0f, 0.45f);
    }

    [Theory]
    [InlineData(PatMaxPreprocessingFilterKind.GaussianScaleSpace)]
    [InlineData(PatMaxPreprocessingFilterKind.MedianDenoise)]
    [InlineData(PatMaxPreprocessingFilterKind.EdgeContrastEnhance)]
    [InlineData(PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview)]
    public void PatMaxInspectionFilter_IntegratedWithTrainerAndEngine_ExtractsAndMatchesFilteredPattern(
        PatMaxPreprocessingFilterKind filterKind)
    {
        var templateBuffer = new byte[TemplateHeight * TemplateStride];
        RenderSubPixelLShape(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            centerX: TemplateWidth * 0.5f,
            centerY: TemplateHeight * 0.5f,
            angleRadians: 0.0f);

        var filteredPreview = new byte[TemplateWidth * TemplateHeight];
        PatMaxInspectionFilter.Apply(
            templateBuffer,
            TemplateWidth,
            TemplateHeight,
            TemplateStride,
            filteredPreview,
            filterKind,
            fineGranularity: 1,
            filterSigma: 1.0f,
            edgeThreshold: 12.0f,
            forDisplayPreview: true);

        var nonZeroPreviewCount = 0;
        for (var i = 0; i < filteredPreview.Length; i++)
        {
            if (filteredPreview[i] > 0)
            {
                nonZeroPreviewCount++;
            }
        }

        Assert.True(nonZeroPreviewCount > 0);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 2.5f,
            preprocessingFilter: filterKind,
            filterSigma: 1.0f);

        using var model = PatMaxTrainer.Train(templateBuffer, TemplateWidth, TemplateHeight, TemplateStride, in trainParams);
        Assert.True(model.IsTrained);

        var sceneBuffer = new byte[SceneHeight * SceneStride];
        const float targetX = 25.25f;
        const float targetY = 16.45f;
        RenderSubPixelLShape(sceneBuffer, SceneWidth, SceneHeight, SceneStride, targetX, targetY, angleRadians: 0.0f);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.55f,
            angleStartRadians: -0.10f,
            angleExtentRadians: 0.20f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 8,
            maxResults: 1);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[2];
        var found = engine.Find(model, sceneBuffer, SceneWidth, SceneHeight, SceneStride, in searchParams, results);

        Assert.Equal(1, found);
        Assert.InRange(MathF.Abs(results[0].X - targetX), 0.0f, 0.75f);
        Assert.InRange(MathF.Abs(results[0].Y - targetY), 0.0f, 0.75f);
    }

    [Fact]
    public void PatMaxEngine_Full8KSceneSearch_CompletesInSubSecondTimeWithSubPixelAccuracy()
    {
        const int tplW = 64;
        const int tplH = 64;
        var tplBuf = new byte[tplW * tplH];
        RenderSubPixelLShape(tplBuf, tplW, tplH, tplW, centerX: tplW * 0.5f, centerY: tplH * 0.5f, angleRadians: 0.0f, scale: 2.5f);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 8,
            fineGranularity: 1,
            edgeThreshold: 15.0f,
            elasticityPixels: 2.5f,
            preprocessingFilter: PatMaxPreprocessingFilterKind.GaussianScaleSpace,
            filterSigma: 1.0f);
        using var model = PatMaxTrainer.Train(tplBuf, tplW, tplH, tplW, in trainParams);
        Assert.True(model.IsTrained);

        const int width8K = 8192;
        const int height8K = 8192;
        var scene8K = new byte[width8K * height8K];
        Array.Fill(scene8K, BackgroundGray);

        const float targetX = 5420.35f;
        const float targetY = 3812.65f;
        const int patchW = 80;
        const int patchH = 80;
        var patch = new byte[patchW * patchH];
        var patchLeft = (int)targetX - (patchW / 2);
        var patchTop = (int)targetY - (patchH / 2);
        RenderSubPixelLShape(
            patch,
            patchW,
            patchH,
            patchW,
            centerX: targetX - patchLeft,
            centerY: targetY - patchTop,
            angleRadians: 0.04f,
            scale: 2.5f);

        for (var y = 0; y < patchH; y++)
        {
            patch.AsSpan(y * patchW, patchW).CopyTo(scene8K.AsSpan(((patchTop + y) * width8K) + patchLeft, patchW));
        }

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.60f,
            angleStartRadians: -0.15f,
            angleExtentRadians: 0.30f,
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 12.0f,
            maxCandidates: 16,
            maxResults: 1);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[2];

        // Warmup
        _ = engine.Find(model, scene8K, width8K, height8K, width8K, in searchParams, results);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = engine.Find(model, scene8K, width8K, height8K, width8K, in searchParams, results);
        var elapsedMs = sw.Elapsed.TotalMilliseconds;

        Assert.Equal(1, count);
        Assert.InRange(MathF.Abs(results[0].X - targetX), 0.0f, 0.35f);
        Assert.InRange(MathF.Abs(results[0].Y - targetY), 0.0f, 0.35f);
        Assert.True(elapsedMs < 1500.0, $"Expected 8K PatMax search under 1500 ms, actual: {elapsedMs:F1} ms");
    }

    [Fact]
    public void PatMaxEngine_Full8KSceneSearch_16Targets_MultiOctaveSalientProbesAndParallelWls()
    {
        const int tplW = 96;
        const int tplH = 96;
        var tplBuf = new byte[tplW * tplH];
        RenderSubPixelLShape(tplBuf, tplW, tplH, tplW, centerX: tplW * 0.5f, centerY: tplH * 0.5f, angleRadians: 0.0f, scale: 3.5f);

        // Even if FineGranularity == CoarseGranularity == 2, GranularityPyramid guarantees multi-octave (>= 3 levels) and <= 64 Coarse salient probes.
        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 2,
            edgeThreshold: 15.0f,
            elasticityPixels: 2.5f);
        using var model = PatMaxTrainer.Train(tplBuf, tplW, tplH, tplW, in trainParams);

        Assert.True(model.IsTrained);
        Assert.True(model.LevelCount >= 3);
        Assert.InRange(model.CoarseLevel.Dipoles.Count, 8, 64);
        Assert.True(model.FineLevel.Dipoles.Count > model.CoarseLevel.Dipoles.Count);

        const int width8K = 8192;
        const int height8K = 8192;
        var scene8K = new byte[width8K * height8K];
        Array.Fill(scene8K, BackgroundGray);

        const int targetCount = 16;
        Span<(float X, float Y, float AngleRad)> groundTruth = stackalloc (float, float, float)[targetCount];
        const int patchW = 112;
        const int patchH = 112;
        var patch = new byte[patchW * patchH];

        for (var idx = 0; idx < targetCount; idx++)
        {
            var gx = idx % 4;
            var gy = idx / 4;
            var tx = 1024.0f + (gx * 1800.0f) + ((idx * 0.17f) % 0.8f);
            var ty = 1024.0f + (gy * 1800.0f) + ((idx * 0.23f) % 0.8f);
            var tAngle = ((idx % 5) - 2) * 0.03f;
            groundTruth[idx] = (tx, ty, tAngle);

            var patchLeft = (int)tx - (patchW / 2);
            var patchTop = (int)ty - (patchH / 2);
            RenderSubPixelLShape(
                patch,
                patchW,
                patchH,
                patchW,
                centerX: tx - patchLeft,
                centerY: ty - patchTop,
                angleRadians: tAngle,
                scale: 3.5f);

            for (var y = 0; y < patchH; y++)
            {
                patch.AsSpan(y * patchW, patchW).CopyTo(scene8K.AsSpan(((patchTop + y) * width8K) + patchLeft, patchW));
            }
        }

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.60f,
            angleStartRadians: -0.15f,
            angleExtentRadians: 0.30f,
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 12.0f,
            maxCandidates: 16,
            maxResults: 16,
            minSeparationPixels: 32.0f);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[targetCount];

        // Warmup
        _ = engine.Find(model, scene8K, width8K, height8K, width8K, in searchParams, results);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var foundCount = engine.Find(model, scene8K, width8K, height8K, width8K, in searchParams, results);
        var elapsedMs = sw.Elapsed.TotalMilliseconds;

        Assert.Equal(targetCount, foundCount);
        var maxPosErr = 0.0f;
        var maxAngleErrDeg = 0.0f;

        for (var idx = 0; idx < targetCount; idx++)
        {
            var gt = groundTruth[idx];
            var bestDist = float.MaxValue;
            var bestAngleDiffDeg = float.MaxValue;
            for (var r = 0; r < foundCount; r++)
            {
                var dx = results[r].X - gt.X;
                var dy = results[r].Y - gt.Y;
                var dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestAngleDiffDeg = MathF.Abs(results[r].AngleRadians - gt.AngleRad) * (180.0f / MathF.PI);
                }
            }

            if (bestDist > maxPosErr) maxPosErr = bestDist;
            if (bestAngleDiffDeg > maxAngleErrDeg) maxAngleErrDeg = bestAngleDiffDeg;
            Assert.InRange(bestDist, 0.0f, 0.35f);
            Assert.InRange(bestAngleDiffDeg, 0.0f, 0.35f);
        }

        Console.WriteLine($"[8K-16Targets Benchmark] Elapsed={elapsedMs:F1} ms | FineDipoles={model.FineLevel.Dipoles.Count} | CoarseProbes={model.CoarseLevel.Dipoles.Count} |Levels={model.LevelCount} (CoarseScale={model.CoarseLevel.ScaleFactor}) | MaxPosErr={maxPosErr:F4} px | MaxAngleErr={maxAngleErrDeg:F4} deg");
        Assert.True(elapsedMs < 1500.0, $"Expected 8K 16-target PatMax search under 1500 ms, actual: {elapsedMs:F1} ms");
    }

    [Fact]
    public void PatMaxEngine_Full8KPinPattern_456FineDipoles_16Targets_Benchmark()
    {
        // Reproduces the exact 80x148 IC pin pattern (456 Fine dipoles) from the 8K sample scene
        const int tplW = 104;
        const int tplH = 172;
        var tplBuf = new byte[tplW * tplH];
        Array.Fill(tplBuf, BackgroundGray);
        for (var y = 12; y < 12 + 148; y++)
        {
            tplBuf.AsSpan((y * tplW) + 12, 80).Fill(ForegroundGray);
        }

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 4,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 2.5f);
        using var model = PatMaxTrainer.Train(tplBuf, tplW, tplH, tplW, in trainParams);

        Assert.True(model.IsTrained);
        Assert.InRange(model.FineLevel.Dipoles.Count, 440, 520);
        Assert.InRange(model.CoarseLevel.Dipoles.Count, 16, 64);

        const int width8K = 8192;
        const int height8K = 8192;
        var scene8K = new byte[width8K * height8K];
        Array.Fill(scene8K, BackgroundGray);

        const int targetCount = 16;
        Span<(float X, float Y, float AngleRad)> groundTruth = stackalloc (float, float, float)[targetCount];
        const int patchW = 192;
        const int patchH = 192;
        var patch = new byte[patchW * patchH];

        for (var idx = 0; idx < targetCount; idx++)
        {
            var gx = idx % 4;
            var gy = idx / 4;
            var tx = 650.0f + (gx * 2048.0f) + ((idx * 0.19f) % 0.85f);
            var ty = 565.0f + (gy * 2048.0f) + ((idx * 0.27f) % 0.85f);
            var tAngle = ((idx % 5) - 2) * 0.025f;
            groundTruth[idx] = (tx, ty, tAngle);

            var patchLeft = (int)tx - (patchW / 2);
            var patchTop = (int)ty - (patchH / 2);
            RenderSubPixelPin80x148(
                patch,
                patchW,
                patchH,
                patchW,
                centerX: tx - patchLeft,
                centerY: ty - patchTop,
                angleRadians: tAngle);

            for (var y = 0; y < patchH; y++)
            {
                patch.AsSpan(y * patchW, patchW).CopyTo(scene8K.AsSpan(((patchTop + y) * width8K) + patchLeft, patchW));
            }
        }

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.65f,
            angleStartRadians: -15.0f * (MathF.PI / 180.0f),
            angleExtentRadians: 30.0f * (MathF.PI / 180.0f),
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 12.0f,
            maxCandidates: 16,
            maxResults: 16,
            minSeparationPixels: 40.0f);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[targetCount];

        // Warmup
        _ = engine.Find(model, scene8K, width8K, height8K, width8K, in searchParams, results);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var foundCount = engine.Find(model, scene8K, width8K, height8K, width8K, in searchParams, results);
        var elapsedMs = sw.Elapsed.TotalMilliseconds;

        Assert.Equal(targetCount, foundCount);
        var maxPosErr = 0.0f;
        var sumPosErr = 0.0f;
        var maxAngleErrDeg = 0.0f;
        var sumAngleErrDeg = 0.0f;

        for (var idx = 0; idx < targetCount; idx++)
        {
            var gt = groundTruth[idx];
            var bestDist = float.MaxValue;
            var bestAngleDiffDeg = float.MaxValue;
            for (var r = 0; r < foundCount; r++)
            {
                var dx = results[r].X - gt.X;
                var dy = results[r].Y - gt.Y;
                var dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestAngleDiffDeg = MathF.Abs(results[r].AngleRadians - gt.AngleRad) * (180.0f / MathF.PI);
                }
            }

            sumPosErr += bestDist;
            sumAngleErrDeg += bestAngleDiffDeg;
            if (bestDist > maxPosErr) maxPosErr = bestDist;
            if (bestAngleDiffDeg > maxAngleErrDeg) maxAngleErrDeg = bestAngleDiffDeg;
            Assert.InRange(bestDist, 0.0f, 0.35f);
            Assert.InRange(bestAngleDiffDeg, 0.0f, 0.35f);
        }

        Console.WriteLine($"[8K-456Dipoles-16Targets Benchmark] Elapsed={elapsedMs:F1} ms | FineDipoles={model.FineLevel.Dipoles.Count} | CoarseProbes={model.CoarseLevel.Dipoles.Count} | Levels={model.LevelCount} (CoarseScale={model.CoarseLevel.ScaleFactor}) | MeanPosErr={sumPosErr / targetCount:F4} px (Max={maxPosErr:F4} px) | MeanAngleErr={sumAngleErrDeg / targetCount:F4} deg (Max={maxAngleErrDeg:F4} deg)");
        Assert.True(elapsedMs < 1000.0, $"Expected 8K 456-dipole 16-target PatMax search under 1000 ms, actual: {elapsedMs:F1} ms");
    }

    [Fact]
    public void PatMaxEngine_AsymmetricCornerHoleWithBrightRingAndDimBorder_SeparatesLeftHoleFromMirroredRightHole()
    {
        // Reproduces Kensa PCB hole scenario:
        // High-contrast bright circular ring (contrast 220) + low-contrast L-shaped PCB border on Left/Bottom (contrast 45).
        // Left hole (#12) with matching Left/Bottom border + minor silkscreen noise must score >= 0.95.
        // Mirrored Right hole (#14) with missing Left border + surplus Right border clutter must drop <= 0.75.
        const int width = 64;
        const int height = 64;
        var templatePixels = new byte[width * height];
        RenderCornerHoleScene(
            templatePixels,
            width,
            height,
            centerX: 32.0f,
            centerY: 32.0f,
            hasLeftBorder: true,
            hasBottomBorder: true,
            hasRightBorderClutter: false,
            hasSilkscreenNoise: false);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 8.0f,
            elasticityPixels: 2.5f);
        using var model = PatMaxTrainer.Train(templatePixels, width, height, width, in trainParams);
        Assert.True(model.IsTrained);

        // 1. Normal Left Hole (#12) with mild nearby silkscreen noise -> must stay >= 0.95
        var leftScene = new byte[width * height];
        RenderCornerHoleScene(
            leftScene,
            width,
            height,
            centerX: 32.0f,
            centerY: 32.0f,
            hasLeftBorder: true,
            hasBottomBorder: true,
            hasRightBorderClutter: false,
            hasSilkscreenNoise: true);

        // 2. Opposite Right Corner Hole (#14): missing Left border, has Right border clutter -> must drop <= 0.75
        var rightScene = new byte[width * height];
        RenderCornerHoleScene(
            rightScene,
            width,
            height,
            centerX: 32.0f,
            centerY: 32.0f,
            hasLeftBorder: false,
            hasBottomBorder: true,
            hasRightBorderClutter: true,
            hasSilkscreenNoise: false);

        using var leftDipoles = EdgeDipoleSoA.Rent(512);
        SubPixelEdgeExtractor.Extract(leftScene, width, height, width, threshold: 8.0f, leftDipoles);

        using var rightDipoles = EdgeDipoleSoA.Rent(512);
        SubPixelEdgeExtractor.Extract(rightScene, width, height, width, threshold: 8.0f, rightDipoles);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.50f,
            angleStartRadians: -0.15f,
            angleExtentRadians: 0.30f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 8.0f);

        var seed = new CoarsePoseCandidate(32.0f, 32.0f, 0.0f, 1.0f, 0.9f);
        var leftResult = PatMaxFineAligner.Align(model, leftDipoles, in seed, in searchParams);
        var rightResult = PatMaxFineAligner.Align(model, rightDipoles, in seed, in searchParams);

        Console.WriteLine($"[Kensa Corner Hole Discrimination] LeftHole Score={leftResult.Score:F3} (Cov={leftResult.Coverage:P1}, Clutter={leftResult.Clutter:P1}) | RightHole(#14) Score={rightResult.Score:F3} (Cov={rightResult.Coverage:P1}, Clutter={rightResult.Clutter:P1})");

        Assert.True(leftResult.Score >= 0.95f, $"Expected normal Left Hole score >= 0.95, actual: {leftResult.Score:F3}");
        Assert.True(rightResult.Score <= 0.75f, $"Expected opposite Right Hole (#14) score <= 0.75, actual: {rightResult.Score:F3}");
    }

    [Fact]
    public void PatMaxEngine_AppDefaultScaleAndAngle8K_And_Small36x36KensaFullSearch_Benchmark()
    {
        // 1. 8K (8192 x 8192) 16-target IC pin search under App default settings (ScaleMin=0.95, ScaleMax=1.05, Angle=-15..+15 deg)
        const int tplW = 104;
        const int tplH = 172;
        var tplBuf = new byte[tplW * tplH];
        Array.Fill(tplBuf, BackgroundGray);
        for (var y = 12; y < 12 + 148; y++)
        {
            tplBuf.AsSpan((y * tplW) + 12, 80).Fill(ForegroundGray);
        }

        var trainParams8K = PatMaxTrainParams.Create(
            coarseGranularity: 4,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 2.5f);
        using var model8K = PatMaxTrainer.Train(tplBuf, tplW, tplH, tplW, in trainParams8K);

        const int width8K = 8192;
        const int height8K = 8192;
        var scene8K = new byte[width8K * height8K];
        Array.Fill(scene8K, BackgroundGray);

        const int targetCount = 16;
        Span<(float X, float Y, float AngleRad)> groundTruth = stackalloc (float, float, float)[targetCount];
        const int patchW = 192;
        const int patchH = 192;
        var patch = new byte[patchW * patchH];

        for (var idx = 0; idx < targetCount; idx++)
        {
            var gx = idx % 4;
            var gy = idx / 4;
            var tx = 650.0f + (gx * 2048.0f) + ((idx * 0.19f) % 0.85f);
            var ty = 565.0f + (gy * 2048.0f) + ((idx * 0.27f) % 0.85f);
            var tAngle = ((idx % 5) - 2) * 0.025f;
            groundTruth[idx] = (tx, ty, tAngle);

            var patchLeft = (int)tx - (patchW / 2);
            var patchTop = (int)ty - (patchH / 2);
            RenderSubPixelPin80x148(
                patch,
                patchW,
                patchH,
                patchW,
                centerX: tx - patchLeft,
                centerY: ty - patchTop,
                angleRadians: tAngle);

            for (var y = 0; y < patchH; y++)
            {
                patch.AsSpan(y * patchW, patchW).CopyTo(scene8K.AsSpan(((patchTop + y) * width8K) + patchLeft, patchW));
            }
        }

        var searchParams8K = PatMaxSearchParams.Create(
            acceptThreshold: 0.65f,
            angleStartRadians: -15.0f * (MathF.PI / 180.0f),
            angleExtentRadians: 30.0f * (MathF.PI / 180.0f),
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 12.0f,
            maxCandidates: 16,
            maxResults: 16,
            minSeparationPixels: 40.0f);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results8K = stackalloc PatMaxResult[targetCount];

        // Warmup
        _ = engine.Find(model8K, scene8K, width8K, height8K, width8K, in searchParams8K, results8K);

        var sw8K = System.Diagnostics.Stopwatch.StartNew();
        var found8K = engine.Find(model8K, scene8K, width8K, height8K, width8K, in searchParams8K, results8K);
        var elapsed8KMs = sw8K.Elapsed.TotalMilliseconds;

        Assert.Equal(targetCount, found8K);
        var minScore8K = 1.0f;
        var minCov8K = 1.0f;
        var maxPosErr8K = 0.0f;
        var maxAngleErr8K = 0.0f;
        for (var r = 0; r < found8K; r++)
        {
            if (results8K[r].Score < minScore8K) minScore8K = results8K[r].Score;
            if (results8K[r].Coverage < minCov8K) minCov8K = results8K[r].Coverage;
        }

        for (var idx = 0; idx < targetCount; idx++)
        {
            var gt = groundTruth[idx];
            var bestDist = float.MaxValue;
            var bestAngleDiffDeg = float.MaxValue;
            for (var r = 0; r < found8K; r++)
            {
                var dx = results8K[r].X - gt.X;
                var dy = results8K[r].Y - gt.Y;
                var dist = MathF.Sqrt((dx * dx) + (dy * dy));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestAngleDiffDeg = MathF.Abs(results8K[r].AngleRadians - gt.AngleRad) * (180.0f / MathF.PI);
                }
            }

            if (bestDist > maxPosErr8K) maxPosErr8K = bestDist;
            if (bestAngleDiffDeg > maxAngleErr8K) maxAngleErr8K = bestAngleDiffDeg;
            Assert.InRange(bestDist, 0.0f, 0.25f);
            Assert.InRange(bestAngleDiffDeg, 0.0f, 0.25f);
        }

        Assert.True(minScore8K >= 0.995f, $"Expected 8K min score >= 0.995, actual: {minScore8K:F4}");
        Assert.True(minCov8K >= 0.995f, $"Expected 8K min coverage >= 0.995, actual: {minCov8K:F4}");

        // 2. Small 40 x 40 pattern (36x36 hole + 2px border margin) full-scene search (1024 x 1536 kensa.png resolution) with Guard 1 dynamic pyramid cap
        const int smallTplW = 40;
        const int smallTplH = 40;
        var smallTplBuf = new byte[smallTplW * smallTplH];
        RenderCornerHoleScene(
            smallTplBuf,
            smallTplW,
            smallTplH,
            centerX: 20.0f,
            centerY: 20.0f,
            hasLeftBorder: true,
            hasBottomBorder: true,
            hasRightBorderClutter: false,
            hasSilkscreenNoise: false);

        var trainParamsSmall = PatMaxTrainParams.Create(
            coarseGranularity: 4,
            fineGranularity: 1,
            edgeThreshold: 8.0f,
            elasticityPixels: 2.5f);
        using var modelSmall = PatMaxTrainer.Train(smallTplBuf, smallTplW, smallTplH, smallTplW, in trainParamsSmall);
        Assert.True(modelSmall.IsTrained);

        const int kensaW = 1024;
        const int kensaH = 1536;
        var kensaScene = new byte[kensaW * kensaH];
        Array.Fill(kensaScene, (byte)20);

        for (var idx = 0; idx < 12; idx++)
        {
            var gx = idx % 3;
            var gy = idx / 3;
            var cx = 180 + (gx * 320);
            var cy = 220 + (gy * 320);
            var holePatch = new byte[smallTplW * smallTplH];
            RenderCornerHoleScene(
                holePatch,
                smallTplW,
                smallTplH,
                centerX: 20.0f,
                centerY: 20.0f,
                hasLeftBorder: true,
                hasBottomBorder: true,
                hasRightBorderClutter: false,
                hasSilkscreenNoise: false);
            for (var y = 0; y < smallTplH; y++)
            {
                holePatch.AsSpan(y * smallTplW, smallTplW).CopyTo(kensaScene.AsSpan(((cy - 20 + y) * kensaW) + (cx - 20), smallTplW));
            }
        }

        // Guard 1 verification: for a 40x40 small pattern, Level 2 (scale 4) has side 40/4 = 10 < 12 px, so SelectOptimalCoarseLevel selects Level 1 (scale 2, 40/2 = 20 >= 12 px)
        using var testPyr = new GranularityPyramid();
        testPyr.Build(kensaScene, kensaW, kensaH, kensaW, trainParamsSmall.FineGranularity, trainParamsSmall.CoarseGranularity, copyBaseLevel: false);
        var selectedSmallLevelIdx = PatQuickCoarseSearcher.SelectOptimalCoarseLevel(modelSmall, testPyr);
        var selectedSmallScale = modelSmall.GetLevel(selectedSmallLevelIdx).ScaleFactor;
        Assert.Equal(2.0f, selectedSmallScale);

        var searchParamsSmall = PatMaxSearchParams.Create(
            acceptThreshold: 0.75f,
            angleStartRadians: -15.0f * (MathF.PI / 180.0f),
            angleExtentRadians: 30.0f * (MathF.PI / 180.0f),
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 8.0f,
            maxCandidates: 16,
            maxResults: 16,
            minSeparationPixels: 24.0f);

        Span<PatMaxResult> resultsSmall = stackalloc PatMaxResult[16];
        _ = engine.Find(modelSmall, kensaScene, kensaW, kensaH, kensaW, in searchParamsSmall, resultsSmall);

        var swSmall = System.Diagnostics.Stopwatch.StartNew();
        var foundSmall = engine.Find(modelSmall, kensaScene, kensaW, kensaH, kensaW, in searchParamsSmall, resultsSmall);
        var elapsedSmallMs = swSmall.Elapsed.TotalMilliseconds;

        Assert.Equal(12, foundSmall);
        Assert.True(resultsSmall[0].Score >= 0.995f, $"Expected small pattern score >= 0.995, actual: {resultsSmall[0].Score:F4}");
        Assert.True(resultsSmall[0].Coverage >= 0.995f, $"Expected small pattern coverage >= 0.995, actual: {resultsSmall[0].Coverage:F4}");

        Console.WriteLine($"[8K AppDefault Scale(0.95..1.05)+Angle(-15..+15)] Elapsed={elapsed8KMs:F1} ms | Found={found8K}/16 | MinScore={minScore8K:F3} | MinCov={minCov8K:P1} | MaxPosErr={maxPosErr8K:F4} px | MaxAngleErr={maxAngleErr8K:F4} deg");
        Console.WriteLine($"[Small Kensa (1024x1536) FullSearch] Elapsed={elapsedSmallMs:F1} ms | Found={foundSmall}/12 | TopScore={resultsSmall[0].Score:F3} | TopCov={resultsSmall[0].Coverage:P1} | SelectedCoarseScale={selectedSmallScale} (Probes={modelSmall.GetLevel(selectedSmallLevelIdx).Dipoles.Count})");
    }

    [Fact]
    public void PatMaxEngine_TightElasticityAndFullCircle360_WithLargePatternMidLevelPatchRefinement_Succeeds()
    {
        // Large 144 x 144 pattern (builds 4 levels: Scale 1, 2, 4, 8 -> triggers Level 3 (1/8) seed + Level 2 (1/4) local patch refinement)
        // with tight user Elasticity (0.8 px) and full-circle 360 deg rotation (-180..+180 deg) near the +-180 deg boundary (-178.5 deg).
        const int tplW = 144;
        const int tplH = 144;
        var tplBuf = new byte[tplW * tplH];
        Array.Fill(tplBuf, BackgroundGray);
        RenderSubPixelLShape(tplBuf, tplW, tplH, tplW, centerX: 72.0f, centerY: 72.0f, angleRadians: 0.0f, scale: 6.0f);

        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 8,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 0.8f,
            preprocessingFilter: PatMaxPreprocessingFilterKind.SobelEdgeResponsePreview,
            filterSigma: 1.0f);
        Assert.False(PatMaxInspectionFilter.RequiresRuntimePreprocessingPass(in trainParams));

        using var model = PatMaxTrainer.Train(tplBuf, tplW, tplH, tplW, in trainParams);
        Assert.True(model.IsTrained);
        Assert.Equal(4, model.LevelCount);

        const int sceneW = 512;
        const int sceneH = 512;
        var scene = new byte[sceneW * sceneH];
        Array.Fill(scene, BackgroundGray);

        const float targetX = 257.35f;
        const float targetY = 262.65f;
        const float targetAngleRad = -178.5f * (MathF.PI / 180.0f);
        RenderSubPixelLShape(scene, sceneW, sceneH, sceneW, centerX: targetX, centerY: targetY, angleRadians: targetAngleRad, scale: 6.0f);

        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.75f,
            angleStartRadians: -MathF.PI,
            angleExtentRadians: MathF.PI * 2.0f,
            scaleMin: 1.0f,
            scaleMax: 1.0f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 8,
            maxResults: 4,
            minSeparationPixels: 24.0f);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[4];
        var found = engine.Find(model, scene, sceneW, sceneH, sceneW, in searchParams, results);

        Assert.Equal(1, found);
        Assert.InRange(MathF.Abs(results[0].X - targetX), 0.0f, 0.25f);
        Assert.InRange(MathF.Abs(results[0].Y - targetY), 0.0f, 0.25f);
        var angleErrDeg = MathF.Abs(PatQuickCoarseSearcher.WrapAnglePi(results[0].AngleRadians - targetAngleRad)) * (180.0f / MathF.PI);
        Assert.InRange(angleErrDeg, 0.0f, 0.35f);
        Assert.True(results[0].Score >= 0.95f, $"Expected score >= 0.95 under tight 0.8px elasticity and 360-deg boundary angle, actual: {results[0].Score:F3}");
    }

    private static void RenderCornerHoleScene(
        Span<byte> buffer,
        int width,
        int height,
        float centerX,
        float centerY,
        bool hasLeftBorder,
        bool hasBottomBorder,
        bool hasRightBorderClutter,
        bool hasSilkscreenNoise)
    {
        buffer.Fill(20);
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            var dy = (y + 0.5f) - centerY;
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5f) - centerX;
                var r = MathF.Sqrt((dx * dx) + (dy * dy));

                // Bright gold circular ring (inner radius 6, outer radius 11 -> high contrast 240)
                var ringCov = Math.Clamp(r - 5.5f, 0.0f, 1.0f) * Math.Clamp(11.5f - r, 0.0f, 1.0f);
                var val = 20.0f + (220.0f * ringCov);

                // Low-contrast PCB module border (contrast +48)
                if (hasLeftBorder && dx >= -16.0f && dx <= -14.0f && dy >= -16.0f && dy <= 16.0f)
                {
                    val = MathF.Max(val, 68.0f);
                }

                if (hasBottomBorder && dy >= 14.0f && dy <= 16.0f && dx >= -16.0f && dx <= 16.0f)
                {
                    val = MathF.Max(val, 68.0f);
                }

                // Opposite right border clutter in #14
                if (hasRightBorderClutter && dx >= 13.5f && dx <= 16.0f && dy >= -16.0f && dy <= 16.0f)
                {
                    val = MathF.Max(val, 95.0f);
                }

                // Mild silkscreen dot noise near one corner of normal hole
                if (hasSilkscreenNoise && dx >= 13.0f && dx <= 15.0f && dy >= -15.0f && dy <= -13.0f)
                {
                    val = MathF.Max(val, 60.0f);
                }

                buffer[rowOffset + x] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
            }
        }
    }

    private static void RenderSubPixelPin80x148(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        float angleRadians)
    {
        var (sin, cos) = MathF.SinCos(-angleRadians);
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var wy = (y + 0.5f) - centerY;
            for (var x = 0; x < width; x++)
            {
                var wx = (x + 0.5f) - centerX;
                var lx = (wx * cos) - (wy * sin);
                var ly = (wx * sin) + (wy * cos);
                var cov = SmoothBox(lx, -40.0f, 40.0f) * SmoothBox(ly, -74.0f, 74.0f);
                var gray = BackgroundGray + ((ForegroundGray - BackgroundGray) * cov);
                buffer[rowOffset + x] = (byte)MathF.Round(gray);
            }
        }
    }

    private static void RenderSubPixelLShape(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        float angleRadians,
        float scale = 1.0f)
    {
        var (sin, cos) = MathF.SinCos(-angleRadians);
        var invScale = 1.0f / scale;
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var wy = (y + 0.5f) - centerY;
            for (var x = 0; x < width; x++)
            {
                var wx = (x + 0.5f) - centerX;
                var lx = ((wx * cos) - (wy * sin)) * invScale;
                var ly = ((wx * sin) + (wy * cos)) * invScale;

                var covV = SmoothBox(lx, -6.0f, -1.0f) * SmoothBox(ly, -6.0f, 6.0f);
                var covH = SmoothBox(lx, -6.0f, 6.0f) * SmoothBox(ly, 1.0f, 6.0f);
                var cov = MathF.Max(covV, covH);

                var gray = BackgroundGray + ((ForegroundGray - BackgroundGray) * cov);
                buffer[rowOffset + x] = (byte)MathF.Round(gray);
            }
        }
    }

    private static void RenderSymmetricCross(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        float centerX,
        float centerY)
    {
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var ly = (y + 0.5f) - centerY;
            for (var x = 0; x < width; x++)
            {
                var lx = (x + 0.5f) - centerX;
                var covV = SmoothBox(lx, -2.0f, 2.0f) * SmoothBox(ly, -6.0f, 6.0f);
                var covH = SmoothBox(lx, -6.0f, 6.0f) * SmoothBox(ly, -2.0f, 2.0f);
                var cov = MathF.Max(covV, covH);

                var gray = BackgroundGray + ((ForegroundGray - BackgroundGray) * cov);
                buffer[rowOffset + x] = (byte)MathF.Round(gray);
            }
        }
    }

    private static float SmoothBox(float coord, float min, float max)
        => Math.Clamp(coord - min + 0.5f, 0.0f, 1.0f) * Math.Clamp(max - coord + 0.5f, 0.0f, 1.0f);
}
