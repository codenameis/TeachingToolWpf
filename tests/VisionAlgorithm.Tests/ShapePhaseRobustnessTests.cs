using VisionAlgorithm.OpenSrc.PatternMatching;
using Xunit.Abstractions;

namespace VisionAlgorithm.Tests;

public sealed class ShapePhaseRobustnessTests(ITestOutputHelper output)
{
    [Fact]
    public void CleanRotationScaleAndSubpixelPhaseKeepTheKnownPose()
    {
        var model = RobustGeometricScene.Train();
        var failures = new List<string>();
        foreach (var degrees in new[] { 0.3, 7.3, 22.4, 44.7, 67.1, 89.8, 137.2, 179.3 })
        {
            foreach (var scale in new[] { .8, 1, 1.2 })
            {
                foreach (var phase in new[] { .15, .65 })
                {
                    var expected = new MatchPose(87 + phase, 76 + phase, degrees * Math.PI / 180, scale);
                    Check(model, SyntheticScene.Create(173, 157, expected), expected,
                        $"angle={degrees} scale={scale} phase={phase}", failures);
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void MildPixelNoiseAndImageBoundaryKeepTheKnownPose()
    {
        var model = RobustGeometricScene.Train();
        var failures = new List<string>();
        foreach (var degrees in new[] { 13.4, 44.7, 89.8 })
        {
            foreach (var amplitude in new[] { 0, 8, 16, 24 })
            {
                var expected = new MatchPose(35.35, 36.65, degrees * Math.PI / 180, 1);
                var image = SyntheticScene.Create(97, 91, expected);
                var pixels = image.Pixels.ToArray();
                var random = new Random(8519);
                for (var index = 0; index < pixels.Length; index++)
                {
                    pixels[index] = (byte)Math.Clamp(pixels[index] + random.Next(-amplitude, amplitude + 1), 0, 255);
                }
                Check(model, new(pixels, image.Width, image.Height, image.Stride), expected,
                    $"boundary angle={degrees} uniform-noise-amplitude={amplitude}", failures);
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ThinAsymmetricContourSurvivesCoarseRotationAndPixelPhase()
    {
        var failures = new List<string>();
        foreach (var thickness in new[] { 2.2, 3.2, 4.2 })
        {
            var model = ShapeTrainer.Train(CreateWire(96, 96, new(48, 48, 0), thickness),
                new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
                {
                    MinimumAngleDegrees = -180,
                    MaximumAngleDegrees = 180,
                    AngleStepDegrees = 5
                });
            output.WriteLine($"wire thickness={thickness} model-levels={model.Levels.Length}");
            foreach (var degrees in new[] { 0.3, 7.3, 22.4, 44.7, 67.1, 89.8, 137.2, 179.3 })
            {
                foreach (var phase in new[] { .15, .65 })
                {
                    var expected = new MatchPose(87 + phase, 76 + phase, degrees * Math.PI / 180);
                    Check(model, CreateWire(173, 157, expected, thickness), expected,
                        $"wire thickness={thickness} angle={degrees} phase={phase}", failures);
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RotatedTrainingPreservesReferencePoseAcrossRotationAndScale()
    {
        var failures = new List<string>();
        foreach (var trainingDegrees in new[] { 22.4, 44.7, 67.1 })
        {
            var trainingPose = new MatchPose(48.25, 47.65, trainingDegrees * Math.PI / 180);
            var model = ShapeTrainer.Train(SyntheticScene.Create(97, 95, trainingPose),
                new(ReadOnlyMemory<byte>.Empty, new(trainingPose.X, trainingPose.Y), trainingPose.Angle), new()
                {
                    MinimumAngleDegrees = -180,
                    MaximumAngleDegrees = 180,
                    AngleStepDegrees = 5,
                    MinimumScale = .8,
                    MaximumScale = 1.2
                });
            foreach (var offsetDegrees in new[] { -135.3, -67.1, -22.4, 0.3, 44.7, 137.2 })
            {
                foreach (var scale in new[] { .8, 1.2 })
                {
                    var expected = new MatchPose(87.15, 76.65, (trainingDegrees + offsetDegrees) * Math.PI / 180, scale);
                    Check(model, SyntheticScene.Create(173, 157, expected), expected,
                        $"trained-angle={trainingDegrees} offset-angle={offsetDegrees} scale={scale}", failures);
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DefaultBudgetFindsWeakTargetsAmongEightyEightStrongDistractors()
    {
        const int TileSize = 96;
        const int Columns = 12;
        const int Rows = 8;
        const int Width = TileSize * Columns;
        const int Height = TileSize * Rows;
        var model = RobustGeometricScene.Train();
        var pixels = new byte[Width * Height];
        var expected = new List<MatchPose>();
        var random = new Random(68123);
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var degrees = (((row * Columns) + column) * 37.1 % 360) - 180;
                var pose = new MatchPose(48.35, 48.65, degrees * Math.PI / 180);
                var target = column == Columns - 1;
                var tile = target ? SyntheticScene.Create(TileSize, TileSize, pose) : CreateDistractor(TileSize, pose);
                if (target)
                {
                    expected.Add(pose with
                    {
                        X = pose.X + (column * TileSize),
                        Y = pose.Y + (row * TileSize)
                    });
                }
                for (var y = 0; y < TileSize; y++)
                {
                    for (var x = 0; x < TileSize; x++)
                    {
                        var value = tile.Pixels.Span[(y * TileSize) + x];
                        if (target)
                        {
                            value = (byte)Math.Clamp(25 + ((value - 25) / 2) + random.Next(-12, 13), 0, 255);
                        }
                        pixels[(((row * TileSize) + y) * Width) + (column * TileSize) + x] = value;
                    }
                }
            }
        }
        var scene = new GrayImage(pixels, Width, Height, Width);
        var options = new SearchOptions { ClutterWeight = 1, MaximumMatches = 1000, MaximumParallelism = 4 };
        var matcher = new ShapeMatcher();
        var result = matcher.Search(scene, model, options);
        var missing = expected.Where(pose => !result.Matches.Any(match => SameTarget(match.Pose, pose))).ToArray();
        var incorrect = result.Matches.Where(match => !expected.Any(pose => SameTarget(match.Pose, pose))).ToArray();
        output.WriteLine($"dense default: matches={result.Matches.Length}, missing={missing.Length}, incorrect={incorrect.Length}, {result.Diagnostics}");
        foreach (var pose in missing)
        {
            var direct = matcher.RefineCandidate(scene, model, pose, 1, options);
            output.WriteLine($"missing expected={pose}; direct-pose matches={direct.Matches.Length}; score={direct.Matches.FirstOrDefault()?.Score}");
        }
        foreach (var match in incorrect)
        {
            output.WriteLine($"incorrect={match}");
        }
        Assert.True(missing.Length == 0 && incorrect.Length == 0,
            $"8 weak targets/88 strong distractors: missing={missing.Length}, incorrect={incorrect.Length}, {result.Diagnostics}");
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, .5)]
    [InlineData(false, true, .5)]
    [InlineData(true, false, .5)]
    [InlineData(true, true, .5)]
    public void CorrelatedNoiseDoesNotRejectUnchangedTargetGeometry(bool rotated, bool geometry, double contrast)
    {
        var model = TrainBlockNoiseModel(geometry);
        var pose = new MatchPose(320, 320, rotated ? 17 * Math.PI / 180 : 0, rotated ? .9 : 1);
        var scene = rotated ? CreateBlockNoiseTile(3072, 1024, 17, .9) : CreateBlockNoiseTile(1024, 3072, 0, 1);
        if (contrast != 1)
        {
            var pixels = scene.Pixels.ToArray();
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)Math.Round(128 + ((pixels[index] - 128) * contrast));
            }
            scene = new(pixels, scene.Width, scene.Height, scene.Stride);
        }
        var gradient = GradientImage.Create(scene, 8, default);
        var trueScore = ContinuousVerifier.Score(gradient, model, pose);
        var trueClutter = GeometricQualityEvaluator.EvaluateClutter(gradient, model, pose, 3, default);
        var matcher = new ShapeMatcher();
        var directionOnly = matcher.RefineCandidate(scene, model, pose, 1, new()
        {
            ClutterWeight = 0,
            EvaluateGeometry = geometry
        });
        var scored = matcher.RefineCandidate(scene, model, pose, 1, new()
        {
            ClutterWeight = 1,
            EvaluateGeometry = geometry
        });
        var message = $"correlated-noise rotated={rotated}, geometry={geometry}, contrast={contrast}: true-direction={trueScore}, true-clutter={trueClutter}, " +
            $"direction-only={directionOnly.Matches.Length}, final={scored.Matches.Length}, " +
            $"refined-direction={directionOnly.Matches.FirstOrDefault()?.DirectionScore}, refined-clutter={directionOnly.Matches.FirstOrDefault()?.ClutterRatio}";
        output.WriteLine(message);
        Assert.True(scored.Matches.Length == 1, message);
        Assert.True(SameTarget(scored.Matches[0].Pose, pose), $"{message}; actual={scored.Matches[0].Pose}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoiseRobustnessStillPenalizesAdditionalStrongContour(bool geometry)
    {
        var model = TrainBlockNoiseModel(geometry);
        var pose = new MatchPose(320, 320, 0);
        var clean = GradientImage.Create(CreateBlockNoiseTile(1024, 3072, 0, 1), 8, default);
        var extra = GradientImage.Create(CreateBlockNoiseTile(1024, 3072, 0, 1, extraContour: true), 8, default);
        var cleanScore = ContinuousVerifier.Score(clean, model, pose).Score;
        var extraScore = ContinuousVerifier.Score(extra, model, pose).Score;
        var cleanClutter = GeometricQualityEvaluator.EvaluateClutter(clean, model, pose, 3, default);
        var extraClutter = GeometricQualityEvaluator.EvaluateClutter(extra, model, pose, 3, default);
        output.WriteLine($"geometry={geometry}: clean direction={cleanScore}, clutter={cleanClutter}; extra direction={extraScore}, clutter={extraClutter}");
        Assert.True(extraClutter.EnergyRatio > cleanClutter.EnergyRatio + .05);
        Assert.True(extraScore * (1 - extraClutter.EnergyRatio) < (cleanScore * (1 - cleanClutter.EnergyRatio)) - .05);
    }

    [Fact]
    public void IdenticalNoisyTrainingImageKeepsItsEntireContourExplained()
    {
        var model = TrainBlockNoiseModel(true);
        var scene = CreateBlockNoiseTile(1024, 1024, 0, 1);
        var pose = new MatchPose(320, 320, 0);
        var gradient = GradientImage.Create(scene, 8, default);
        var clutter = GeometricQualityEvaluator.EvaluateClutter(gradient, model, pose, 3, default);
        var result = new ShapeMatcher().RefineCandidate(scene, model, pose, 1, new()
        {
            ClutterWeight = 1,
            MaximumClutterRatio = .05,
            Refine = false
        });
        output.WriteLine($"identical training pixels: clutter={clutter}, matches={result.Matches.Length}");
        Assert.Equal(0, clutter.Ratio, 10);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void ExplicitZeroContrastRatioKeepsLowContrastDetailsBesideStrongEdges()
    {
        var template = CreateMixedContrastScene(includeDetail: true);
        var missingDetail = CreateMixedContrastScene(includeDetail: false);
        var training = new TrainingOptions
        {
            PyramidLevels = 1,
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0,
            BuildClutterModel = true
        };
        var region = new TrainingRegion(ReadOnlyMemory<byte>.Empty, new(128, 128));
        var defaultModel = ShapeTrainer.Train(template, region, training);
        var detailedModel = ShapeTrainer.Train(template, region, training with
        {
            MinimumFeatureContrastRatio = 0
        });
        var pose = new MatchPose(128, 128, 0);
        var options = new SearchOptions { ClutterWeight = 1, Refine = false };
        var matcher = new ShapeMatcher();
        var defaultMissing = matcher.RefineCandidate(missingDetail, defaultModel, pose, 1, options);
        var detailedMissing = matcher.RefineCandidate(missingDetail, detailedModel, pose, 1, options);
        var detailedComplete = matcher.RefineCandidate(template, detailedModel, pose, 1, options);
        output.WriteLine($"mixed contrast: default missing-detail={defaultMissing.Matches.Length}, " +
            $"score={defaultMissing.Matches.FirstOrDefault()?.Score}; explicit zero missing-detail={detailedMissing.Matches.Length}, " +
            $"complete={detailedComplete.Matches.Length}, complete-score={detailedComplete.Matches.FirstOrDefault()?.Score}");
        Assert.Empty(detailedMissing.Matches);
        Assert.Single(detailedComplete.Matches);
    }

    [Fact]
    public void BrighterLocalizedContourDoesNotDiluteItsOwnPenalty()
    {
        var training = CreateLocalContrastScene(55);
        var model = TrainLocalContrastModel(training);
        var ratios = new List<double>();
        foreach (var intensity in new byte[] { 55, 75, 115, 175, 235 })
        {
            var scene = CreateLocalContrastScene(intensity);
            var gradient = GradientImage.Create(scene, 8, default);
            var clutter = GeometricQualityEvaluator.EvaluateClutter(gradient, model, new(128, 128, 0), 3, default);
            output.WriteLine($"localized intensity={intensity}: clutter={clutter}");
            if (ratios.Count > 0)
            {
                Assert.True(clutter.EnergyRatio >= ratios[^1] - 1e-10,
                    $"A brighter localized contour reduced its penalty: {ratios[^1]} -> {clutter.EnergyRatio}.");
            }
            ratios.Add(clutter.EnergyRatio);
        }
        Assert.True(ratios[^1] > ratios[0] + .1);
    }

    [Fact]
    public void SmallBrightDefectDoesNotNormalizeAwayAnExistingContourDefect()
    {
        var model = TrainLocalContrastModel(CreateLocalContrastScene(55));
        var defect = GradientImage.Create(CreateLocalContrastScene(115), 8, default);
        var moreDefects = GradientImage.Create(CreateLocalContrastScene(115, brightPatch: true), 8, default);
        var pose = new MatchPose(128, 128, 0);
        var before = GeometricQualityEvaluator.EvaluateClutter(defect, model, pose, 3, default);
        var after = GeometricQualityEvaluator.EvaluateClutter(moreDefects, model, pose, 3, default);
        output.WriteLine($"existing defect={before}; additional bright patch={after}");
        Assert.True(before.EnergyRatio > .1);
        Assert.True(after.EnergyRatio >= before.EnergyRatio - 1e-10,
            $"Adding a separate bright defect reduced the original penalty: {before.EnergyRatio} -> {after.EnergyRatio}.");
    }

    [Fact]
    public void BrightPatchInsideExcludedMaskHoleDoesNotChangeContrastOrClutter()
    {
        const int Size = 256;
        var mask = Enumerable.Repeat((byte)255, Size * Size).ToArray();
        for (var row = 228; row < 252; row++)
        {
            mask.AsSpan((row * Size) + 176, 64).Clear();
        }
        var model = TrainLocalContrastModel(CreateLocalContrastScene(55), mask);
        var pose = new MatchPose(128, 128, 0);
        var baseline = GradientImage.Create(CreateLocalContrastScene(115), 8, default);
        var outside = GradientImage.Create(CreateLocalContrastScene(115, brightPatch: true), 8, default);
        var before = GeometricQualityEvaluator.EvaluateClutter(baseline, model, pose, 3, default);
        var after = GeometricQualityEvaluator.EvaluateClutter(outside, model, pose, 3, default);
        output.WriteLine($"masked hole: baseline={before}; bright outside-mask patch={after}");
        Assert.Equal(before, after);
    }

    [Fact]
    public void OutsideMaskEdgeCannotBecomeAnIlluminationReference()
    {
        const int Size = 256;
        var mask = new byte[Size * Size];
        for (var row = 16; row < 240; row++)
        {
            mask.AsSpan((row * Size) + 29, 3).Fill(255);
        }
        for (var row = 30; row < 225; row++)
        {
            mask.AsSpan((row * Size) + 110, 131).Fill(255);
        }
        var model = TrainLocalContrastModel(CreateBoundaryContrastScene(30, 110, 55), mask);
        var missingReference = GradientImage.Create(CreateBoundaryContrastScene(null, 110, 115), 8, default);
        var outsideReference = GradientImage.Create(CreateBoundaryContrastScene(28, 250, 115), 8, default);
        var pose = new MatchPose(128, 128, 0);
        var before = GeometricQualityEvaluator.EvaluateClutter(missingReference, model, pose, 3, default);
        var after = GeometricQualityEvaluator.EvaluateClutter(outsideReference, model, pose, 3, default);
        output.WriteLine($"no valid reference={before}; nearby reference edge outside actual mask={after}");
        Assert.Equal(before.SceneEdges, after.SceneEdges);
        Assert.Equal(before.Ratio, after.Ratio, 10);
        Assert.Equal(before.EnergyRatio, after.EnergyRatio, 10);
    }

    private static ShapeModel TrainLocalContrastModel(GrayImage training, ReadOnlyMemory<byte> mask = default)
        => ShapeTrainer.Train(training, new(mask, new(128, 128)), new()
        {
            PyramidLevels = 1,
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0,
            BuildClutterModel = true
        });

    private static ShapeModel TrainBlockNoiseModel(bool geometry)
    {
        const int Size = 640;
        var mask = new byte[Size * Size];
        for (var row = 64; row < Size - 64; row++)
        {
            mask.AsSpan((row * Size) + 64, 512).Fill(255);
        }
        return ShapeTrainer.Train(CreateBlockNoiseTile(1024, 1024, 0, 1), new(mask, new(320, 320)), new()
        {
            PyramidLevels = 4,
            MinimumAngleDegrees = -45,
            MaximumAngleDegrees = 45,
            AngleStepDegrees = 10,
            MinimumScale = .85,
            MaximumScale = 1.15,
            ScaleStep = .05,
            BuildGeometricModel = geometry,
            BuildClutterModel = true
        });
    }

    private void Check(ShapeModel model, GrayImage scene, MatchPose expected, string name, List<string> failures)
    {
        var options = new SearchOptions
        {
            ClutterWeight = 1,
            MaximumParallelism = 1
        };
        var matcher = new ShapeMatcher();
        var result = matcher.Search(scene, model, options);
        var nearest = result.Matches.OrderBy(match => Distance(match.Pose, expected)).FirstOrDefault();
        var positionError = nearest is null ? double.PositiveInfinity : Distance(nearest.Pose, expected);
        var message = $"{name}: matches={result.Matches.Length} position-error={positionError}";
        var passed = nearest is not null && Distance(nearest.Pose, expected) <= .75 &&
            RobustGeometricScene.AngleErrorDegrees(nearest.Pose, expected) <= 1.5 &&
            Math.Abs(nearest.Pose.Scale - expected.Scale) <= .025 && result.Matches.Length == 1;
        if (!passed)
        {
            var direct = matcher.RefineCandidate(scene, model, expected, 1, options);
            var gradient = GradientImage.Create(scene, 8, default);
            var trueScore = ContinuousVerifier.Score(gradient, model, expected);
            var trueClutter = GeometricQualityEvaluator.EvaluateClutter(gradient, model, expected, 3, default);
            message += $" true-direction={trueScore.Score:F6} true-clutter={trueClutter.Ratio:F6} direct={direct.Matches.Length}";
            message += $" diagnostics={result.Diagnostics}";
            failures.Add(message);
        }
        output.WriteLine(message);
    }

    private static double Distance(MatchPose left, MatchPose right)
        => Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));

    private static bool SameTarget(MatchPose actual, MatchPose expected)
        => Distance(actual, expected) <= .75 && RobustGeometricScene.AngleErrorDegrees(actual, expected) <= 1.5 &&
            Math.Abs(actual.Scale - expected.Scale) <= .025;

    private static GrayImage CreateDistractor(int size, MatchPose pose)
    {
        var pixels = new byte[size * size];
        var cosine = Math.Cos(pose.Angle);
        var sine = Math.Sin(pose.Angle);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x + .5 - pose.X;
                var dy = y + .5 - pose.Y;
                var localX = (cosine * dx) + (sine * dy);
                var localY = (-sine * dx) + (cosine * dy);
                var frame = Math.Abs(localX) <= 20 && Math.Abs(localY) <= 22 &&
                    (Math.Abs(localX) >= 10 || Math.Abs(localY) >= 14);
                pixels[(y * size) + x] = frame ? (byte)225 : (byte)25;
            }
        }
        return new(pixels, size, size, size);
    }

    private static GrayImage CreateMixedContrastScene(bool includeDetail)
    {
        const int Size = 256;
        var pixels = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var strong = x is >= 16 and <= 68 && y is >= 24 and <= 232;
                var detail = ContainsEStroke(x, y);
                pixels[(y * Size) + x] = strong ? (byte)255 : includeDetail && detail ? (byte)35 : (byte)0;
            }
        }
        return new(pixels, Size, Size, Size);
    }

    private static GrayImage CreateLocalContrastScene(byte detailIntensity, bool brightPatch = false)
    {
        const int Size = 256;
        var pixels = Enumerable.Repeat((byte)20, Size * Size).ToArray();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var strong = x is >= 16 and <= 68 && y is >= 24 and <= 232;
                var detail = ContainsEStroke(x, y);
                var patch = brightPatch && x is >= 184 and <= 231 && y is >= 233 and <= 244;
                pixels[(y * Size) + x] = patch ? (byte)250 : strong ? (byte)110 : detail ? detailIntensity : (byte)20;
            }
        }
        return new(pixels, Size, Size, Size);
    }

    private static GrayImage CreateBoundaryContrastScene(int? barLeft, byte barIntensity, byte detailIntensity)
    {
        const int Size = 256;
        var pixels = Enumerable.Repeat((byte)20, Size * Size).ToArray();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var strong = barLeft is { } left && x >= left && x <= 45;
                var detail = ContainsEStroke(x, y);
                pixels[(y * Size) + x] = strong ? barIntensity : detail ? detailIntensity : (byte)20;
            }
        }
        return new(pixels, Size, Size, Size);
    }

    private static bool ContainsEStroke(int x, int y)
        => (x is >= 118 and <= 134 && y is >= 40 and <= 216) ||
            (x is >= 118 and <= 232 && (y is >= 40 and <= 56 || y is >= 120 and <= 136 || y is >= 200 and <= 216));

    private static GrayImage CreateBlockNoiseTile(int centerX, int centerY, double degrees, double scale, bool extraContour = false)
    {
        const int Size = 640;
        var pixels = new byte[Size * Size];
        var cosine = Math.Cos(degrees * Math.PI / 180);
        var sine = Math.Sin(degrees * Math.PI / 180);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var sourceX = centerX - 320 + x;
                var sourceY = centerY - 320 + y;
                var dx = x + .5 - 320;
                var dy = y + .5 - 320;
                var localX = ((cosine * dx) + (sine * dy)) / scale;
                var localY = ((-sine * dx) + (cosine * dy)) / scale;
                var inside = (localX >= -180 && localX <= -100 && localY >= -190 && localY <= 180) ||
                    (localX >= -100 && localX <= 170 && localY >= 100 && localY <= 180) ||
                    (localX >= -100 && localX <= 100 && localY >= -190 && localY <= -120) ||
                    (((localX - 100) * (localX - 100)) + (localY * localY) <= 45 * 45);
                inside |= extraContour && localX is >= -65 and <= -10 && localY is >= -85 and <= 60;
                var hash = unchecked(((uint)(sourceX / 6) * 73856093) ^ ((uint)(sourceY / 6) * 19349663));
                var noise = (int)(hash % 41) - 20;
                pixels[(y * Size) + x] = (byte)((inside ? 212 : 47) + noise);
            }
        }
        return new(pixels, Size, Size, Size);
    }

    private static GrayImage CreateWire(int width, int height, MatchPose pose, double thickness)
    {
        var pixels = new byte[width * height];
        var cosine = Math.Cos(pose.Angle);
        var sine = Math.Sin(pose.Angle);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var samples = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var dx = x + ((sx + .5) / 4) - pose.X;
                        var dy = y + ((sy + .5) / 4) - pose.Y;
                        var localX = (cosine * dx) + (sine * dy);
                        var localY = (-sine * dx) + (cosine * dy);
                        var inside = (Math.Abs(localX + 27) <= thickness / 2 && Math.Abs(localY) <= 25) ||
                            (Math.Abs(localY - 25) <= thickness / 2 && localX is >= -27 and <= 29) ||
                            (Math.Abs(localY + 25) <= thickness / 2 && localX is >= -27 and <= 9) ||
                            (Math.Abs(localX - 13) <= thickness / 2 && localY is >= -10 and <= 11);
                        if (inside)
                        {
                            samples++;
                        }
                    }
                }
                pixels[(y * width) + x] = (byte)(25 + (200 * samples / 16));
            }
        }
        return new(pixels, width, height, width);
    }
}
