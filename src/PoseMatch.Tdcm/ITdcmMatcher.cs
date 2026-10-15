namespace PoseMatch.Tdcm;

public enum TdcmBackend
{
    OnnxRuntime, TorchSharp
}

public interface ITdcmMatcher : IDisposable
{
    Prediction Predict(PixelImage image, PixelImage template, CancellationToken cancellationToken = default);
    MatchOutput Find(PixelImage image, PixelImage template, MatchOptions? options = null, CancellationToken cancellationToken = default);
}
