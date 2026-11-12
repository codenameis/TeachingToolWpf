using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.Metrology;
using VisionAlgorithm.OpenSrc.PatMax;
using VisionAlgorithm.OpenSrc.PatternMatching;
using Xunit.Abstractions;

namespace VisionAlgorithm.Tests;

public sealed class Phase4PatternMatchingGoldenTests(ITestOutputHelper output)
{
    private const string ExpectedStegerShapeHash = "76EAF197CDF84A914EC77EDCFE6EB28AC22ABBD06426EA12E9F7ADC23B938A9A";
    private const string ExpectedPatMaxHash = "453FDF6050711FC4B9F97B372972CF99748B08A0C9CAB171A4AB064363BD20B1";
    private const string ExpectedCpuMatchingHash = "C5F2F6FBC7E247C7B2A728A367484E062D80B29DC12DCE4203E68427A0AF5B8C";
    private const string ExpectedMetrologyHash = "C5289E72E013523B81AB19D0396F9727006A112FE6FF49089E2881343D46C9FA";

    [Fact]
    public void AllPatternMatchingAndMetrologyOutputs_MatchBitExactGoldenHashes()
    {
        var stegerHash = ComputeStegerShapeHash();
        var patMaxHash = ComputePatMaxHash();
        var cpuHash = ComputeCpuMatchingHash();
        var metrologyHash = ComputeMetrologyHash();

        output.WriteLine($"GOLDEN_STEGER_SHA256 = {stegerHash}");
        output.WriteLine($"GOLDEN_PATMAX_SHA256 = {patMaxHash}");
        output.WriteLine($"GOLDEN_CPUMATCH_SHA256 = {cpuHash}");
        output.WriteLine($"GOLDEN_METROLOGY_SHA256 = {metrologyHash}");

        Assert.Equal(ExpectedStegerShapeHash, stegerHash);
        Assert.Equal(ExpectedPatMaxHash, patMaxHash);
        Assert.Equal(ExpectedCpuMatchingHash, cpuHash);
        Assert.Equal(ExpectedMetrologyHash, metrologyHash);
    }

