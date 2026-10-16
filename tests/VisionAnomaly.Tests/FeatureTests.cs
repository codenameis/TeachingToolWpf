using VisionAnomaly.Learning;

namespace VisionAnomaly.Tests;

public sealed class FeatureTests
{
    internal static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "resnet18-patches.onnx");

    [Fact]
    public void OnnxFeaturesMatchTheExportedTorchReference()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/VisionAnomaly/Models"));
        var input = ReadFloats(Path.Combine(root, "golden-input.bin"));
        var expected = ReadFloats(Path.Combine(root, "golden-output.bin"));
        using var extractor = new PatchFeatureExtractor(ModelPath);
        var actual = extractor.Run(input);
        Assert.Equal(784 * 96, actual.Length);
        Assert.True(actual.Zip(expected).Max(pair => Math.Abs(pair.First - pair.Second)) < .0001);
    }

    private static float[] ReadFloats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
