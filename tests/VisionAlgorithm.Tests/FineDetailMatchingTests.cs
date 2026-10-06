using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class FineDetailMatchingTests
{
    [Fact]
    public void ValidLastPixelIsPreservedWhenAnImageIsPlacedInsideALargerCanvas()
    {
        var pixels = new byte[9 * 9];
        new Random(481).NextBytes(pixels);
        var detail = FineDetailModel.Create(new(pixels, 9, 9, 9), new(ReadOnlyMemory<byte>.Empty, new(4.5, 4.5)), default)!;
        var canvas = new byte[15 * 15];
        for (var row = 0; row < 9; row++)
        {
            pixels.AsSpan(row * 9, 9).CopyTo(canvas.AsSpan(((row + 3) * 15) + 3));
        }
        Assert.Equal(1, detail.Score(new(canvas, 15, 15, 15), new(7.5, 7.5, 0), ContrastPolarity.Preserve), 10);
    }

    [Fact]
    public void RawDetailSeparatesOneStrokeWhileIgnoringUniformLighting()
    {
        var image = CreateText();
        var region = Region();
        var detail = FineDetailModel.Create(image, region, default)!;
        var pose = new MatchPose(43.5, 37, 0);
        Assert.Equal(1, detail.Score(image, pose, ContrastPolarity.Preserve), 10);
        var lighting = image.Pixels.ToArray().Select(value => (byte)((value / 2) + 30)).ToArray();
        Assert.InRange(detail.Score(new(lighting, 96, 80, 96), pose, ContrastPolarity.Preserve), .999, 1);
        var changed = CreateText(removeStroke: true);
        // 획 세 픽셀의 누락을 식별하되 이를 임의의 제품 불량 임계값으로 해석하지 않는다.
        Assert.InRange(detail.Score(changed, pose, ContrastPolarity.Preserve), 0, .99);
        var inverted = image.Pixels.ToArray().Select(value => (byte)(255 - value)).ToArray();
        Assert.Equal(0, detail.Score(new(inverted, 96, 80, 96), pose, ContrastPolarity.Preserve));
        Assert.InRange(detail.Score(new(inverted, 96, 80, 96), pose, ContrastPolarity.IgnoreLocal), .999, 1);
    }

    [Fact]
    public void ActualMaskExcludesOutsidePixelsAndOwnsTrainingSamples()
    {
        var image = CreateText();
        var mask = Region().Mask.ToArray();
        var region = new TrainingRegion(mask, new(43.5, 37));
        var detail = FineDetailModel.Create(image, region, default)!;
        var altered = image.Pixels.ToArray();
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
            {
                altered[i] = 255;
            }
        }
        Array.Clear(mask);
        Assert.Equal(1, detail.Score(new(altered, 96, 80, 96), new(43.5, 37, 0), ContrastPolarity.Preserve), 10);
    }

    [Fact]
    public void OppositeCandidateRecoversCorrectTextAndRespectsAllowedAngles()
    {
        var image = CreateText();
        var model = Train(image);
        Assert.NotNull(model.FineDetail);
        var recovered = model.FineDetail!.Optimize(image, model, new(43.5, 37, Math.PI), new(), default);
        Assert.InRange(Math.Abs(recovered.Pose.Angle), 0, .001);
        Assert.InRange(recovered.Score, .999, 1);
        var fixedAngle = Train(image, 180, 180);
        var constrained = fixedAngle.FineDetail!.Optimize(image, fixedAngle, new(43.5, 37, Math.PI), new(), default);
        Assert.Equal(Math.PI, constrained.Pose.Angle);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => model.FineDetail.Optimize(image, model, new(43.5, 37, 0), new(), cancelled.Token));
    }

    [Fact]
    public void FinalDetailScoreUsesReturnedPoseAndZeroWeightKeepsOldContract()
    {
        var image = CreateText();
        var model = Train(image);
        var matcher = new ShapeMatcher();
        var options = new SearchOptions { ClutterWeight = 1, EvaluateGeometry = false, MinimumScore = .5, MinimumSupport = .2 };
        var result = Assert.Single(matcher.RefineCandidate(image, model, new(43.5, 37, 0), 1, options).Matches);
        Assert.Equal(model.FineDetail!.Score(image, result.Pose, model.Options.Polarity), result.DetailConsistency!.Value, 10);
        Assert.Equal(result.DirectionScore * (1 - result.ClutterPenaltyRatio!.Value) * result.DetailConsistency.Value, result.Score, 10);
        var unchanged = Assert.Single(matcher.RefineCandidate(image, model, new(43.5, 37, 0), 1, options with
        {
            ClutterWeight = 0
        }).Matches);
        Assert.Null(unchanged.DetailConsistency);
        Assert.Equal(unchanged.DirectionScore, unchanged.Score);
        var prepared = PreparedSearchImage.Create(image, model.Options.MinimumGradient, 1, retainOriginal: true);
        var saved = prepared.Original!.Pixels.ToArray();
        Assert.Equal(image.Pixels.ToArray(), saved);
    }

    [Fact]
    public void GeometricModelNeverEnablesPaperDetailValidation()
    {
        var model = ShapeTrainer.Train(CreateText(), Region(), new()
        {
            PyramidLevels = 1,
            UseFineDetailValidation = true,
            BuildGeometricModel = true
        });
        Assert.Null(model.FineDetail);
    }

    [Fact]
    public void DetailCoordinatesPreserveRotatedTrainingAndScale()
    {
        var image = CreateText();
        var region = Region() with
        {
            ReferenceAngle = .37
        };
        var detail = FineDetailModel.Create(image, region, default)!;
        Assert.Equal(1, detail.Score(image, new(43.5, 37, .37), ContrastPolarity.Preserve), 10);
        var rotated = new byte[96 * 80];
        var scaled = new byte[192 * 160];
        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 96; x++)
            {
                var value = image.Pixels.Span[(y * 96) + x];
                rotated[(x * 80) + 79 - y] = value;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        scaled[(((y * 2) + dy) * 192) + (x * 2) + dx] = value;
                    }
                }
            }
        }
        Assert.Equal(1, detail.Score(new(rotated, 80, 96, 80), new(43, 43.5, .37 + (Math.PI / 2)), ContrastPolarity.Preserve), 10);
        Assert.Equal(1, detail.Score(new(scaled, 192, 160, 192), new(87, 74, .37, 2), ContrastPolarity.Preserve), 10);
    }

    private static ShapeModel Train(GrayImage image, double minimumAngle = -180, double maximumAngle = 180)
        => ShapeTrainer.Train(image, Region(), new()
        {
            PyramidLevels = 1,
            UseFineDetailValidation = true,
            BuildGeometricModel = false,
            BuildClutterModel = true,
            MinimumAngleDegrees = minimumAngle,
            MaximumAngleDegrees = maximumAngle,
            AngleStepDegrees = 10
        });

    private static TrainingRegion Region()
    {
        var mask = new byte[96 * 80];
        for (var y = 31; y < 43; y++)
        {
            for (var x = 33; x < 54; x++)
            {
                mask[(y * 96) + x] = 255;
            }
        }
        return new(mask, new(43.5, 37));
    }

    private static GrayImage CreateText(bool removeStroke = false)
    {
        string[] rows = ["  #   ###  #   #", " ##   # #  #   #", "  #   # #  #   #", "  #   ###   # # ", "  #   # #   # # ", "  #   # #   # # ", " ###  ###    #  "];
        var pixels = Enumerable.Repeat((byte)20, 96 * 80).ToArray();
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (rows[y][x] == '#' && !(removeStroke && y == 3 && x is >= 6 and <= 8))
                {
                    pixels[((y + 33) * 96) + x + 35] = 220;
                }
            }
        }
        return new(pixels, 96, 80, 96);
    }
}
