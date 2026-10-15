using System.Text.Json;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionInspection.Cli;

namespace VisionInspection.Tests;

public sealed class RecipeJsonTests
{
    [Theory]
    [InlineData("evaluateGeometry")]
    [InlineData("minimumCoverage")]
    [InlineData("maximumFitErrorPixels")]
    [InlineData("maximumClutterRatio")]
    [InlineData("geometricTolerancePixels")]
    public void RemovedGeometryOptionsAreRejectedInsteadOfSilentlyIgnored(string field)
    {
        var json = "{\"searchOptions\":{\"" + field + "\":0.5}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<InspectionRecipe>(json, RecipeJson.CreateOptions()));
    }

    [Fact]
    public void PartialSearchOptionsPreserveHostDefaults()
    {
        var recipe = JsonSerializer.Deserialize<InspectionRecipe>("""{"searchOptions":{"minimumScore":0.8}}""", RecipeJson.CreateOptions())!;

        Assert.Equal(.8, recipe.SearchOptions.MinimumScore);
        Assert.Equal(1, recipe.SearchOptions.ClutterWeight);
        Assert.Equal(256, recipe.SearchOptions.MaximumMatches);
    }

    [Fact]
    public void ExplicitSearchOptionsOverrideHostDefaults()
    {
        var recipe = JsonSerializer.Deserialize<InspectionRecipe>("""{"searchOptions":{"clutterWeight":0,"maximumMatches":7}}""", RecipeJson.CreateOptions())!;

        Assert.Equal(0, recipe.SearchOptions.ClutterWeight);
        Assert.Equal(7, recipe.SearchOptions.MaximumMatches);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void RecipeRoundTripPreservesClutterToleranceAndSearchDefaults(double tolerance)
    {
        var recipe = new InspectionRecipe
        {
            SearchOptions = new SearchOptions { ClutterTolerancePixels = tolerance, MaximumMatches = 71, ClutterWeight = .6 }
        };
        var options = RecipeJson.CreateOptions();

        var json = JsonSerializer.Serialize(recipe, options);
        var restored = JsonSerializer.Deserialize<InspectionRecipe>(json, options)!;

        Assert.Equal(tolerance, restored.SearchOptions.ClutterTolerancePixels);
        Assert.Equal(71, restored.SearchOptions.MaximumMatches);
        Assert.Equal(.6, restored.SearchOptions.ClutterWeight);
        Assert.DoesNotContain("Infinity", json);
    }
}
