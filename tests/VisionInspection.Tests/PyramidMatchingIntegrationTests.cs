using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionGeometry;
using VisionImaging;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class PyramidMatchingIntegrationTests
{
    [Fact]
    public async Task ShapeExclusionRejectsMaskedTargetsAfterOriginalPyramidRefinement()
    {
        var scene = SyntheticScene.Create(800, 300, new MatchPose(160, 150, 0, 4), new MatchPose(620, 150, 0, 4));
        var loader = new Loader(scene);
        var image = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var service = new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper);
        var roi = new RoiGeometry(new(160, 150), 256, 256, 0);
        var trained = await service.TrainAsync(image, false, roi, new()
        {
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0,
            BuildClutterModel = true
        }, default);
        Assert.True(trained.Model.Levels.Length >= 3);
        var exclusion = new RoiExclusion(new(new(400, 150), 800, 300, 0), [new(MaskShape.Rectangle, new(0, 0, .4, 1))]);
        var options = new SearchOptions { ClutterWeight = 1 };
        var baseline = await service.SearchAsync(image, false, trained.Model, null, options, default);
        Assert.Equal(2, baseline.Matches.Length);
        var result = await service.SearchAsync(image, false, trained.Model, null, options, default, exclusion);
        Assert.False(result.Truncated);
        Assert.InRange(Assert.Single(result.Matches).Pose.X, 619, 621);
        Assert.Equal(baseline.Matches.Single(match => match.Pose.X > 400), result.Matches[0]);
        Assert.Equal(3, loader.Disposed);
    }

    [Fact]
    public async Task OriginalTrainingMasksExcludePixelsForBothMatchersAndDoNotModifySource()
    {
        var scene = SyntheticScene.Create(256, 128, new MatchPose(64, 64, 0), new MatchPose(192, 64, 0));
        var original = scene.Pixels.ToArray();
        var loader = new Loader(scene);
        var image = new ImageReference("fixture", "fixture", 256, 128);
        var roi = new RoiGeometry(new(64, 64), 64, 64, 0);
        var exclusion = new RoiExclusion(roi, [new(MaskShape.Rectangle, new(.5, .25, .9, .6))]);
        var shapeService = new ShapeMatchingService(loader, () => null);
        var shape = await shapeService.TrainAsync(image, false, roi, new()
        {
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0
        }, default, exclusion);
        Assert.All(shape.Model.Levels[0].Features, feature => Assert.False(exclusion.Contains(new(64 + feature.X, 64 + feature.Y))));
        var templateService = new TemplateMatchingService(loader, () => null);
        var template = await templateService.TrainAsync(image, false, roi, default, exclusion);
        var changed = original.ToArray();
        for (var y = 0; y < 128; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                if (exclusion.Contains(new(x + .5, y + .5)))
                {
                    changed[(y * 256) + x] = (byte)(255 - changed[(y * 256) + x]);
                }
            }
        }
        var changedService = new TemplateMatchingService(new Loader(new(changed, 256, 128, 256)), () => null);
        var matches = await changedService.SearchAsync(image, false, template.Model, null, new()
        {
            MinimumScore = .99
        }, default);
        Assert.Contains(matches.Matches, match => match.ReferencePoint.X == 64 && match.Score > .999);
        var searchMask = new RoiExclusion(new(new(128, 64), 256, 128, 0), [new(MaskShape.Ellipse, new(0, 0, .5, 1))]);
        var masked = await templateService.SearchAsync(image, false, template.Model, null, new()
        {
            MinimumScore = .99
        }, default, searchMask);
        Assert.Equal(192, Assert.Single(masked.Matches).ReferencePoint.X);
        Assert.Equal(original, scene.Pixels.ToArray());
        var full = new RoiExclusion(roi, [new(MaskShape.Rectangle, new(0, 0, 1, 1))]);
        await Assert.ThrowsAsync<ArgumentException>(() => templateService.TrainAsync(image, false, roi, default, full));
        await Assert.ThrowsAsync<ArgumentException>(() => shapeService.TrainAsync(image, false, roi, new(), default, full));
    }

    [Fact]
    public async Task PaperPipelinePreservesRotatedScaledTargets()
    {
        MatchPose[] expected = [new(100, 100, 0), new(256.3, 256.7, .23, 1.1), new(510.7, 385.2, -.21, .9)];
        var scene = SyntheticScene.Create(641, 517, expected);
        var loader = new Loader(scene);
        var image = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var paper = new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper);
        var classic = new ShapeMatchingService(loader, () => null);
        var trained = await paper.TrainAsync(image, false, new(new(100, 100), 96, 96, 0), new()
        {
            BuildClutterModel = true,
            MinimumAngleDegrees = -30,
            MaximumAngleDegrees = 30,
            AngleStepDegrees = 10,
            MinimumScale = .9,
            MaximumScale = 1.1,
            ScaleStep = .1
        }, default);
        var options = new SearchOptions { MaximumParallelism = 4, ClutterWeight = 1 };
        var output = await paper.SearchAsync(image, false, trained.Model, null, options, default);
        var phases = Assert.IsType<ShapeSearchPhases>(output.Phases);
        Assert.InRange(phases.SourceOpenMilliseconds + phases.CoarseSearchMilliseconds + phases.OriginalRefinementMilliseconds +
            phases.WindowSearchMilliseconds + phases.FinalSuppressionMilliseconds, 0, output.TotalMilliseconds + 1);
        Assert.False(output.Truncated);
        Assert.False(output.MatchLimitExceeded);
        Assert.Equal(expected.Length, output.Matches.Length);
        foreach (var target in expected)
        {
            var match = Assert.Single(output.Matches.Where(match => Math.Abs(match.Pose.X - target.X) < 1 && Math.Abs(match.Pose.Y - target.Y) < 1));
            Assert.InRange(Math.Abs(match.Pose.Angle - target.Angle), 0, .03);
            Assert.InRange(Math.Abs(match.Pose.Scale - target.Scale), 0, .025);
        }
    }

    [Fact]
    public async Task KensaGndAndPatternSampleCircleMatchAccuratelyWithoutTruncation()
    {
        var loader = new ImageLoader(Path.GetTempPath());
        var service = new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper);

        var patternPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/TeachingToolWpf/Assets/pattern-matching.png"));
        if (File.Exists(patternPath))
        {
            var patternImage = new ImageReference(patternPath, "pattern-matching.png", 640, 400);
            var circleRoi = new RoiGeometry(new(173.5, 156.5), 24, 24, 0);
            var trainedCircle = await service.TrainAsync(patternImage, false, circleRoi, new()
            {
                MinimumAngleDegrees = -30,
                MaximumAngleDegrees = 30,
                AngleStepDegrees = 10,
                MinimumScale = 0.8,
                MaximumScale = 1.3,
                ScaleStep = 0.1,
                UseFineDetailValidation = true,
                BuildClutterModel = true
            }, default);
            var circleResult = await service.SearchAsync(patternImage, false, trainedCircle.Model, null, new()
            {
                MaximumMatches = 256,
                MaximumParallelism = 4,
                ClutterWeight = 1
            }, default);
            Assert.False(circleResult.Truncated);
            Assert.Equal(2, circleResult.Matches.Length);
            Assert.Contains(circleResult.Matches, m => Math.Abs(m.Pose.X - 173.5) < 1.5 && Math.Abs(m.Pose.Y - 156.5) < 1.5);
            Assert.Contains(circleResult.Matches, m => Math.Abs(m.Pose.X - 436.8) < 1.5 && Math.Abs(m.Pose.Y - 231.9) < 1.5);
        }

        var kensaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/CpuMatching.Modules/kensa.png"));
        if (File.Exists(kensaPath))
        {
            var kensaImage = new ImageReference(kensaPath, "kensa.png", 1024, 1536);
            foreach (var (cx, cy, width, height) in new[] { (144.0, 145.5, 26d, 27d), (144.0, 144.5, 26d, 27d), (144.0, 145.5, 24d, 24d) })
            {
                var circleFiducialRoi = new RoiGeometry(new(cx, cy), width, height, 0);
                var trainedFiducial = await service.TrainAsync(kensaImage, false, circleFiducialRoi, new()
                {
                    MinimumAngleDegrees = -30,
                    MaximumAngleDegrees = 30,
                    AngleStepDegrees = 10,
                    MinimumScale = 1,
                    MaximumScale = 1,
                    UseFineDetailValidation = true,
                    BuildClutterModel = true
                }, default);
                var fiducialResult = await service.SearchAsync(kensaImage, false, trainedFiducial.Model, null, new()
                {
                    MinimumScore = 0.72,
                    MaximumMatches = 256,
                    MaximumParallelism = 8,
                    ClutterWeight = 1
                }, default);
                Assert.False(fiducialResult.Truncated);
                Assert.Equal(48, fiducialResult.Matches.Length);
                Assert.All(fiducialResult.Matches, m => Assert.Equal(RefinementStatus.Converged, m.Refinement.Status));
                Assert.Contains(fiducialResult.Matches, m => Math.Abs(m.Pose.X - 144.0) < 2.5 && Math.Abs(m.Pose.Y - 145.5) < 2.5);
                Assert.Contains(fiducialResult.Matches, m => Math.Abs(m.Pose.X - 144.0) < 2.5 && Math.Abs(m.Pose.Y - 375.5) < 2.5);
            }

            double[] smdExpectedX = [172.0, 437.0, 700.0];
            double[] smdExpectedY = [145.0, 459.5, 773.5, 1088.0];
            foreach (var (minAngle, maxAngle) in new[] { (-180d, 180d), (-30d, 30d) })
            {
                var smdRoi = new RoiGeometry(new(172.0, 145.0), 17, 17, 0);
                var trainedSmd = await service.TrainAsync(kensaImage, false, smdRoi, new()
                {
                    MinimumAngleDegrees = minAngle,
                    MaximumAngleDegrees = maxAngle,
                    AngleStepDegrees = 10,
                    MinimumScale = 1,
                    MaximumScale = 1,
                    UseFineDetailValidation = true,
                    BuildClutterModel = true
                }, default);
                var smdResult = await service.SearchAsync(kensaImage, false, trainedSmd.Model, null, new()
                {
                    MinimumScore = 0.72,
                    MaximumMatches = 256,
                    MaximumParallelism = 8,
                    ClutterWeight = 1
                }, default);
                Assert.False(smdResult.Truncated);
                Assert.Equal(12, smdResult.Matches.Length);
                Assert.All(smdResult.Matches, m =>
                {
                    Assert.Equal(RefinementStatus.Converged, m.Refinement.Status);
                    Assert.Contains(smdExpectedX, x => Math.Abs(m.Pose.X - x) < 3.5);
                    Assert.Contains(smdExpectedY, y => Math.Abs(m.Pose.Y - y) < 3.5);
                });
            }

            double[] expectedX = [174.0, 439.1, 702.2, 172.9, 437.8, 700.9];
            double[] expectedY = [371.0, 685.9, 1000.0, 1314.2, 386.0, 700.9, 1014.9, 1329.2];
            foreach (var (cx, cy, width, height) in new[] { (174.0, 371.0, 24d, 14d), (174.0, 371.0, 28d, 16d), (173.0, 370.0, 22d, 12d), (173.0, 370.0, 26d, 15d) })
            {
                var gndRoi = new RoiGeometry(new(cx, cy), width, height, 0);
                var trainedGnd = await service.TrainAsync(kensaImage, false, gndRoi, new()
                {
                    MinimumAngleDegrees = -30,
                    MaximumAngleDegrees = 30,
                    AngleStepDegrees = 10,
                    MinimumScale = 1,
                    MaximumScale = 1,
                    UseFineDetailValidation = true,
                    BuildClutterModel = true
                }, default);
                var gndResult = await service.SearchAsync(kensaImage, false, trainedGnd.Model, null, new()
                {
                    MaximumMatches = 256,
                    MaximumParallelism = 8,
                    ClutterWeight = 1
                }, default);
                Assert.False(gndResult.Truncated);
                Assert.Equal(24, gndResult.Matches.Length);
                Assert.Contains(gndResult.Matches, m => Math.Abs(m.Pose.X - 174.0) < 4.5 && Math.Abs(m.Pose.Y - 1000.0) < 4.5);
                Assert.Contains(gndResult.Matches, m => Math.Abs(m.Pose.X - 172.9) < 4.5 && Math.Abs(m.Pose.Y - 1014.9) < 4.5);
                Assert.All(gndResult.Matches, m =>
                {
                    Assert.Contains(expectedX, x => Math.Abs(m.Pose.X - x) < 4.5);
                    Assert.Contains(expectedY, y => Math.Abs(m.Pose.Y - y) < 4.5);
                });
            }
        }
    }

    [Fact]
    public async Task KensaConcentricDonutRejectsTrayHolesInEveryPolarityAndClutterSetting()
    {
        var kensaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/CpuMatching.Modules/kensa.png"));
        if (!File.Exists(kensaPath))
        {
            return;
        }
        var service = new ShapeMatchingService(new ImageLoader(Path.GetTempPath()), () => null, pipeline: ShapeSearchPipeline.Paper);
        var image = new ImageReference(kensaPath, "kensa.png", 1024, 1536);
        var roi = new RoiGeometry(new(144, 145), 22, 21, 0);
        foreach (var (ignorePolarity, clutterWeight, maximumCount) in new[] { (false, 1d, 48), (true, 1d, 48), (true, 0d, 50), (false, 0d, 50) })
        {
            var trained = await service.TrainAsync(image, false, roi, new()
            {
                MinimumAngleDegrees = -180,
                MaximumAngleDegrees = 180,
                AngleStepDegrees = 10,
                Polarity = ignorePolarity ? ContrastPolarity.IgnoreLocal : ContrastPolarity.Preserve,
                UseFineDetailValidation = true,
                BuildClutterModel = true
            }, default);
            var result = await service.SearchAsync(image, false, trained.Model, null, new()
            {
                MinimumScore = 0.72,
                MaximumMatches = 256,
                MaximumParallelism = 8,
                ClutterWeight = clutterWeight
            }, default);
            Assert.False(result.Truncated);
            // 트레이의 검은 구멍은 외곽/안쪽 윤곽의 단면 구조가 달라 제외된다. 클러터 감점을 끄면 인접 부품의 약한 일치 2건까지만 허용한다.
            Assert.InRange(result.Matches.Length, 48, maximumCount);
            Assert.True(result.Matches.OrderByDescending(m => m.Score).Take(48).All(m => m.Score >= 0.98));
            Assert.DoesNotContain(result.Matches, m => Math.Abs(m.Pose.X - 92) < 6 || Math.Abs(m.Pose.X - 937) < 6);
        }
    }

    [Fact]
    public async Task KensaConnectorsDetectAllTwelveWithoutTruncation()
    {
        var kensaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/CpuMatching.Modules/kensa.png"));
        if (!File.Exists(kensaPath))
        {
            return;
        }
        var service = new ShapeMatchingService(new ImageLoader(Path.GetTempPath()), () => null, pipeline: ShapeSearchPipeline.Paper);
        var image = new ImageReference(kensaPath, "kensa.png", 1024, 1536);
        var trained = await service.TrainAsync(image, false, new RoiGeometry(new(244, 414), 179, 31, 0), new()
        {
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10,
            UseFineDetailValidation = true,
            BuildClutterModel = true
        }, default);
        var result = await service.SearchAsync(image, false, trained.Model, null, new()
        {
            MinimumScore = 0.72,
            MaximumMatches = 256,
            MaximumParallelism = 12,
            ClutterWeight = 1
        }, default);
        Assert.False(result.Truncated);
        Assert.Equal(12, result.Matches.Length);
        Assert.All(result.Matches, m => Assert.True(m.Score >= 0.990, m.ToString()));
    }

    [Fact]
    public async Task KensaAsymmetricContextRoiDetectsOnlyBottomLeftTwelve()
    {
        var kensaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/CpuMatching.Modules/kensa.png"));
        if (!File.Exists(kensaPath))
        {
            return;
        }
        var service = new ShapeMatchingService(new ImageLoader(Path.GetTempPath()), () => null, pipeline: ShapeSearchPipeline.Paper);
        var image = new ImageReference(kensaPath, "kensa.png", 1024, 1536);
        foreach (var (cx, cy, w, h, minAngle, maxAngle, minScore) in new[]
        {
            (144d, 375d, 38d, 44d, -180d, 180d, 0.72),
            (144d, 375d, 33d, 38d, -30d, 30d, 0.60),
            (144d, 375d, 33d, 38d, -180d, 180d, 0.72),
            (144d, 375d, 31d, 34d, -40d, 40d, 0.72),
            (144d, 376d, 29d, 32d, -40d, 40d, 0.72)
        })
        {
            var trained = await service.TrainAsync(image, false, new RoiGeometry(new(cx, cy), w, h, 0), new()
            {
                MinimumAngleDegrees = minAngle,
                MaximumAngleDegrees = maxAngle,
                AngleStepDegrees = 10,
                UseFineDetailValidation = true,
                BuildClutterModel = true
            }, default);
            var result = await service.SearchAsync(image, false, trained.Model, null, new()
            {
                MinimumScore = minScore,
                MaximumMatches = 256,
                MaximumParallelism = 12,
                ClutterWeight = 1
            }, default);
            Assert.False(result.Truncated);
            var lowResult = await service.SearchAsync(image, false, trained.Model, null, new()
            {
                MinimumScore = 0.40,
                CandidateThreshold = 0.55,
                MaximumMatches = 256,
                MaximumParallelism = 12,
                ClutterWeight = 1
            }, default);
            Assert.True(result.Matches.Length == 12, $"ROI ({cx},{cy}) {w}x{h} [{minAngle}..{maxAngle}] feats={trained.Model.Levels[0].Features.Length} symC=({trained.Model.SymmetryCenter.X:F2},{trained.Model.SymmetryCenter.Y:F2}) coreD={trained.Model.SymmetryCoreDiameter:F1} radii=({trained.Model.ContextSectorRadii.X:F1},{trained.Model.ContextSectorRadii.Y:F1}) occ={trained.Model.OccupiedContextSectorMask} emp={trained.Model.EmptyContextSectorMask}: count={result.Matches.Length}, topScores={string.Join(",", result.Matches.Select(m => $"({m.Pose.X:F1},{m.Pose.Y:F1}@{m.Pose.Angle * 180 / Math.PI:F0}d,dir={m.DirectionScore:F3},det={m.DetailConsistency ?? -1:F3},clut={m.ClutterPenaltyRatio ?? -1:F3},um={m.UnmatchedFeatures.Length},km={m.KeyMatchedFeatures.Length}):{m.Score:F3}"))}");
            Assert.All(result.Matches, m => Assert.True(m.Score >= 0.930, m.ToString()));
            var maxTopLeftScore = lowResult.Matches
                .Where(m => Math.Abs(m.Pose.Y - 145) < 15 || Math.Abs(m.Pose.Y - 460) < 15 || Math.Abs(m.Pose.Y - 774) < 15 || Math.Abs(m.Pose.Y - 1088) < 15)
                .Select(m => m.Score)
                .DefaultIfEmpty(0.0)
                .Max();
            Assert.True(maxTopLeftScore <= 0.55, $"ROI ({cx},{cy}) {w}x{h} [{minAngle}..{maxAngle}]: maxTopLeftScore={maxTopLeftScore:F3}");
        }

        // 위치 고정 하드코딩 제거 검증: 좌상단 모서리(144, 145)를 정답으로 티칭했을 때도 자기 자신을 죽이지 않고 좌상단 12개만 정확히 검출하며, 좌하단(Y=375, 690, 1004, 1318)은 <= 0.55로 배제한다.
        var topLeftTrained = await service.TrainAsync(image, false, new RoiGeometry(new(144, 145), 33, 38, 0), new()
        {
            MinimumAngleDegrees = -30,
            MaximumAngleDegrees = 30,
            AngleStepDegrees = 10,
            UseFineDetailValidation = true,
            BuildClutterModel = true
        }, default);
        var topLeftResult = await service.SearchAsync(image, false, topLeftTrained.Model, null, new()
        {
            MinimumScore = 0.72,
            MaximumMatches = 256,
            MaximumParallelism = 12,
            ClutterWeight = 1
        }, default);
        Assert.Equal(12, topLeftResult.Matches.Length);
        Assert.All(topLeftResult.Matches, m => Assert.True(m.Score >= 0.930, m.ToString()));
        var topLeftLowResult = await service.SearchAsync(image, false, topLeftTrained.Model, null, new()
        {
            MinimumScore = 0.40,
            CandidateThreshold = 0.55,
            MaximumMatches = 256,
            MaximumParallelism = 12,
            ClutterWeight = 1
        }, default);
        var maxBottomLeftScore = topLeftLowResult.Matches
            .Where(m => Math.Abs(m.Pose.Y - 375) < 15 || Math.Abs(m.Pose.Y - 690) < 15 || Math.Abs(m.Pose.Y - 1004) < 15 || Math.Abs(m.Pose.Y - 1318) < 15)
            .Select(m => m.Score)
            .DefaultIfEmpty(0.0)
            .Max();
        Assert.True(maxBottomLeftScore <= 0.55, $"TopLeft ROI (144,145) 33x38: maxBottomLeftScore={maxBottomLeftScore:F3}");
    }

    [Fact]
    public async Task KensaOccludedConnectorsDetectAllTwelveUnderThreeOcclusionVariants()
    {
        var kensaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/CpuMatching.Modules/kensa.png"));
        if (!File.Exists(kensaPath))
        {
            return;
        }
        var service = new ShapeMatchingService(new ImageLoader(Path.GetTempPath()), () => null, pipeline: ShapeSearchPipeline.Paper);
        var reference = new ImageReference(kensaPath, "kensa.png", 1024, 1536);
        var trained = await service.TrainAsync(reference, false, new RoiGeometry(new(244, 414), 179, 31, 0), new()
        {
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10,
            UseFineDetailValidation = true,
            BuildClutterModel = true
        }, default);

        byte[] basePixels;
        using (var reader = await MatchingImageReader.OpenAsync(reference, false, new ImageLoader(Path.GetTempPath()), () => null, default))
        {
            reader.PreferDirectGray = true;
            var full = reader.Read(0, 0, 1024, 1536, default);
            basePixels = full.Pixels.ToArray();
        }
        foreach (var mode in new[] { "bg", "white", "noise" })
        {
            var pixels = (byte[])basePixels.Clone();
            var rng = new Random(7);
            for (var y = 396; y <= 432; y++)
            {
                for (var x = 417; x <= 484; x++)
                {
                    pixels[(y * 1024) + x] = mode switch
                    {
                        "bg" => (byte)22,
                        "white" => (byte)255,
                        _ => (byte)rng.Next(0, 256)
                    };
                }
            }
            var occLoader = new Loader(new GrayImage(pixels, 1024, 1536, 1024));
            var occService = new ShapeMatchingService(occLoader, () => null, pipeline: ShapeSearchPipeline.Paper);
            var result = await occService.SearchAsync(new("fixture", "fixture", 1024, 1536), false, trained.Model, null, new()
            {
                MinimumScore = 0.72,
                MaxOcclusionRatio = 0.40,
                MaximumMatches = 256,
                MaximumParallelism = 12,
                ClutterWeight = 1
            }, default);
            Assert.False(result.Truncated);
            Assert.Equal(12, result.Matches.Length);
            var occluded = Assert.Single(result.Matches, m => Math.Abs(m.Pose.X - 508.2) <= 2.0 && Math.Abs(m.Pose.Y - 414.0) <= 2.0);
            var posError = Math.Sqrt(((occluded.Pose.X - 508.2) * (occluded.Pose.X - 508.2)) + ((occluded.Pose.Y - 414.0) * (occluded.Pose.Y - 414.0)));
            Assert.True(posError <= 0.5, $"mode={mode}, pose=({occluded.Pose.X:F2},{occluded.Pose.Y:F2}), err={posError:F3}");
        }
    }

    [Fact]
    public async Task KensaOccludedConnectorsUnderArbitraryRotationsDetectAccuratelyWithZeroFalsePositives()
    {
        var kensaPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/CpuMatching.Modules/kensa.png"));
        if (!File.Exists(kensaPath))
        {
            return;
        }
        var service = new ShapeMatchingService(new ImageLoader(Path.GetTempPath()), () => null, pipeline: ShapeSearchPipeline.Paper);
        var reference = new ImageReference(kensaPath, "kensa.png", 1024, 1536);
        var trained = await service.TrainAsync(reference, false, new RoiGeometry(new(244, 414), 179, 31, 0), new()
        {
            MinimumAngleDegrees = -180,
            MaximumAngleDegrees = 180,
            AngleStepDegrees = 10,
            UseFineDetailValidation = true,
            BuildClutterModel = true
        }, default);

        byte[] basePixels;
        using (var reader = await MatchingImageReader.OpenAsync(reference, false, new ImageLoader(Path.GetTempPath()), () => null, default))
        {
            reader.PreferDirectGray = true;
            var full = reader.Read(0, 0, 1024, 1536, default);
            basePixels = full.Pixels.ToArray();
        }
        const double srcCenterX = 508.2;
        const double srcCenterY = 414.0;
        const int canvasSize = 512;
        const double dstCenterX = 256.0;
        const double dstCenterY = 256.0;
        foreach (var angleDegrees in new[] { 65.0, 135.0 })
        {
            foreach (var mode in new[] { "bg", "white", "noise" })
            {
                var occludedSrc = (byte[])basePixels.Clone();
                var rng = new Random(7);
                for (var y = 396; y <= 432; y++)
                {
                    for (var x = 417; x <= 484; x++)
                    {
                        occludedSrc[(y * 1024) + x] = mode switch
                        {
                            "bg" => (byte)22,
                            "white" => (byte)255,
                            _ => (byte)rng.Next(0, 256)
                        };
                    }
                }
                var rad = angleDegrees * Math.PI / 180.0;
                var cos = Math.Cos(rad);
                var sin = Math.Sin(rad);
                var rotated = new byte[canvasSize * canvasSize];
                Array.Fill(rotated, (byte)22);
                for (var dy = 0; dy < canvasSize; dy++)
                {
                    for (var dx = 0; dx < canvasSize; dx++)
                    {
                        var rx = dx + 0.5 - dstCenterX;
                        var ry = dy + 0.5 - dstCenterY;
                        // 회전된 장면 중심 주변 모듈(가로 ±130px, 세로 ±190px: 실제 기판 배선·도넛·외곽선 및 35% 가림 커넥터 포함)을 쌍선형 보간으로 투영한다.
                        var u = (cos * rx) + (sin * ry);
                        var v = (-sin * rx) + (cos * ry);
                        if (Math.Abs(u) > 130.0 || Math.Abs(v) > 190.0)
                        {
                            continue;
                        }
                        var sx = srcCenterX + u - 0.5;
                        var sy = srcCenterY + v - 0.5;
                        var x0 = (int)Math.Floor(sx);
                        var y0 = (int)Math.Floor(sy);
                        if (x0 < 0 || y0 < 0 || x0 + 1 >= 1024 || y0 + 1 >= 1536)
                        {
                            continue;
                        }
                        var fx = sx - x0;
                        var fy = sy - y0;
                        var p00 = occludedSrc[(y0 * 1024) + x0];
                        var p10 = occludedSrc[(y0 * 1024) + x0 + 1];
                        var p01 = occludedSrc[((y0 + 1) * 1024) + x0];
                        var p11 = occludedSrc[((y0 + 1) * 1024) + x0 + 1];
                        var val = ((1 - fx) * (1 - fy) * p00) + (fx * (1 - fy) * p10) + ((1 - fx) * fy * p01) + (fx * fy * p11);
                        rotated[(dy * canvasSize) + dx] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                    }
                }
                var rotLoader = new Loader(new GrayImage(rotated, canvasSize, canvasSize, canvasSize));
                var rotService = new ShapeMatchingService(rotLoader, () => null, pipeline: ShapeSearchPipeline.Paper);
                var result = await rotService.SearchAsync(new("fixture", "fixture", canvasSize, canvasSize), false, trained.Model, null, new()
                {
                    MinimumScore = 0.72,
                    MaxOcclusionRatio = 0.40,
                    MaximumMatches = 256,
                    MaximumParallelism = 12,
                    ClutterWeight = 1
                }, default);
                Assert.False(result.Truncated);
                var match = Assert.Single(result.Matches);
                var posError = Math.Sqrt(((match.Pose.X - dstCenterX) * (match.Pose.X - dstCenterX)) + ((match.Pose.Y - dstCenterY) * (match.Pose.Y - dstCenterY)));
                Assert.True(posError <= 0.5, $"angle={angleDegrees}, mode={mode}, pose=({match.Pose.X:F2},{match.Pose.Y:F2}), err={posError:F3}");
            }
        }
    }
    [Fact]
    public async Task OpenCvAppliesSuppressionOnceAcrossSearchWindowSeam()
    {
        var pattern = new byte[100 * 20];
        var mask = new byte[pattern.Length];
        var scene = new byte[2300 * 20];
        var random = new Random(409);
        for (var y = 0; y < 20; y++)
        {
            for (var x = 38; x < 62; x++)
            {
                var value = random.Next(40, 216);
                pattern[(y * 100) + x] = (byte)value;
                mask[(y * 100) + x] = 255;
                scene[(y * 2300) + 1990 + x] = (byte)Math.Clamp(value + random.Next(-40, 41), 0, 255);
                scene[(y * 2300) + 2040 + x] = (byte)Math.Clamp(value + random.Next(-20, 21), 0, 255);
                scene[(y * 2300) + 2090 + x] = (byte)value;
            }
        }
        var model = CorrelationTemplate.Train(new(pattern, 100, 20, 100), new(mask, new(50, 10)));
        var service = new TemplateMatchingService(new Loader(new(scene, 2300, 20, 2300)), () => null);
        var result = await service.SearchAsync(new("fixture", "fixture", 2300, 20), false, model, null,
            new()
            {
                MinimumScore = .8,
                MaximumOverlap = .3
            }, default);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Windows);
        Assert.Equal(2, result.Matches.Length);
        Assert.Contains(result.Matches, match => match.ReferencePoint.X == 2040);
        Assert.Contains(result.Matches, match => match.ReferencePoint.X == 2140);
    }

    [Fact]
    public async Task ReducedOriginalTilesMatchTrainingReductionIncludingOddSizesAndStride()
    {
        var bytes = new byte[1060 * 1033];
        new Random(89).NextBytes(bytes);
        var image = new GrayImage(bytes, 1049, 1033, 1060);
        var loader = new Loader(image);
        using (var reader = await MatchingImageReader.OpenAsync(new("fixture", "fixture", 1049, 1033), false, loader, () => null, default))
        {
            var reduced = reader.ReadReduced(0, 0, image.Width, image.Height, 8, default);
            var expected = InspectionPyramid.Reduce(image, 8);
            Assert.Equal(expected.Width, reduced.Width);
            Assert.Equal(expected.Height, reduced.Height);
            Assert.Equal(expected.Pixels.ToArray(), reduced.Pixels.ToArray());
            Assert.Equal(9, loader.Reads);
        }
        Assert.Equal(1, loader.Disposed);
    }

    [Fact]
    public async Task LargeImageCoarseSearchRefinesOriginalPoseAndHonorsSmallCircle()
    {
        var scene = SyntheticScene.Create(2049, 1153, new MatchPose(650, 520, 0, 10), new MatchPose(1517.3, 591.7, 3.4 * Math.PI / 180, 10));
        var loader = new Loader(scene);
        var service = new ShapeMatchingService(loader, () => null);
        var reference = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var trained = await service.TrainAsync(reference, false, new(new(650, 520), 512, 512, 0),
            new()
            {
                MinimumAngleDegrees = -10,
                MaximumAngleDegrees = 10,
                AngleStepDegrees = 10
            }, default);
        Assert.Equal(4, trained.Model.Levels.Length);
        var result = await service.SearchAsync(reference, false, trained.Model, null, new(), default);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Matches.Length);
        Assert.Contains(result.Matches, match => Math.Abs(match.Pose.X - 1517.3) < .5 && Math.Abs(match.Pose.Y - 591.7) < .5 &&
            Math.Abs(match.Pose.Angle - (3.4 * Math.PI / 180)) < .3 * Math.PI / 180);
        var region = RoiGeometry.Create(RoiShapeKind.Circle, new(1517.3, 591.7), 2, 2);
        var limited = await service.SearchAsync(reference, false, trained.Model, region, new(), default);
        Assert.False(limited.Truncated);
        Assert.True(region.Contains(new PointD(Assert.Single(limited.Matches).Pose.X, limited.Matches[0].Pose.Y)));
        Assert.Equal(3, loader.Disposed);
    }

    private sealed class Loader(GrayImage image) : IImageLoader
    {
        public int Reads;
        public int Disposed;

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(this, image));
    }

    private sealed class Source(Loader owner, GrayImage image) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", image.Width, image.Height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Assert.Equal(0, key.Level);
            owner.Reads++;
            var left = key.X * 512;
            var top = key.Y * 512;
            var width = Math.Min(512, image.Width - left);
            var height = Math.Min(512, image.Height - top);
            var pixels = new PixelBuffer(width, height, PixelKind.Gray8);
            for (var y = 0; y < height; y++)
            {
                image.Pixels.Span.Slice(((top + y) * image.Stride) + left, width).CopyTo(pixels.Span.Slice(y * pixels.Stride, width));
            }
            return pixels;
        }

        public void Dispose() => owner.Disposed++;
    }
}
