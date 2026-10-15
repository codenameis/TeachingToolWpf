using VisionAlgorithm.OpenSrc.PatternMatching;

namespace VisionAlgorithm.Tests;

internal sealed record ShapeScenario(string Name, double Degrees, double Scale = 1,
    double Contrast = 1, bool Occluded = false, bool Clutter = false, bool Inverted = false,
    bool SimilarDistractor = false, bool Empty = false);

internal static class RobustShapeScene
{
    public const int Width = 193;
    public const int Height = 181;
    public const double ExpectedX = 103.35;
    public const double ExpectedY = 87.65;

    public static ShapeScenario[] PositiveScenarios =>
    [
        new("rotation-minus179-scale080", -179.4, 0.8),
        new("rotation-minus135-scale090", -135.3, 0.9),
        new("rotation-minus90-scale100", -90.2),
        new("rotation-minus45-scale110", -45.1, 1.1),
        new("rotation-zero-scale120", 0.3, 1.2),
        new("rotation45-scale080", 45.2, 0.8),
        new("rotation90-scale090", 90.4, 0.9),
        new("rotation135-scale110", 135.3, 1.1),
        new("rotation179-scale120", 179.4, 1.2),
        new("contrast-half", 13.4, 1.07, Contrast: 0.5),
        new("right-quarter-occlusion", 13.4, 1.07, Occluded: true),
        new("background-clutter", -17.2, 0.93, Clutter: true),
        new("occlusion-clutter-half-contrast", 13.4, 1.07, 0.5, Occluded: true, Clutter: true)
    ];

    public static ShapeModel Train(ContrastPolarity polarity = ContrastPolarity.Preserve)
        => ShapeTrainer.Train(SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0)),
            new(ReadOnlyMemory<byte>.Empty, new(48, 48)), new()
            {
                MinimumAngleDegrees = -180,
                MaximumAngleDegrees = 180,
                AngleStepDegrees = 5,
                MinimumScale = 0.8,
                MaximumScale = 1.2,
                BuildClutterModel = true,
                Polarity = polarity
            });

    public static MatchPose ExpectedPose(ShapeScenario scenario)
        => new(ExpectedX, ExpectedY, scenario.Degrees * Math.PI / 180, scenario.Scale);

    public static GrayImage Create(ShapeScenario scenario)
    {
        var expected = ExpectedPose(scenario);
        var source = SyntheticScene.Create(Width, Height,
            scenario.Empty || scenario.SimilarDistractor ? [] : [expected]);
        var pixels = source.Pixels.ToArray();
        var cos = Math.Cos(expected.Angle);
        var sin = Math.Sin(expected.Angle);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var index = (y * Width) + x;
                var dx = (x + 0.5 - ExpectedX) / expected.Scale;
                var dy = (y + 0.5 - ExpectedY) / expected.Scale;
                var localX = (cos * dx) + (sin * dy);
                var localY = (-sin * dx) + (cos * dy);
                // 학습 도형의 39×42 경계 상자에서 오른쪽 1/4을 가린다. 특징 가림 비율과는 다르다.
                if (scenario.Occluded && localX >= 9.25 && localX <= 19 && localY >= -22 && localY <= 20)
                {
                    pixels[index] = 25;
                }
                if (scenario.SimilarDistractor)
                {
                    // 같은 수직·수평 획을 공유하지만 닫힌 직사각형이며 내부 원형 특징은 없다.
                    var frame = Math.Abs(localX) <= 20 && Math.Abs(localY) <= 22 &&
                        (Math.Abs(localX) >= 10 || Math.Abs(localY) >= 14);
                    pixels[index] = frame ? (byte)225 : (byte)25;
                }
                if (scenario.Clutter && IsClutter(x, y))
                {
                    pixels[index] = 205;
                }
                pixels[index] = (byte)Math.Round(40 + ((pixels[index] - 25) * scenario.Contrast));
                if (scenario.Inverted)
                {
                    pixels[index] = (byte)(255 - pixels[index]);
                }
            }
        }
        return new(pixels, Width, Height, Width);
    }

    public static double PositionError(MatchPose actual)
        => Math.Sqrt(Math.Pow(actual.X - ExpectedX, 2) + Math.Pow(actual.Y - ExpectedY, 2));

    public static double AngleErrorDegrees(MatchPose actual, MatchPose expected)
        => Math.Abs(Math.IEEERemainder(actual.Angle - expected.Angle, 2 * Math.PI)) * 180 / Math.PI;

    private static bool IsClutter(int x, int y)
        => (x >= 13 && x <= 62 && y >= 22 && y <= 29) ||
            (x >= 21 && x <= 28 && y >= 22 && y <= 95) ||
            (x >= 46 && x <= 54 && y >= 42 && y <= 133) ||
            (x >= 29 && x <= 68 && y >= 127 && y <= 134) ||
            (x >= 149 && x <= 158 && y >= 35 && y <= 127) ||
            (x >= 128 && x <= 178 && y >= 140 && y <= 148) ||
            (x >= 135 && x <= 182 && y >= 17 && y <= 23) ||
            (x >= 85 && x <= 115 && y >= 147 && y <= 153);
}
