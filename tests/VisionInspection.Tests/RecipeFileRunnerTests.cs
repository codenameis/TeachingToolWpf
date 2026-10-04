using System.Text.Json;
using TeachingToolWpf.Imaging.Tests;
using VisionInspection.Cli;

namespace VisionInspection.Tests;

public sealed class RecipeFileRunnerTests
{
    [Fact]
    public async Task FileExecutionResolvesRelativeImageAndReplacesCompleteJson()
    {
        var (directory, recipe, image) = CreateFixture();
        var output = Path.Combine(directory, "result.json");
        await File.WriteAllTextAsync(output, "previous result");
        var result = await new RecipeFileRunner().RunAsync(recipe, output);
        Assert.Equal("completed", result.Status);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("matches").GetArrayLength());
        Assert.Equal(image, json.RootElement.GetProperty("inspectionImage").GetProperty("path").GetString());
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task InvalidRecipeAndInputOverwriteLeaveOriginalFilesIntact()
    {
        var (directory, recipe, image) = CreateFixture();
        var originalImage = await File.ReadAllBytesAsync(image);
        var originalRecipe = await File.ReadAllTextAsync(recipe);
        var runner = new RecipeFileRunner();
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(recipe, image));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(recipe, recipe));
        Assert.Equal(originalImage, await File.ReadAllBytesAsync(image));
        Assert.Equal(originalRecipe, await File.ReadAllTextAsync(recipe));

        var output = Path.Combine(directory, "result.json");
        await File.WriteAllTextAsync(output, "previous result");
        await File.WriteAllTextAsync(recipe, "{invalid}");
        await Assert.ThrowsAsync<JsonException>(() => runner.RunAsync(recipe, output));
        Assert.Equal("previous result", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    private static (string Directory, string Recipe, string Image) CreateFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionInspection.FileTests", Guid.NewGuid().ToString("N"));
        var image = Path.Combine(directory, "original.png");
        PrimitiveShapeFixture.WritePng(image, PrimitiveShapeFixture.Mixed());
        var recipe = Path.Combine(directory, "recipe.json");
        File.WriteAllText(recipe, "{\"method\":\"primitives\",\"inspectionImage\":\"original.png\"}");
        return (directory, recipe, image);
    }
}
