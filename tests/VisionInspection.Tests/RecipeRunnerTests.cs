using System.Text.Json;
using TeachingToolWpf.Imaging.Tests;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionInspection.Cli;

namespace VisionInspection.Tests;

public sealed class RecipeRunnerTests
{
    [Fact]
    public async Task DoubleSquareRecipePreservesDisconnectedContoursThroughJson()
    {
        var (directory, runner, recipe) = CreateFixture();
        PrimitiveShapeFixture.WritePng(Path.Combine(directory, "double.png"),
            VisionAlgorithm.Tests.PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare, 27));
        var result = await runner.RunAsync(recipe with
        {
            InspectionImage = "double.png"
        }, directory);
        Assert.Equal("completed", result.Status);
        var match = Assert.Single(result.Matches);
        Assert.Equal("doublesquare", match.Kind);
        Assert.Empty(match.InnerContours);
        Assert.Single(match.AdditionalContours);
        var restored = JsonSerializer.Deserialize<InspectionRunResult>(JsonSerializer.Serialize(result, RecipeJson.CreateOptions()), RecipeJson.CreateOptions())!;
        Assert.Equal(match.AdditionalContours[0], Assert.Single(restored.Matches).AdditionalContours[0]);
    }

    [Fact]
    public async Task NestedPrimitiveRecipePreservesHoleContoursInSerializedResults()
    {
        var (directory, runner, recipe) = CreateFixture();
        PrimitiveShapeFixture.WritePng(Path.Combine(directory, "nested.png"), PrimitiveShapeFixture.Create(640, 480,
            new(PrimitiveShapeKind.Ring, 160, 180), new(PrimitiveShapeKind.Circle, 160, 180, Scale: .3),
            new(PrimitiveShapeKind.RectangleFrame, 430, 180, 27), new(PrimitiveShapeKind.Rectangle, 430, 180, 27, .3)));
        var result = await runner.RunAsync(recipe with
        {
            InspectionImage = "nested.png"
        }, directory);
        Assert.Equal("completed", result.Status);
        Assert.Equal(4, result.Matches.Length);
        var ring = Assert.Single(result.Matches, match => match.Kind == "ring");
        Assert.Single(ring.InnerContours);
        Assert.Single(Assert.Single(result.Matches, match => match.Kind == "rectangleframe").InnerContours);
        var json = JsonSerializer.Serialize(result, RecipeJson.CreateOptions());
        var restored = JsonSerializer.Deserialize<InspectionRunResult>(json, RecipeJson.CreateOptions())!;
        Assert.Equal(ring.InnerContours[0], Assert.Single(restored.Matches, match => match.Kind == "ring").InnerContours[0]);
        var unavailable = await runner.RunAsync(recipe with
        {
            PrimitiveOptions = new()
            {
                UseGpu = true
            }
        }, directory);
        Assert.Equal("failed", unavailable.Status);
        Assert.Contains("CUDA", unavailable.Error);
        Assert.Empty(unavailable.Matches);
    }

    [Fact]
    public async Task RemovedGeometricMethodIsRejectedBeforeOpeningImages()
    {
        var result = await new RecipeRunner().RunAsync(new()
        {
            Method = "geometric",
            InspectionImage = "missing.png"
        }, ".");
        Assert.Equal("failed", result.Status);
        Assert.Contains("method", result.Error);
        Assert.Null(result.InspectionImage);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task PrimitiveRecipeReadsOriginalFileAndReturnsFourMeasuredContours()
    {
        var (directory, runner, recipe) = CreateFixture();
        var result = await runner.RunAsync(recipe, directory);
        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(640, result.InspectionImage!.Width);
        Assert.Equal(480, result.InspectionImage.Height);
        Assert.Equal(new[] { "circle", "cross", "rectangle", "triangle" }, result.Matches.Select(match => match.Kind).Order());
        Assert.All(result.Matches, match =>
        {
            Assert.Equal("detectedBinaryContour", match.ContourSource);
            Assert.True(match.Contour.Length >= 3);
        });
    }

    [Theory]
    [InlineData("paper")]
    [InlineData("template")]
    public async Task PatternRecipesTrainAndInspectThroughSharedServices(string method)
    {
        var (directory, runner, recipe) = CreateFixture();
        var result = await runner.RunAsync(recipe with
        {
            Method = method,
            TrainingImage = "original.png",
            TeachingRoi = new()
            {
                CenterX = 440,
                CenterY = 120,
                Width = 140,
                Height = 120
            },
            TrainingOptions = new()
            {
                MinimumAngleDegrees = 0,
                MaximumAngleDegrees = 0
            },
            SearchOptions = new()
            {
                MinimumScore = .9,
                MaximumCandidates = 8192,
                MaximumVerifiedCandidates = 256,
                MaximumMatches = 1000,
                ClutterWeight = 1
            }
        }, directory);
        Assert.Null(result.Error);
        Assert.True(result.Status is "completed" or "incomplete");
        var match = Assert.Single(result.Matches, match => Math.Abs(match.Center.X - 440) < 1 && Math.Abs(match.Center.Y - 120) < 1);
        Assert.InRange(match.Score, .98, 1);
        Assert.Equal("transformedTeachingRoi", match.ContourSource);
        Assert.Equal(4, match.Contour.Length);
        Assert.NotNull(result.TrainingImage);
    }

    [Fact]
    public async Task OutputLimitAndCancellationCannotBecomeCompletedResults()
    {
        var (directory, runner, recipe) = CreateFixture();
        var limited = await runner.RunAsync(recipe with
        {
            PrimitiveOptions = new()
            {
                MaximumResults = 1
            }
        }, directory);
        Assert.Equal("incomplete", limited.Status);
        Assert.True(limited.Incomplete);
        Assert.Equal(2, limited.ExitCode);
        Assert.Single(limited.Matches);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await runner.RunAsync(recipe, directory, cancellation.Token);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal("requested", cancelled.CancellationReason);
        Assert.Equal(3, cancelled.ExitCode);
        Assert.Empty(cancelled.Matches);
    }

    [Fact]
    public async Task InvalidFileAndUnsupportedGpuRequestReturnExplicitFailures()
    {
        var (directory, runner, recipe) = CreateFixture();
        var missing = await runner.RunAsync(recipe with
        {
            InspectionImage = "missing.png"
        }, directory);
        Assert.Equal("failed", missing.Status);
        Assert.Equal(1, missing.ExitCode);
        Assert.NotEmpty(missing.Error!);
        Assert.Empty(missing.Matches);

        var unsupported = await runner.RunAsync(recipe with
        {
            SearchOptions = new()
            {
                ComputeMode = InspectionComputeMode.GpuPreprocessing
            }
        }, directory);
        Assert.Equal("failed", unsupported.Status);
        Assert.Contains("CPU", unsupported.Error);
    }

    [Fact]
    public void JsonRejectsUnknownFieldsAndNumericDeviceEnums()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<InspectionRecipe>(
            "{\"inspectionImage\":\"original.png\",\"minimumScroe\":0.1}", RecipeJson.CreateOptions()));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<InspectionRecipe>(
            "{\"searchOptions\":{\"computeMode\":1}}", RecipeJson.CreateOptions()));
    }

    private static (string Directory, RecipeRunner Runner, InspectionRecipe Recipe) CreateFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionInspection.Tests", Guid.NewGuid().ToString("N"));
        PrimitiveShapeFixture.WritePng(Path.Combine(directory, "original.png"), PrimitiveShapeFixture.Mixed());
        return (directory, new(Path.Combine(directory, "cache")), new()
        {
            Method = "primitives",
            InspectionImage = "original.png"
        });
    }
}