    [Fact]
    public void HotPathReleaseTimings_RecordBaseline()
    {
        // 1. StegerShape Search + ContinuousVerifier hot path
        var stegerModel = RobustShapeScene.Train();
        var stegerScene = SyntheticScene.Create(
            240,
            210,
            new MatchPose(72.35, 84.65, 18.4 * Math.PI / 180.0, 1.05),
            new MatchPose(168.20, 122.80, -37.2 * Math.PI / 180.0, 0.94));
        var stegerMatcher = new ShapeMatcher();
        var stegerSearchOptions = new ShapeSearchOptions
        {
            ClutterWeight = 0.5,
            MaxOcclusionRatio = 0.25,
            StrictContext = true
        };
        for (var warmup = 0; warmup < 2; warmup++)
        {
            _ = stegerMatcher.Search(stegerScene, stegerModel, stegerSearchOptions);
        }

        var stegerSamples = new double[5];
        for (var run = 0; run < stegerSamples.Length; run++)
        {
            var started = Stopwatch.GetTimestamp();
            var res = stegerMatcher.Search(stegerScene, stegerModel, stegerSearchOptions);
            stegerSamples[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(2, res.Matches.Length);
        }

        Array.Sort(stegerSamples);

        // 2. PatMax coarse + fine hot path
        var patTemplate = CreatePatMaxScene(32, 28, 32, 16.0f, 14.0f, 0.0f);
        var patTrainParams = PatMaxTrainParams.Create(coarseGranularity: 2, fineGranularity: 1, edgeThreshold: 12.0f, elasticityPixels: 2.0f);
        using var patModel = PatMaxTrainer.Train(patTemplate, 32, 28, 32, in patTrainParams);
        var patScene = CreatePatMaxScene(128, 96, 128, 63.4f, 47.6f, 0.12f);
        var patSearchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.55f,
            angleStartRadians: -0.35f,
            angleExtentRadians: 0.70f,
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 16,
            maxResults: 2);
        using var patEngine = new PatMaxEngine();
        Span<PatMaxResult> patResults = stackalloc PatMaxResult[2];
        for (var warmup = 0; warmup < 40; warmup++)
        {
            _ = patEngine.Find(patModel, patScene, 128, 96, 128, in patSearchParams, patResults);
        }

        var patSamples = new double[7];
        for (var run = 0; run < patSamples.Length; run++)
        {
            const int Batch = 20;
            var started = Stopwatch.GetTimestamp();
            var count = 0;
            for (var b = 0; b < Batch; b++)
            {
                count += patEngine.Find(patModel, patScene, 128, 96, 128, in patSearchParams, patResults);
            }

            patSamples[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds / Batch;
            Assert.True(count >= Batch);
        }

        Array.Sort(patSamples);

        // 3. CpuMatching Line2D + SubNcc hot path
        var cpuTemplate = CreateCpuTemplate();
        var cpuScene = CreateCpuScene(cpuTemplate);
        var line2d = new Line2DMatcher(cpuTemplate);
        var subNcc = new SubNccMatcher(cpuTemplate);
        var cpuOptions = new CpuSearchOptions
        {
            MinimumAngle = -15,
            MaximumAngle = 15,
            PyramidLevels = 3,
            MaximumDegreeOfParallelism = 1
        };
        for (var warmup = 0; warmup < 5; warmup++)
        {
            _ = line2d.Find(cpuScene, cpuOptions);
            _ = subNcc.Find(cpuScene, cpuOptions);
        }

        var line2dSamples = new double[5];
        var subNccSamples = new double[5];
        for (var run = 0; run < 5; run++)
        {
            var s1 = Stopwatch.GetTimestamp();
            Assert.Equal(3, line2d.Find(cpuScene, cpuOptions).Matches.Count);
            line2dSamples[run] = Stopwatch.GetElapsedTime(s1).TotalMilliseconds;

            var s2 = Stopwatch.GetTimestamp();
            Assert.Equal(3, subNcc.Find(cpuScene, cpuOptions).Matches.Count);
            subNccSamples[run] = Stopwatch.GetElapsedTime(s2).TotalMilliseconds;
        }

        Array.Sort(line2dSamples);
        Array.Sort(subNccSamples);

        output.WriteLine($"PERF_STEGER_MEDIAN_MS = {stegerSamples[2]:F3}");
        output.WriteLine($"PERF_PATMAX_MEDIAN_MS = {patSamples[3]:F3}");
        output.WriteLine($"PERF_LINE2D_MEDIAN_MS = {line2dSamples[2]:F3}");
        output.WriteLine($"PERF_SUBNCC_MEDIAN_MS = {subNccSamples[2]:F3}");
    }

    private static string ComputeStegerShapeHash()
    {
        var sb = new StringBuilder(32768);

        // Case A: Asymmetric shape with multi-level pyramid, clutter model, fine detail, self-symmetry & context sectors
        var trainImageA = CreateSymmetricCoreWithContextPinScene(112, 112, 56.0, 56.0, 0.0);
        var modelA = ShapeTrainer.Train(
            trainImageA,
            new TrainingRegion(ReadOnlyMemory<byte>.Empty, new ImagePoint(56, 56), 0),
            new TrainingOptions
            {
                MinimumAngleDegrees = -180,
                MaximumAngleDegrees = 180,
                AngleStepDegrees = 15,
                MinimumScale = 0.9,
                MaximumScale = 1.1,
                ScaleStep = 0.1,
                BuildClutterModel = true,
                UseFineDetailValidation = true
            });
        AppendShapeModel(sb, "ModelA_SymmetricCoreContext", modelA);

        var sceneA = CreateSymmetricCoreWithContextPinScene(160, 148, 83.25, 74.65, 90.0 * Math.PI / 180.0);
        var matcher = new ShapeMatcher();
        var searchA = matcher.Search(
            sceneA,
            modelA,
            new ShapeSearchOptions
            {
                StrictContext = true,
                ClutterWeight = 0.4,
                MaximumParallelism = 1
            });
        AppendMatchResult(sb, "SearchA_StrictContext", searchA);

        // Case B: Multi-target + occlusion + elastic/partition dead-block + group templates
        var modelB = ShapeTrainer.Train(
            SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new TrainingRegion(ReadOnlyMemory<byte>.Empty, new ImagePoint(48, 48), 0),
            new TrainingOptions
            {
                MinimumAngleDegrees = -45,
                MaximumAngleDegrees = 45,
                AngleStepDegrees = 5,
                MinimumScale = 0.9,
                MaximumScale = 1.1,
                ScaleStep = 0.1,
                BuildClutterModel = true
            });
        AppendShapeModel(sb, "ModelB_Standard", modelB);

        var rawSceneB = SyntheticScene.Create(
            220,
            160,
            new MatchPose(62.35, 78.65, 13.4 * Math.PI / 180.0, 1.04),
            new MatchPose(158.70, 82.25, -17.2 * Math.PI / 180.0, 0.96));
        var occludedPixels = rawSceneB.Pixels.ToArray();
        for (var y = 40; y < 68; y++)
        {
            for (var x = 40; x < 68; x++)
            {
                occludedPixels[(y * rawSceneB.Stride) + x] = 25;
            }
        }

        var sceneB = new ShapeGrayImage(occludedPixels, rawSceneB.Width, rawSceneB.Height, rawSceneB.Stride);
        var searchB = matcher.Search(
            sceneB,
            modelB,
            new ShapeSearchOptions
            {
                MinimumScore = 0.55,
                MinimumSupport = 0.45,
                CandidateThreshold = 0.50,
                MaxOcclusionRatio = 0.30,
                ClutterWeight = 0.35,
                GroupTemplates = true,
                MaximumParallelism = 1
            });
        AppendMatchResult(sb, "SearchB_OccludedGrouped", searchB);

        // Direct ContinuousVerifier.Score & Optimize probe
        var gradB = GradientImage.Create(sceneB, modelB.Options.MinimumGradient, CancellationToken.None);
        var directScore = ContinuousVerifier.Score(gradB, modelB, new MatchPose(62.0, 79.0, 12.0 * Math.PI / 180.0, 1.0));
        sb.AppendLine(CultureInfo.InvariantCulture, $"DirectScore|{directScore.Score:R}|{directScore.Support:R}|{directScore.Quadrants}");

        return HashString(sb.ToString());
    }

    private static string ComputePatMaxHash()
    {
        var sb = new StringBuilder(8192);
        var template = CreatePatMaxScene(32, 28, 36, 16.0f, 14.0f, 0.0f);
        var trainParams = PatMaxTrainParams.Create(
            coarseGranularity: 2,
            fineGranularity: 1,
            edgeThreshold: 12.0f,
            elasticityPixels: 2.0f);
        using var model = PatMaxTrainer.Train(template, 32, 28, 36, in trainParams);

        var scene = CreatePatMaxScene(112, 88, 120, 41.35f, 44.65f, 0.08f, secondCenterX: 84.70f, secondCenterY: 46.20f, secondAngle: -0.06f);
        var searchParams = PatMaxSearchParams.Create(
            acceptThreshold: 0.55f,
            angleStartRadians: -0.25f,
            angleExtentRadians: 0.50f,
            scaleMin: 0.95f,
            scaleMax: 1.05f,
            runtimeEdgeThreshold: 10.0f,
            maxCandidates: 16,
            maxResults: 4);

        using var engine = new PatMaxEngine();
        Span<PatMaxResult> results = stackalloc PatMaxResult[4];
        var found = engine.Find(model, scene, 112, 88, 120, in searchParams, results);
        sb.AppendLine(CultureInfo.InvariantCulture, $"PatMaxFound|{found}|{model.CoarseLevel.Dipoles.Count}|{model.FineLevel.Dipoles.Count}");
        for (var i = 0; i < found; i++)
        {
            var r = results[i];
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Res[{i}]|{r.X:R}|{r.Y:R}|{r.AngleRadians:R}|{r.Scale:R}|{r.Score:R}|{r.Coverage:R}|{r.Clutter:R}|{r.RmsFitErrorPixels:R}|{r.IterationCount}");
        }

        return HashString(sb.ToString());
    }

    private static string ComputeCpuMatchingHash()
    {
        var sb = new StringBuilder(16384);
        var template = CreateCpuTemplate();
        var scene = CreateCpuScene(template);
        var source = new GoldenWindowSource(scene);

        var options = new CpuSearchOptions
        {
            Region = new CpuSearchRegion(4, 4, scene.Width - 8, scene.Height - 8),
            MinimumAngle = -15,
            MaximumAngle = 15,
            MinimumScale = 0.95,
            MaximumScale = 1.05,
            PyramidLevels = 3,
            SuppressionRadius = 20,
            CandidateLimit = 32,
            MaximumDegreeOfParallelism = 1
        };

        var line2d = new Line2DMatcher(template);
        AppendCpuResult(sb, "Line2D_Find", line2d.Find(scene, options));
        AppendCpuResult(sb, "Line2D_Windowed", line2d.FindWindowed(source, options));

        var subNcc = new SubNccMatcher(template);
        AppendCpuResult(sb, "SubNcc_Find", subNcc.Find(scene, options));
        AppendCpuResult(sb, "SubNcc_Windowed", subNcc.FindWindowed(source, options));

        return HashString(sb.ToString());
    }

    private static string ComputeMetrologyHash()
    {
        var sb = new StringBuilder(4096);
        const int Width = 128;
        const int Height = 96;
        var pixels = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // Rotated bright stripe centered around x = 64 + 0.15*(y - 48) with subpixel anti-aliased ramp
                var center = 64.35f + (0.12f * (y - 48));
                var dist = MathF.Abs(x - center);
                var val = dist <= 13.2f ? 220f : dist >= 15.2f ? 30f : 220f - ((dist - 13.2f) * 0.5f * 190f);
                pixels[(y * Width) + x] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
            }
        }

