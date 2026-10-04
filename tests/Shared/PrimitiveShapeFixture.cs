using System.IO;
using NetVips;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.ShapeDetection;

namespace TeachingToolWpf.Imaging.Tests;

internal readonly record struct PrimitiveFixtureShape(PrimitiveShapeKind Kind, double X, double Y, double Angle = 0, double Scale = 1);

internal static class PrimitiveShapeFixture
{
    public static GrayImage Mixed() => Create(640, 480,
        new(PrimitiveShapeKind.Triangle, 130, 120, 17), new(PrimitiveShapeKind.Rectangle, 440, 120, 27),
        new(PrimitiveShapeKind.Circle, 160, 350), new(PrimitiveShapeKind.Cross, 450, 350, 29));

    public static GrayImage Create(int width, int height, params PrimitiveFixtureShape[] shapes)
    {
        var pixels = new byte[checked(width * height)];
        Array.Fill(pixels, (byte)25);
        foreach (var shape in shapes)
        {
            var cosine = Math.Cos(shape.Angle * Math.PI / 180);
            var sine = Math.Sin(shape.Angle * Math.PI / 180);
            var extent = 80 * shape.Scale;
            for (var y = Math.Max(0, (int)(shape.Y - extent)); y < Math.Min(height, shape.Y + extent); y++)
            {
                for (var x = Math.Max(0, (int)(shape.X - extent)); x < Math.Min(width, shape.X + extent); x++)
                {
                    var dx = (x + .5 - shape.X) / shape.Scale;
                    var dy = (y + .5 - shape.Y) / shape.Scale;
                    var localX = (cosine * dx) + (sine * dy);
                    var localY = (-sine * dx) + (cosine * dy);
                    var inside = shape.Kind switch
                    {
                        PrimitiveShapeKind.Circle => (localX * localX) + (localY * localY) <= 1600,
                        PrimitiveShapeKind.Rectangle => Math.Abs(localX) <= 48 && Math.Abs(localY) <= 30,
                        PrimitiveShapeKind.Cross => (Math.Abs(localX) <= 14 && Math.Abs(localY) <= 48) ||
                            (Math.Abs(localX) <= 48 && Math.Abs(localY) <= 14),
                        PrimitiveShapeKind.Triangle => localY >= -30 && localY <= 60 && Math.Abs(localX) <= (60 - localY) / 2,
                        _ => false
                    };
                    if (inside)
                    {
                        pixels[(y * width) + x] = 225;
                    }
                }
            }
        }
        return new(pixels, width, height, width);
    }

    public static void WritePng(string path, GrayImage image)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            image.Pixels.Span.Slice(y * image.Stride, image.Width).CopyTo(pixels.AsSpan(y * image.Width));
        }
        using var bitmap = Image.NewFromMemory(pixels, image.Width, image.Height, 1, Enums.BandFormat.Uchar);
        bitmap.WriteToFile(path);
    }
}
