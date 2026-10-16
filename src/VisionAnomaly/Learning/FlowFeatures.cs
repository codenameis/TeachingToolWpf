using OpenCvSharp;

namespace VisionAnomaly.Learning;

internal sealed class FlowFeatures : IDisposable
{
    public const int Grid = 8;
    public const int Dimensions = Grid * Grid * 3;
    private Mat? _previous;
    private double _lastTime;

    public float[]? Extract(Mat bgra, double seconds)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        var delta = seconds - _lastTime;
        float[]? features = null;
        if (_previous is not null && delta is >= .02 and <= 1.0)
        {
            using var flow = new Mat();
            Cv2.CalcOpticalFlowFarneback(_previous, gray, flow, .5, 3, 15, 3, 5, 1.2, OpticalFlowFlags.None);
            features = new float[Dimensions];
            for (var y = 0; y < 224; y += 2)
            {
                for (var x = 0; x < 224; x += 2)
                {
                    var velocity = flow.At<Vec2f>(y, x);
                    var index = ((y / 28 * Grid) + (x / 28)) * 3;
                    features[index] += (float)(velocity.Item0 / delta / 196);
                    features[index + 1] += (float)(velocity.Item1 / delta / 196);
                    features[index + 2] += (float)(Math.Sqrt((velocity.Item0 * velocity.Item0) + (velocity.Item1 * velocity.Item1)) / delta / 196);
                }
            }
        }
        _previous?.Dispose();
        _previous = gray.Clone();
        _lastTime = seconds;
        return features;
    }

    public void Reset()
    {
        _previous?.Dispose();
        _previous = null;
    }
    public void Dispose() => Reset();
}