        var view = new ReadOnlyImageView2D<byte>(pixels, Width, Height, Width);
        var pose = new FixturePose2D(64.0f, 48.0f, -0.1194289f);
        var spec1D = new CaliperMeasurementSpec(gaussianSigma: 1.2f, minimumContrast: 10.0f, projectionStripCount: 11);
        using var workspace = new InspectionWorkspace();

        var foundSingle = SubpixelCaliper1D.TryMeasureSingleEdge(
            view,
            pose,
            scanLengthPixels: 56.0f,
            projectionWidthPixels: 18.0f,
            CaliperEdgePolarity.DarkToLight,
            spec1D,
            workspace,
            out var singleHit);
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Single1D|{foundSingle}|{singleHit.SubpixelPosition:R}|{singleHit.WorldPoint.X:R}|{singleHit.WorldPoint.Y:R}|{singleHit.GradientAmplitude:R}|{singleHit.Polarity}");

        var pair1D = SubpixelCaliper1D.MeasureEdgePairWidth(
            view,
            pose,
            scanLengthPixels: 56.0f,
            projectionWidthPixels: 18.0f,
            CaliperEdgePolarity.DarkToLight,
            CaliperEdgePolarity.LightToDark,
            spec1D,
            workspace);
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Pair1D|{pair1D.IsFound}|{pair1D.SubpixelWidth:R}|{pair1D.MidpointWorld.X:R}|{pair1D.MidpointWorld.Y:R}|{pair1D.FirstEdge.SubpixelPosition:R}|{pair1D.SecondEdge.SubpixelPosition:R}");

