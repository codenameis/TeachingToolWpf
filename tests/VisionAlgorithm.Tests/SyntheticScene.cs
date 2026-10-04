using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

internal static class SyntheticScene
{
    // 독립 역변환과 4×4 면적 샘플링으로 비정수 자세의 생성 오차를 줄인다.
    public static GrayImage Create(int width, int height, params MatchPose[] poses)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        foreach (var pose in poses)
                        {
                            var dx = (x + ((sx + 0.5) / 4) - pose.X) / pose.Scale;
                            var dy = (y + ((sy + 0.5) / 4) - pose.Y) / pose.Scale;
                            var u = (dx * Math.Cos(pose.Angle)) + (dy * Math.Sin(pose.Angle));
                            var v = (-dx * Math.Sin(pose.Angle)) + (dy * Math.Cos(pose.Angle));
                            var filled = (u >= -20 && u <= -9 && v >= -22 && v <= 20) ||
                                (u >= -9 && u <= 19 && v >= 9 && v <= 20) ||
                                (u >= -9 && u <= 12 && v >= -22 && v <= -15) ||
                                (((u - 11) * (u - 11)) + ((v + 1) * (v + 1)) <= 36);
                            if (filled)
                            {
                                hits++;
                                break;
                            }
                        }
                    }
                }
                pixels[(y * width) + x] = (byte)(25 + (hits * 200 / 16));
            }
        }
        return new(pixels, width, height, width);
    }
}

