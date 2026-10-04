using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class GeometricQualityTests
{
    [Fact]
    public void MaskHolesAndOriginalMaskMutationDoNotAffectOwnedGeometry()
    {
        var training = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var mask = Enumerable.Repeat((byte)255, 96 * 96).ToArray();
        for (var y = 24; y < 45; y++)
        {
            for (var x = 56; x < 78; x++)
            {
                mask[(y * 96) + x] = 0;
            }
        }
        var model = ShapeTrainer.Train(training, new(mask, new(48, 48)), new());
        var clean = SyntheticScene.Create(160, 150, new MatchPose(80, 75, 0));
        var pixels = clean.Pixels.ToArray();
        for (var y = 58; y < 65; y++)
        {
            for (var x = 94; x < 102; x++)
            {
                pixels[(y * 160) + x] = 225;
            }
        }
        var cleanQuality = Evaluate(clean, model, new(80, 75, 0));
        var maskedQuality = Evaluate(new(pixels, 160, 150, 160), model, new(80, 75, 0));
        Array.Clear(mask);
        var ownedQuality = Evaluate(new(pixels, 160, 150, 160), model, new(80, 75, 0));

        Assert.Equal(maskedQuality, ownedQuality);
        Assert.Equal(cleanQuality.SceneEdges, maskedQuality.SceneEdges);
        Assert.InRange(maskedQuality.ClutterRatio, 0, 0.02);
        Assert.True(model.EstimatedBytes > mask.Length);
    }

    [Fact]
    public void OcclusionLowersFixedDenominatorCoverageAndExplicitGateRejectsIt()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(160, 150, new MatchPose(80, 75, 0));
        var pixels = scene.Pixels.ToArray();
        for (var y = 0; y < 65; y++)
        {
            Array.Fill(pixels, (byte)25, y * 160, 160);
        }
        var partial = new GrayImage(pixels, 160, 150, 160);
        var fullQuality = Evaluate(scene, model, new(80, 75, 0));
        var partialQuality = Evaluate(partial, model, new(80, 75, 0));
        Assert.Equal(fullQuality.ModelFeatures, partialQuality.ModelFeatures);
        Assert.True(partialQuality.Coverage < fullQuality.Coverage - 0.1);

        var options = new SearchOptions { MinimumScore = 0.55, MinimumSupport = 0.5, CandidateThreshold = 0.5 };
        var matcher = new ShapeMatcher();
        Assert.Single(matcher.Search(partial, model, options).Matches);
        Assert.Empty(matcher.Search(partial, model, options with
        {
            MinimumCoverage = 0.95
        }).Matches);
    }

    [Fact]
    public void DenseContourAvoidsTreatingUnselectedTrainingEdgesAsClutter()
    {
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = ShapeTrainer.Train(image, new(ReadOnlyMemory<byte>.Empty, new(48, 48)),
            new()
            {
                MaximumFeatures = 12
            });
        var quality = Evaluate(image, model, new(48, 48, 0));

        Assert.Equal(12, quality.ModelFeatures);
        Assert.True(quality.SceneEdges > quality.ModelFeatures * 10);
        Assert.Equal(1, quality.Coverage);
        Assert.Equal(0, quality.ClutterRatio);
    }

    [Fact]
    public void RotatedTrainingAngleIsAppliedOnceAndPaddingIsNotASceneEdge()
    {
        const double TrainingAngle = 24 * Math.PI / 180;
        var training = SyntheticScene.Create(128, 128, new MatchPose(64, 64, TrainingAngle));
        var model = ShapeTrainer.Train(training, new(ReadOnlyMemory<byte>.Empty, new(64, 64), TrainingAngle),
            new()
            {
                MinimumScale = 0.9,
                MaximumScale = 1.2
            });
        var pose = new MatchPose(83.2, 81.4, 29 * Math.PI / 180, 1.13);
        var scene = SyntheticScene.Create(161, 157, pose);
        var padded = Enumerable.Repeat((byte)255, 173 * 157).ToArray();
        for (var y = 0; y < scene.Height; y++)
        {
            scene.Pixels.Span.Slice(y * scene.Stride, scene.Width).CopyTo(padded.AsSpan(y * 173));
        }
        var quality = Evaluate(scene, model, pose);
        Assert.Equal(quality, Evaluate(new(padded, scene.Width, scene.Height, 173), model, pose));
        Assert.InRange(quality.Coverage, 0.95, 1);
        Assert.InRange(quality.ClutterRatio, 0, 0.08);
    }

    [Fact]
    public void EmptyAndOutOfImageEvidenceDoesNotImproveCoverage()
    {
        var model = MatchingTests.Train();
        var blank = new GrayImage(new byte[96 * 96], 96, 96, 96);
        var empty = Evaluate(blank, model, new(48, 48, 0));
        Assert.Equal(0, empty.Coverage);
        Assert.Equal(0, empty.SceneEdges);
        Assert.Equal(0, empty.ClutterRatio);
        Assert.Equal(double.PositiveInfinity, empty.FitErrorPixels);

        var clipped = SyntheticScene.Create(96, 96, new MatchPose(4, 4, 0));
        var quality = Evaluate(clipped, model, new(4, 4, 0));
        Assert.Equal(model.Levels[0].Features.Length, quality.ModelFeatures);
        Assert.InRange(quality.Coverage, 0, 0.65);
    }

    [Fact]
    public void FitErrorGateRejectsAnOtherwiseAcceptedMatch()
    {
        var image = SyntheticScene.Create(160, 150, new MatchPose(80.3, 75.7, 0.13, 1.07));
        var model = MatchingTests.Train();
        var matcher = new ShapeMatcher();
        var match = Assert.Single(matcher.Search(image, model, new()).Matches);
        Assert.True(match.Geometry!.FitErrorPixels > 0.01);
        Assert.Empty(matcher.Search(image, model, new()
        {
            MaximumFitErrorPixels = 0.01
        }).Matches);
    }

    [Fact]
    public void CropTranslationLeavesQualityInOriginalPixelUnits()
    {
        var model = MatchingTests.Train();
        var image = SyntheticScene.Create(160, 150, new MatchPose(80.3, 75.7, 0.13, 1.07));
        var pixels = new byte[120 * 110];
        for (var y = 0; y < 110; y++)
        {
            image.Pixels.Span.Slice(((y + 20) * 160) + 20, 120).CopyTo(pixels.AsSpan(y * 120));
        }
        var full = Evaluate(image, model, new(80.3, 75.7, 0.13, 1.07));
        var crop = Evaluate(new(pixels, 120, 110, 120), model, new(60.3, 55.7, 0.13, 1.07));
        Assert.Equal(full.Coverage, crop.Coverage);
        Assert.Equal(full.FitErrorPixels, crop.FitErrorPixels, 10);
        Assert.Equal(full.ClutterRatio, crop.ClutterRatio);
        Assert.Equal(full.SceneEdges, crop.SceneEdges);
    }

    [Fact]
    public void PolarityAffectsGeometricCorrespondenceAndClutter()
    {
        var scene = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var inverted = scene.Pixels.ToArray();
        for (var index = 0; index < inverted.Length; index++)
        {
            inverted[index] = (byte)(255 - inverted[index]);
        }
        var input = new GrayImage(inverted, 96, 96, 96);
        var preserving = Evaluate(input, MatchingTests.Train(), new(48, 48, 0));
        var ignoring = Evaluate(input, MatchingTests.Train(ContrastPolarity.IgnoreLocal), new(48, 48, 0));
        Assert.Equal(0, preserving.Coverage);
        Assert.Equal(1, preserving.ClutterRatio);
        Assert.Equal(1, ignoring.Coverage);
        Assert.Equal(0, ignoring.ClutterRatio);
    }

    [Fact]
    public void DisabledEvaluationAndCoarseModelsDoNotClaimGeometry()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var matcher = new ShapeMatcher();
        Assert.All(matcher.Search(scene, model, new()
        {
            EvaluateGeometry = false
        }).Matches,
            match => Assert.Null(match.Geometry));
        Assert.All(matcher.Search(scene, model, new()
        {
            Verify = false
        }).Matches,
            match => Assert.Null(match.Geometry));
        var coarse = model.AtPyramidLevel(model.Levels.Length - 1);
        Assert.Null(coarse.GeometryModel);
        Assert.True(coarse.EstimatedBytes < model.EstimatedBytes);
        Assert.Same(model.GeometryModel, model.AtPyramidLevel(0).GeometryModel);
    }

    [Theory]
    [InlineData(0.8, double.PositiveInfinity, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(0, double.PositiveInfinity, 0.8)]
    public void CoarseModelCannotSilentlyBypassAnEnabledGeometricGate(double coverage, double fit, double clutter)
    {
        var coarse = MatchingTests.Train().AtPyramidLevel(1);
        var image = GradientImage.Reduce(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)));
        var options = new SearchOptions
        {
            MinimumCoverage = coverage,
            MaximumFitErrorPixels = fit,
            MaximumClutterRatio = clutter
        };
        var matcher = new ShapeMatcher();
        Assert.Throws<ArgumentException>(() => matcher.Search(image, coarse, options));
        Assert.Throws<ArgumentException>(() => matcher.RefineCandidate(image, coarse, new(24, 24, 0), 1, options));
        var result = matcher.Search(image, coarse, options with
        {
            EvaluateGeometry = false
        });
        Assert.All(result.Matches, match => Assert.Null(match.Geometry));
    }

    [Fact]
    public void GeometryCancellationAndDenseContourBudgetAreExplicit()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var gradient = GradientImage.Create(scene, 8, default);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => GeometricQualityEvaluator.Evaluate(gradient, model,
            new(48, 48, 0), 3, source.Token));
        var contour = Enumerable.Repeat(new EdgePoint(1.5, 1.5, 1, 0, 10),
            GeometricQualityModel.MaximumContourPoints + 1).ToList();
        Assert.Throws<ArgumentException>(() => new GeometricQualityModel(3, 3, ReadOnlyMemory<byte>.Empty, contour, default));
        Assert.Throws<ArgumentException>(() => new GeometricQualityModel(ShapeTrainer.MaximumPixels + 1, 1,
            ReadOnlyMemory<byte>.Empty, [], default));
        Assert.Throws<ArgumentException>(() => new GeometricQualityModel(3, 3, new byte[8], [], default));
    }

    [Fact]
    public void TrainingRejectsDenseContourOverflowWithoutThinningIt()
    {
        const int Size = 1024;
        var pixels = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                pixels[(y * Size) + x] = (byte)(x / 3 % 2 == 0 ? 25 : 225);
            }
        }
        var error = Assert.Throws<ArgumentException>(() => ShapeTrainer.Train(new(pixels, Size, Size, Size),
            new(ReadOnlyMemory<byte>.Empty, new(Size / 2, Size / 2)), new()
            {
                PyramidLevels = 1
            }));
        Assert.Contains("262,144", error.Message);
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(9, 0, 1, 1)]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(3, -0.1, 1, 1)]
    [InlineData(3, 1.1, 1, 1)]
    [InlineData(3, 0, double.NaN, 1)]
    [InlineData(3, 0, double.NegativeInfinity, 1)]
    [InlineData(3, 0, 0, 1)]
    [InlineData(3, 0, 1, -0.1)]
    [InlineData(3, 0, 1, 1.1)]
    public void InvalidGeometricPolicyIsRejected(double tolerance, double coverage, double fit, double clutter)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchOptions
        {
            GeometricTolerancePixels = tolerance,
            MinimumCoverage = coverage,
            MaximumFitErrorPixels = fit,
            MaximumClutterRatio = clutter
        }.Validate());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(17.3, 1.13)]
    public void MatchingContourHasHighCoverageLowFitErrorAndLittleClutter(double angle, double scale)
    {
        var expected = new MatchPose(80.3, 75.7, angle * Math.PI / 180, scale);
        var image = SyntheticScene.Create(160, 150, expected);
        var match = Assert.Single(new ShapeMatcher().Search(image, MatchingTests.Train(), new()).Matches);

        var quality = Assert.IsType<GeometricMatchQuality>(match.Geometry);
        Assert.InRange(quality.Coverage, 0.95, 1);
        Assert.InRange(quality.FitErrorPixels, 0, 0.9);
        Assert.InRange(quality.ClutterRatio, 0, 0.08);
        Assert.Equal(quality.Coverage, quality.MatchedFeatures / (double)quality.ModelFeatures);
        Assert.True(quality.SceneEdges > quality.ModelFeatures);
    }

    [Fact]
    public void ExtraEdgesAreMeasuredAgainstDenseContourAndCanBeRejected()
    {
        var model = MatchingTests.Train();
        var clean = SyntheticScene.Create(160, 150, new MatchPose(80, 75, 0));
        var pixels = clean.Pixels.ToArray();
        for (var y = 94; y < 110; y++)
        {
            for (var x = 102; x < 118; x++)
            {
                pixels[(y * 160) + x] = 225;
            }
        }
        var dirty = new GrayImage(pixels, 160, 150, 160);
        var matcher = new ShapeMatcher();
        var cleanMatch = Assert.Single(matcher.Search(clean, model, new()).Matches);
        var dirtyMatch = Assert.Single(matcher.Search(dirty, model, new()).Matches);
        var quality = Assert.IsType<GeometricMatchQuality>(dirtyMatch.Geometry);
        Assert.True(quality.ClutterRatio > cleanMatch.Geometry!.ClutterRatio + 0.08);
        Assert.Empty(matcher.Search(dirty, model, new()
        {
            MaximumClutterRatio = 0.05
        }).Matches);
    }

    [Fact]
    public void RejectedRefinementReportsResidualAndCorrespondencesAtReturnedPose()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(160, 150, new MatchPose(80, 75, 0));
        var prepared = PreparedSearchImage.Create(scene, 8, model.Levels.Length);
        var initial = new MatchPose(81.5, 76.2, 0);
        var candidate = new ShapeMatch(initial, 1, 1, 1, 4,
            new(RefinementStatus.Disabled, initial, double.NaN, double.NaN, 0, 0, true));
        var refined = PointToLineRefiner.Refine(prepared, model, candidate,
            new()
            {
                MinimumScore = 1,
                MinimumSupport = 1
            });

        Assert.Equal(RefinementStatus.Rejected, refined.Refinement.Status);
        Assert.Equal(initial, refined.Pose);
        var residuals = new List<double>();
        foreach (var feature in model.Levels[0].Features)
        {
            var x = refined.Pose.X + feature.X;
            var y = refined.Pose.Y + feature.Y;
            var edges = prepared.Gradients[0].EdgePoints.Where(edge =>
                ((feature.NormalX * edge.NormalX) + (feature.NormalY * edge.NormalY)) >= 0.8 &&
                (((edge.X - x) * (edge.X - x)) + ((edge.Y - y) * (edge.Y - y))) < 9);
            var closest = edges.OrderBy(edge => ((edge.X - x) * (edge.X - x)) + ((edge.Y - y) * (edge.Y - y))).ToArray();
            if (closest.Length > 0)
            {
                var edge = closest[0];
                residuals.Add((edge.NormalX * (x - edge.X)) + (edge.NormalY * (y - edge.Y)));
            }
        }
        Assert.Equal(residuals.Count, refined.Refinement.Correspondences);
        Assert.Equal(Math.Sqrt(residuals.Average(value => value * value)), refined.Refinement.FinalResidual, 9);
    }

    [Fact]
    public void PublicRefinementInvalidatesGeometryMeasuredBeforeTheCall()
    {
        var model = MatchingTests.Train();
        var scene = SyntheticScene.Create(160, 150, new MatchPose(80, 75, 0));
        var prepared = PreparedSearchImage.Create(scene, 8, model.Levels.Length);
        var initial = new MatchPose(81.5, 76.2, 0);
        var candidate = new ShapeMatch(initial, 1, 1, 1, 4,
            new(RefinementStatus.Disabled, initial, double.NaN, double.NaN, 0, 0, true))
        {
            Geometry = new(.1, 7, .9, 16, 160, 500)
        };

        var refined = PointToLineRefiner.Refine(prepared, model, candidate, new());

        Assert.NotEqual(initial, refined.Pose);
        Assert.Null(refined.Geometry);
    }

    private static GeometricMatchQuality Evaluate(GrayImage image, ShapeModel model, MatchPose pose)
        => GeometricQualityEvaluator.Evaluate(GradientImage.Create(image, 8, default), model, pose, 3, default);
}