        foreach (var mode in new[]
        {
            CaliperSelectionMode.FirstEdge,
            CaliperSelectionMode.LastEdge,
            CaliperSelectionMode.StrongestContrast,
            CaliperSelectionMode.CompositeScore
        })
        {
            var compSpec = CompositeCaliperSpec.Create(
                selectionMode: mode,
                gaussianSigma: 1.2f,
                minimumContrast: 10.0f,
                projectionStripCount: 11,
                expectedPosition: 28.0f,
                expectedSpan: 28.4f,
                positionToleranceSigma: 8.0f,
                spanToleranceSigma: 4.0f);
            var compSingle = CompositeSubpixelCaliper.MeasureSingleEdge(
                view,
                pose,
                scanLengthPixels: 56.0f,
                projectionWidthPixels: 18.0f,
                CaliperEdgePolarity.Any,
                in compSpec,
                workspace);
            var compPair = CompositeSubpixelCaliper.MeasureEdgePair(
                view,
                pose,
                scanLengthPixels: 56.0f,
                projectionWidthPixels: 18.0f,
                CaliperEdgePolarity.DarkToLight,
                CaliperEdgePolarity.LightToDark,
                in compSpec,
                workspace);
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Comp[{mode}]|{compSingle.SubpixelPosition:R}|{compSingle.GradientAmplitude:R}|{compPair.IsFound}|{compPair.SubpixelWidth:R}|{compPair.MidpointWorld.X:R}|{compPair.MidpointWorld.Y:R}");
        }

