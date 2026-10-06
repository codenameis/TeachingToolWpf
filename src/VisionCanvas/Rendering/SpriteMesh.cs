using System.Numerics;
using System.Runtime.InteropServices;
using VisionGeometry;

namespace VisionCanvas.Rendering;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct SpriteVertex(Vector2 Position, Vector2 Uv, Vector4 Color);
internal readonly record struct SpriteBatch(GpuTexture Texture, int Start, int Count, bool PointSampling);

internal sealed class SpriteMesh
{
    public List<SpriteVertex> Vertices
    {
        get;
    } = new(16384);

    public List<SpriteBatch> Batches
    {
        get;
    } = new(256);

    private double _width;

    private double _height;
    private bool _split;

    public void Begin(double width, double height)
    {
        _width = width;
        _height = height;
        Vertices.Clear();
        Batches.Clear();
        _split = false;
    }

    public int Split()
    {
        _split = true;
        return Batches.Count;
    }

    private Vector2 Ndc(PointD point) => new((float)((point.X * 2 / _width) - 1), (float)(1 - (point.Y * 2 / _height)));

    public void Quad(GpuTexture texture, PointD a, PointD b, PointD c, PointD d, Vector4 color, bool pointSampling = false, BoundsD? uv = null)
    {
        var start = Vertices.Count;
        var t = uv ?? new BoundsD(0, 0, 1, 1);
        var v0 = new SpriteVertex(Ndc(a), new((float)t.Left, (float)t.Top), color);
        var v1 = new SpriteVertex(Ndc(b), new((float)t.Right, (float)t.Top), color);
        var v2 = new SpriteVertex(Ndc(c), new((float)t.Right, (float)t.Bottom), color);
        var v3 = new SpriteVertex(Ndc(d), new((float)t.Left, (float)t.Bottom), color);
        Vertices.Add(v0);
        Vertices.Add(v1);
        Vertices.Add(v2);
        Vertices.Add(v0);
        Vertices.Add(v2);
        Vertices.Add(v3);
        if (!_split && Batches.Count > 0 && Batches[^1].Texture == texture && Batches[^1].PointSampling == pointSampling)
        {
            Batches[^1] = Batches[^1] with
            {
                Count = Batches[^1].Count + 6
            };
        }
        else
        {
            Batches.Add(new(texture, start, 6, pointSampling));
        }

        _split = false;
    }

    public void Rectangle(GpuTexture texture, BoundsD bounds, Vector4 color, bool pointSampling = false)
        => Quad(texture, new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom), color, pointSampling);

    public void Line(GpuTexture white, PointD a, PointD b, double thickness, Vector4 color)
    {
        var delta = b - a;
        if (delta.Length < 0.001)
        {
            return;
        }

        var normal = new PointD(-delta.Y, delta.X) * (thickness / (2 * delta.Length));
        Quad(white, a + normal, b + normal, b - normal, a - normal, color);
    }

    public void Outline(GpuTexture white, IReadOnlyList<PointD> points, double thickness, Vector4 color)
    {
        for (var i = 0; i < points.Count; i++)
        {
            Line(white, points[i], points[(i + 1) % points.Count], thickness, color);
        }
    }

    public void Circle(GpuTexture white, PointD center, double radius, Vector4 color)
    {
        const int Segments = 24;
        var start = Vertices.Count;
        for (var i = 0; i < Segments; i++)
        {
            var a = center + new PointD(radius, 0).Rotate(i * 360d / Segments);
            var b = center + new PointD(radius, 0).Rotate((i + 1) * 360d / Segments);
            Vertices.Add(new(Ndc(center), Vector2.Zero, color));
            Vertices.Add(new(Ndc(a), Vector2.Zero, color));
            Vertices.Add(new(Ndc(b), Vector2.Zero, color));
        }
        if (!_split && Batches.Count > 0 && Batches[^1].Texture == white && !Batches[^1].PointSampling)
        {
            Batches[^1] = Batches[^1] with
            {
                Count = Batches[^1].Count + (Segments * 3)
            };
        }
        else
        {
            Batches.Add(new(white, start, Segments * 3, false));
        }

        _split = false;
    }
}
