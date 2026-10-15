using System.Text.Json;
using PoseMatch.Tdcm;
using PoseMatch.Tdcm.TorchSharp;

internal static class BackendSwitchProbe
{
    internal static void Run(PixelImage image, PixelImage template, string path)
    {
        var rows = new List<object>();
        Prediction? baseline = null;
        foreach (var backend in new[] { TdcmBackend.OnnxRuntime, TdcmBackend.TorchSharp, TdcmBackend.OnnxRuntime, TdcmBackend.TorchSharp })
        {
            using ITdcmMatcher matcher = backend == TdcmBackend.TorchSharp
                ? new TorchTdcmMatcher(device: TdcmDevice.Cuda) : new TdcmMatcher(device: TdcmDevice.Cuda);
            var prediction = matcher.Predict(image, template);
            baseline ??= prediction;
            float[][] reference = [baseline.Score, baseline.Sign, baseline.Cos, baseline.ScaleX, baseline.ScaleY];
            float[][] actual = [prediction.Score, prediction.Sign, prediction.Cos, prediction.ScaleX, prediction.ScaleY];
            var maximum = reference.Select((map, h) => map.Zip(actual[h], (a, b) => Math.Abs(a - b)).Max()).Max();
            rows.Add(new
            {
                backend = backend.ToString(),
                maximumDifference = maximum,
                passed = maximum < .002f
            });
            if (maximum >= .002f)
            {
                throw new InvalidOperationException($"백엔드 전환 출력 차이: {maximum}");
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }
}