        return HashString(sb.ToString());
    }

    private static void AppendShapeModel(StringBuilder sb, string tag, ShapeModel model)
    {
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{tag}|Levels={model.Levels.Length}|Templates={model.TemplateCount}|Sym=({model.SymmetryCenter.X:R},{model.SymmetryCenter.Y:R})|CoreD={model.SymmetryCoreDiameter:R}|Radii=({model.ContextSectorRadii.X:R},{model.ContextSectorRadii.Y:R})|Occ={model.OccupiedContextSectorMask}|Emp={model.EmptyContextSectorMask}");
        foreach (var lvl in model.Levels)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Lvl|{lvl.Factor}|Count={lvl.Features.Length}");
            foreach (var f in lvl.Features)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"F|{f.X:R}|{f.Y:R}|{f.NormalX:R}|{f.NormalY:R}");
            }
        }

        if (!model.FeatureAsymmetryFlags.IsDefault)
        {
            sb.Append("AsymFlags|");
            foreach (var b in model.FeatureAsymmetryFlags)
            {
                sb.Append(b.ToString(CultureInfo.InvariantCulture)).Append(',');
            }

            sb.AppendLine();
        }
    }

    private static void AppendMatchResult(StringBuilder sb, string tag, MatchResult result)
    {
        var d = result.Diagnostics;
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{tag}|Matches={result.Matches.Length}|TemplatesEval={d.TemplatesEvaluated}|Cand={d.Candidates}|CandBefore={d.CandidatesBeforeRefinement}|Budg={d.CandidateBudgetExceeded}/{d.VerificationBudgetExceeded}/{d.MatchLimitExceeded}");
        foreach (var m in result.Matches)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"M|{m.Pose.X:R}|{m.Pose.Y:R}|{m.Pose.Angle:R}|{m.Pose.Scale:R}|{m.CandidateScore:R}|{m.Score:R}|{m.DirectionScore:R}|{m.Support:R}|{m.SupportedQuadrants}|{m.Refinement.Status}|{m.Refinement.Iterations}|{m.Refinement.FinalResidual:R}|{m.ClutterPenaltyRatio:R}|{m.DetailConsistency:R}|Unmatched={m.UnmatchedFeatures.Length}|KeyMatched={m.KeyMatchedFeatures.Length}");
        }
    }

    private static void AppendCpuResult(StringBuilder sb, string tag, SearchResult result)
    {
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{tag}|Matches={result.Matches.Count}|Eval={result.Evaluations}|Cand={result.CandidateCount}|Batches={result.CandidateBatches}|Trunc={result.CandidatesTruncated}|Limit={result.MatchLimitExceeded}");
        foreach (var m in result.Matches)
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"CM|{m.X:R}|{m.Y:R}|{m.AngleDegrees:R}|{m.Scale:R}|{m.Score:R}");
        }
    }

    private static ShapeGrayImage CreateSymmetricCoreWithContextPinScene(
        int width,
        int height,
        double cx,
        double cy,
        double angleRad)
    {
        var pixels = new byte[width * height];
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var dx = (x + ((sx + 0.5) / 4.0)) - cx;
                        var dy = (y + ((sy + 0.5) / 4.0)) - cy;
                        var u = (dx * cos) + (dy * sin);
                        var v = (-dx * sin) + (dy * cos);
                        // Symmetric central square ring + asymmetric context pin on +X side and notch on +Y
                        var rMax = Math.Max(Math.Abs(u), Math.Abs(v));
                        var coreRing = rMax <= 18.0 && rMax >= 10.0;
                        var pinRight = u >= 22.0 && u <= 30.0 && Math.Abs(v) <= 6.0;
                        var tabBottom = v >= 21.0 && v <= 26.0 && u >= -12.0 && u <= -3.0;
                        if (coreRing || pinRight || tabBottom)
                        {
                            hits++;
                        }
                    }
                }

                pixels[(y * width) + x] = (byte)(28 + ((hits * 195) / 16));
            }
        }

        return new ShapeGrayImage(pixels, width, height, width);
    }

    private static byte[] CreatePatMaxScene(
        int width,
        int height,
        int stride,
        float centerX,
        float centerY,
        float angleRad,
        float? secondCenterX = null,
        float? secondCenterY = null,
        float secondAngle = 0.0f)
    {
        var buffer = new byte[height * stride];
        Array.Fill(buffer, (byte)25);
        StampLShape(buffer, width, height, stride, centerX, centerY, angleRad);
        if (secondCenterX is { } sx && secondCenterY is { } sy)
        {
            StampLShape(buffer, width, height, stride, sx, sy, secondAngle);
        }

        return buffer;
    }

    private static void StampLShape(byte[] buffer, int width, int height, int stride, float cx, float cy, float angleRad)
    {
        var cos = MathF.Cos(angleRad);
        var sin = MathF.Sin(angleRad);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var dx = (x + ((sx + 0.5f) * 0.25f)) - cx;
                        var dy = (y + ((sy + 0.5f) * 0.25f)) - cy;
                        var u = (dx * cos) + (dy * sin);
                        var v = (-dx * sin) + (dy * cos);
                        var inside = (u >= -8.0f && u <= -3.0f && v >= -8.0f && v <= 8.0f)
                            || (u >= -3.0f && u <= 7.0f && v >= 3.0f && v <= 8.0f)
                            || (u >= 2.0f && u <= 7.0f && v >= -7.0f && v <= -3.0f);
                        if (inside)
                        {
                            hits++;
                        }
                    }
                }

                if (hits > 0)
                {
                    var gray = (byte)Math.Clamp(25 + ((hits * 200) / 16), 0, 255);
                    if (gray > buffer[(y * stride) + x])
                    {
                        buffer[(y * stride) + x] = gray;
                    }
                }
            }
        }
    }

    private static CpuGrayImage CreateCpuTemplate()
    {
        const int W = 41;
        const int H = 45;
        var pixels = new byte[W * H];
        Array.Fill(pixels, (byte)30);
        for (var y = 5; y < 40; y++)
        {
            for (var x = 5; x < 36; x++)
            {
                if (x < 13 || y > 31 || (x > 24 && y < 14))
                {
                    pixels[(y * W) + x] = (byte)(210 - ((x + y) % 17));
                }
            }
        }

        return new CpuGrayImage(W, H, pixels);
    }

    private static CpuGrayImage CreateCpuScene(CpuGrayImage template)
    {
        const int W = 320;
        const int H = 240;
        var scene = new byte[W * H];
        Array.Fill(scene, (byte)30);
        var tpl = template.Pixels.Span;
        (int X, int Y)[] placements = [(28, 32), (148, 96), (242, 164)];
        foreach (var (px, py) in placements)
        {
            for (var row = 0; row < template.Height; row++)
            {
                tpl.Slice(row * template.Width, template.Width).CopyTo(scene.AsSpan(((py + row) * W) + px, template.Width));
            }
        }

        return new CpuGrayImage(W, H, scene);
    }

    private static string HashString(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class GoldenWindowSource(CpuGrayImage image) : IGrayImageSource
    {
        public int Width => image.Width;

        public int Height => image.Height;

        public CpuGrayImage Read(CpuSearchRegion region, int reduction, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = image.Crop(region);
            for (var step = 1; step < reduction; step *= 2)
            {
                result = result.Half();
            }

            return result;
        }
    }
}
