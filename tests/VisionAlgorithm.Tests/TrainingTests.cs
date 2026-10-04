using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

public sealed class TrainingTests
{
    [Fact]
    public void PaperTrainingOmitsGeometryResourcesWithoutChangingDirectionalModel()
    {
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var region = new TrainingRegion(ReadOnlyMemory<byte>.Empty, new(48, 48));
        var options = new TrainingOptions { MinimumAngleDegrees = 0, MaximumAngleDegrees = 0 };
        var geometric = ShapeTrainer.Train(image, region, options);
        var paper = ShapeTrainer.Train(image, region, options with
        {
            BuildGeometricModel = false
        });

        Assert.NotNull(geometric.GeometryModel);
        Assert.Null(paper.GeometryModel);
        Assert.True(paper.EstimatedBytes < geometric.EstimatedBytes);
        Assert.Equal(geometric.Levels.Length, paper.Levels.Length);
        for (var level = 0; level < paper.Levels.Length; level++)
        {
            Assert.Equal(geometric.Levels[level].Features.ToArray(), paper.Levels[level].Features.ToArray());
        }
        Assert.Equal(geometric.TemplateCount, paper.TemplateCount);
        var result = new ShapeMatcher().Search(image, paper, new()
        {
            EvaluateGeometry = false
        });
        Assert.NotEmpty(result.Matches);
        Assert.All(result.Matches, match => Assert.Null(match.Geometry));
        Assert.Throws<ArgumentException>(() => new ShapeMatcher().Search(image, paper,
            new()
            {
                EvaluateGeometry = true,
                MinimumCoverage = .5
            }));
    }

    [Fact]
    public void ConstantImageAndMaskBoundaryDoNotCreateFeatures()
    {
        var pixels = Enumerable.Repeat((byte)128, 64 * 64).ToArray();
        var mask = new byte[pixels.Length];
        for (var y = 10; y < 50; y++)
        {
            for (var x = 10; x < 50; x++)
            {
                mask[(y * 64) + x] = 255;
            }
        }
        Assert.Throws<ArgumentException>(() => ShapeTrainer.Train(new(pixels, 64, 64, 64),
            new(mask, new(32, 32)), new()));
    }

    [Fact]
    public void TrainingProducesImmutableDistributedFeaturesAndRotatedDirections()
    {
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var model = ShapeTrainer.Train(image, new(ReadOnlyMemory<byte>.Empty, new(48, 48)),
            new()
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 90,
                AngleStepDegrees = 45,
                PyramidLevels = 2
            });
        Assert.True(model.Levels[0].Features.Length >= 40);
        Assert.True(model.TemplateCount >= 3);
        Assert.Equal(2, model.Levels.Length);
        var first = model.Templates[0].Levels[0][0];
        var rotated = model.Templates[^1].Levels[0][0];
        Assert.Equal(-first.Y, rotated.X);
        Assert.Equal(first.X, rotated.Y);
        Assert.Equal((first.Bin + 4) % 8, rotated.Bin);
        Assert.Throws<ArgumentException>(() => ShapeTrainer.Train(image,
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MaximumTemplates = 1
            }));
    }

    [Fact]
    public void ModelBoundsPreserveTheTrainingMaskWhenEdgesDoNotReachItsBoundary()
    {
        var image = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var mask = new byte[image.Width * image.Height];
        for (var y = 21; y < 73; y++)
        {
            for (var x = 25; x < 70; x++)
            {
                mask[(y * image.Width) + x] = 255;
            }
        }

        var model = ShapeTrainer.Train(image, new(mask, new(48, 48)), new());

        Assert.Equal(new ImagePoint(-23, -27), model.Bounds[0]);
        Assert.Equal(new ImagePoint(22, -27), model.Bounds[1]);
        Assert.Equal(new ImagePoint(22, 25), model.Bounds[2]);
        Assert.Equal(new ImagePoint(-23, 25), model.Bounds[3]);
    }
}


