using System.Security.Cryptography;
using System.Text.Json;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.ShapeDetection;
using VisionAlgorithm.Tests;

internal static class PoseSnapshot
{
    public static void Run()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var cases = 0;
        foreach (var kind in new[] { PrimitiveShapeKind.Diamond, PrimitiveShapeKind.CrossHash,
            PrimitiveShapeKind.DoubleSquare, PrimitiveShapeKind.Star, PrimitiveShapeKind.Asterisk })
        {
            foreach (var scale in new[] { .6, 1, 1.5 })
            {
                for (var angle = 0; angle < 180; angle++)
                {
                    Add(PcbMarkFixture.Create(kind, angle, scale, angle % 2 == 0));
                }
            }
        }
        foreach (var scale in new[] { .6, 1, 1.5 })
        {
            for (var angle = 0; angle < 45; angle++)
            {
                Add(PcbMarkFixture.Create(PrimitiveShapeKind.Asterisk, angle, scale, angle % 2 == 0,
                    contains: (x, y) => PcbMarkFixture.Contains(PrimitiveShapeKind.Asterisk, x, y, 8)));
            }
        }
        foreach (var gap in new[] { 0, 1, 2 })
        {
            for (var angle = 0; angle < 180; angle += 3)
            {
                var offset = 17 + (gap / 2d);
                Add(PcbMarkFixture.Create(PrimitiveShapeKind.DoubleSquare, angle, contains: (x, y) =>
                    (Math.Abs(x - offset) <= 17 && Math.Abs(y + offset) <= 17) ||
                    (Math.Abs(x + offset) <= 17 && Math.Abs(y - offset) <= 17)));
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            cases,
            hash = Convert.ToHexString(hash.GetHashAndReset())
        }));

        void Add(ShapeGrayImage image)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(PrimitiveShapeDetector.Detect(image, new())));
            cases++;
        }
    }
}
