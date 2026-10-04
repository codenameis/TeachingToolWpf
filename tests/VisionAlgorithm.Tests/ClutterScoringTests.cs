using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class ClutterScoringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddedDiagonalLowersFinalScoreWhilePreservingDirectionalEvidence(bool geometry)
    {
        var model = Train(geometry);
        var clean = Refine(CreatePin(0), model, geometry, 1);
        var legacy = Refine(CreatePin(0, extra: true), model, geometry, 0);
        var penalized = Refine(CreatePin(0, extra: true), model, geometry, 1);

        Assert.InRange(clean.Score, .95, 1);
        Assert.InRange(legacy.Score, .95, 1);
        Assert.Equal(legacy.Score, penalized.DirectionScore, 12);
        Assert.True(penalized.Score < clean.Score - .15);
        Assert.InRange(penalized.ClutterRatio!.Value, .15, .6);
        Assert.Equal(penalized.DirectionScore * (1 - penalized.ClutterRatio.Value), penalized.Score, 12);
        Assert.Equal(geometry, penalized.Geometry is not null);
        Assert.Equal(0, clean.ClutterRatio!.Value, 12);
    }

    [Fact]
    public void PartialWeightIsExplicitAndZeroKeepsExistingScore()
    {
        var model = Train(false);
        var scene = CreatePin(0, extra: true);
        var legacy = Refine(scene, model, false, 0);
        var weighted = Refine(scene, model, false, .25);

        Assert.Null(legacy.ClutterRatio);
        Assert.Equal(legacy.Score, legacy.DirectionScore);
        Assert.Equal(legacy.Score, weighted.DirectionScore, 12);
        Assert.Equal(weighted.DirectionScore * (1 - (.25 * weighted.ClutterRatio!.Value)), weighted.Score, 12);
    }

    [Fact]
    public void MissingPinContourStillLowersDirectionalAndFinalScores()
    {
        var model = Train(false);
        var clean = Refine(CreatePin(0), model, false, 1);
        var missing = Refine(CreatePin(0, missing: true), model, false, 1);

        Assert.True(missing.DirectionScore < clean.DirectionScore - .1);
        Assert.True(missing.Score <= missing.DirectionScore);
        Assert.True(missing.Score < clean.Score - .1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void AdditionalEdgesOutsideActualMaskDoNotReduceScore(double angleDegrees)
    {
        var angle = angleDegrees * Math.PI / 180;
        var mask = new byte[128 * 128];
        for (var y = 0; y < 128; y++)
        {
            for (var x = 43; x < 128; x++)
            {
                mask[(y * 128) + x] = 255;
            }
        }
        var model = Train(false, mask);
        Array.Clear(mask);
        var clean = Refine(CreatePin(angle), model, false, 1, angle);
        var outside = Refine(CreatePin(angle, extra: true), model, false, 1, angle);

        Assert.Equal(clean.DirectionScore, outside.DirectionScore, 8);
        Assert.Equal(clean.Score, outside.Score, 8);
        Assert.Equal(clean.ClutterRatio, outside.ClutterRatio);
        Assert.InRange(outside.ClutterRatio!.Value, 0, .01);
    }

    [Fact]
    public void NonzeroTrainingAngleAndRotatedSceneApplyClutterTransformOnce()
    {
        const double TrainingAngle = Math.PI / 2;
        const double SceneAngle = Math.PI;
        var model = ShapeTrainer.Train(CreatePin(TrainingAngle), new(ReadOnlyMemory<byte>.Empty, new(64, 64), TrainingAngle),
            Training(false));
        var clean = Refine(CreatePin(SceneAngle), model, false, 1, SceneAngle);
        var extra = Refine(CreatePin(SceneAngle, extra: true), model, false, 1, SceneAngle);

        Assert.InRange(clean.Score, .95, 1);
        Assert.InRange(extra.DirectionScore, .95, 1);
        Assert.True(extra.Score < clean.Score - .15);
        Assert.InRange(extra.ClutterRatio!.Value, .15, .6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MinimumScoreUsesFinalScoreAfterClutterPenalty(bool geometry)
    {
        var model = Train(geometry);
        var scene = CreatePin(0, extra: true);
        var matcher = new ShapeMatcher();
        var directional = Assert.Single(matcher.RefineCandidate(scene, model, new(64, 64, 0), 1,
            Options(geometry, 0) with
            {
                MinimumScore = .9
            }).Matches);
        Assert.True(directional.Score > .9);
        Assert.Empty(matcher.RefineCandidate(scene, model, new(64, 64, 0), 1,
            Options(geometry, 1) with
            {
                MinimumScore = .9
            }).Matches);
    }

    [Fact]
    public void FinalScoreControlsOrderingAndOutputLimitBeforeSuppression()
    {
        var model = Train(false);
        var clean = CreatePin(0);
        var dirty = CreatePin(0, extra: true);
        var pixels = new byte[256 * 128];
        for (var row = 0; row < 128; row++)
        {
            clean.Pixels.Span.Slice(row * 128, 128).CopyTo(pixels.AsSpan(row * 256));
            dirty.Pixels.Span.Slice(row * 128, 128).CopyTo(pixels.AsSpan((row * 256) + 128));
        }
        var image = PreparedSearchImage.Create(new(pixels, 256, 128, 256), 8, model.Levels.Length);
        var cleanPose = new MatchPose(64, 64, 0);
        var dirtyPose = new MatchPose(192, 64, 0);
        // 후보 순서와 방향 점수에서 추가선 후보가 앞서도 최종 점수로 다시 정렬한다.
        var batch = new CandidateBatch(
            [new(dirtyPose, 1, 0, new(1, 1, 4)), new(cleanPose, .96, 0, new(.96, 1, 4))], false, 2);
        var options = Options(false, 1) with
        {
            MaximumMatches = 1
        };
        var result = ShapeMatcher.CompleteCandidates(image, model, options, batch, 0, default);

        Assert.Equal(cleanPose, Assert.Single(result.Matches).Pose);
        Assert.True(result.Diagnostics.MatchLimitExceeded);
        var both = ShapeMatcher.CompleteCandidates(image, model, options with
        {
            MaximumMatches = 2
        }, batch, 0, default);
        Assert.Equal(cleanPose, both.Matches[0].Pose);
        Assert.Equal(dirtyPose, both.Matches[1].Pose);
        // 겹치는 후보 중에서도 방향 점수가 더 높은 추가선 후보를 우선하지 않는다.
        var overlapping = both.Matches[1] with
        {
            Pose = cleanPose
        };
        Assert.Same(both.Matches[0], Assert.Single(MatchSuppression.Apply(model, [overlapping, both.Matches[0]], .3, 2)));
    }

    [Fact]
    public void FullSearchReturnsPenalizedScoreAndRejectsItAtFinalThreshold()
    {
        var model = Train(false);
        var scene = CreatePin(0, extra: true);
        var matcher = new ShapeMatcher();
        var options = Options(false, 1) with
        {
            MinimumScore = .7
        };
        var match = Assert.Single(matcher.Search(scene, model, options).Matches);

        Assert.InRange(match.DirectionScore, .95, 1);
        Assert.InRange(match.Score, .7, .85);
        Assert.Empty(matcher.Search(scene, model, options with
        {
            MinimumScore = .9
        }).Matches);
    }

    [Fact]
    public void PublicRefinementClearsPreviousClutterAndKeepsDirectionSeparate()
    {
        var model = Train(false);
        var image = PreparedSearchImage.Create(CreatePin(0), 8, model.Levels.Length);
        var initial = new MatchPose(65.5, 65.2, 0);
        var match = new ShapeMatch(initial, 1, .4, 1, 4,
            new(RefinementStatus.Disabled, initial, double.NaN, double.NaN, 0, 0, true))
        {
            DirectionScore = 1,
            ClutterRatio = .6
        };
        var refined = PointToLineRefiner.Refine(image, model, match, Options(false, 0) with
        {
            MinimumScore = 1,
            MinimumSupport = 1
        });

        Assert.Equal(RefinementStatus.Rejected, refined.Refinement.Status);
        Assert.Null(refined.ClutterRatio);
        Assert.Equal(1, refined.DirectionScore);
        Assert.Equal(refined.DirectionScore, refined.Score);
    }

    [Fact]
    public void PaperContourFootprintIsSeparateFromGeometryAndCoarseViews()
    {
        var model = Train(false);
        Assert.NotNull(model.ContourModel);
        Assert.Null(model.GeometryModel);
        Assert.Same(model.ContourModel, model.AtPyramidLevel(0).ContourModel);
        Assert.Null(model.AtPyramidLevel(1).ContourModel);
        Assert.True(model.EstimatedBytes > model.AtPyramidLevel(1).EstimatedBytes);
    }

    [Theory]
    [InlineData(-.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidClutterWeightIsRejected(double weight)
        => Assert.Throws<ArgumentOutOfRangeException>(() => (new SearchOptions { ClutterWeight = weight }).Validate());

    [Fact]
    public void ClutterScoringRequiresVerificationAndOriginalContourModel()
    {
        var image = CreatePin(0);
        var model = Train(false);
        var withoutContour = ShapeTrainer.Train(image, new(ReadOnlyMemory<byte>.Empty, new(64, 64)),
            Training(false) with
            {
                BuildClutterModel = false
            });
        var matcher = new ShapeMatcher();

        Assert.Throws<ArgumentException>(() => matcher.Search(image, withoutContour, Options(false, 1)));
        Assert.Throws<ArgumentException>(() => matcher.Search(image, model.AtPyramidLevel(1), Options(false, 1)));
        Assert.Throws<ArgumentException>(() => matcher.Search(image, model, Options(false, 1) with { Verify = false }));
    }

    private static ShapeModel Train(bool geometry, byte[]? mask = null)
        => ShapeTrainer.Train(CreatePin(0), new(mask ?? ReadOnlyMemory<byte>.Empty, new(64, 64)), Training(geometry));

    private static TrainingOptions Training(bool geometry)
        => new()
        {
            BuildGeometricModel = geometry,
            BuildClutterModel = true,
            PyramidLevels = 2,
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 90,
            AngleStepDegrees = 45
        };

    private static SearchOptions Options(bool geometry, double weight)
        => new()
        {
            EvaluateGeometry = geometry,
            ClutterWeight = weight,
            Refine = false,
            MinimumScore = .1,
            MinimumSupport = .1
        };

    private static ShapeMatch Refine(GrayImage image, ShapeModel model, bool geometry, double weight, double angle = 0)
        => Assert.Single(new ShapeMatcher().RefineCandidate(image, model, new(64, 64, angle), 1, Options(geometry, weight)).Matches);

    private static GrayImage CreatePin(double angle, bool extra = false, bool missing = false)
    {
        const int Size = 128;
        var pixels = new byte[Size * Size];
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = x + .5 - 64;
                var dy = y + .5 - 64;
                var u = (cosine * dx) + (sine * dy);
                var v = (-sine * dx) + (cosine * dy);
                var pin = (u >= -12 && u <= 12 && v >= -32 && v <= 32) ||
                    (u >= 12 && u <= 25 && v >= -32 && v <= -22);
                if (missing && v < -12)
                {
                    pin = false;
                }
                var fraction = Math.Clamp((((u + 48) * 21) + ((v + 24) * 48)) / (double)((21 * 21) + (48 * 48)), 0, 1);
                var ex = u + 48 - (fraction * 21);
                var ey = v + 24 - (fraction * 48);
                var trace = extra && ((ex * ex) + (ey * ey)) <= 2.5 * 2.5;
                pixels[(y * Size) + x] = pin || trace ? (byte)225 : (byte)25;
            }
        }
        return new(pixels, Size, Size, Size);
    }
}
