// 튜닝 전 삼각형 경로. 성능 실험과 픽셀 회귀 검증의 고정 기준이다.
using System.Numerics;
using SkiaSharp;
using VisionCanvas.Rendering;
using VisionCanvas.Rendering.Abstractions;
using VisionCanvas.Skia.Rendering;

namespace VisionCanvas.Skia.Tests;

/// <summary>공통 레이어·핸들·미니맵 배치의 순서와 UV를 Skia 삼각형으로 표시한다.</summary>
internal sealed class ReferenceSprites : IDisposable
{
    private const int MaximumRetainedVertices = 65_536;

    private readonly SKPaint _paint = new();

    private readonly Dictionary<int, (SKPoint[] Points, SKPoint[] Coordinates, SKColor[] Colors)> _buffers = [];
    private int _retainedVertices;

    public void Draw(SKCanvas canvas, SpriteMesh mesh, int first, int end, double width, double height)
    {
        for (var index = first; index < end; index++)
        {
            var batch = mesh.Batches[index];
            var texture = (SkiaTexture)batch.Texture;
            var (points, coordinates, colors) = GetBuffers(batch.Count);
            for (var vertexIndex = 0; vertexIndex < batch.Count; vertexIndex++)
            {
                var vertex = mesh.Vertices[batch.Start + vertexIndex];
                points[vertexIndex] = new((float)((vertex.Position.X + 1) * width / 2),
                    (float)((1 - vertex.Position.Y) * height / 2));
                coordinates[vertexIndex] = new(vertex.Uv.X * texture.Width, vertex.Uv.Y * texture.Height);
                colors[vertexIndex] = Color(vertex.Color);
            }
            using var shader = texture.Bitmap.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(batch.PointSampling ? SKFilterMode.Nearest : SKFilterMode.Linear));
            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, points, coordinates, colors);
            _paint.Shader = shader;
            canvas.DrawVertices(vertices, SKBlendMode.Modulate, _paint);
            _paint.Shader = null;
        }
    }

    private (SKPoint[] Points, SKPoint[] Coordinates, SKColor[] Colors) GetBuffers(int count)
    {
        if (_buffers.TryGetValue(count, out var existing))
        {
            return existing;
        }
        var buffers = (new SKPoint[count], new SKPoint[count], new SKColor[count]);
        if (count <= MaximumRetainedVertices)
        {
            if (_retainedVertices + count > MaximumRetainedVertices || _buffers.Count >= 64)
            {
                _buffers.Clear();
                _retainedVertices = 0;
            }
            _buffers.Add(count, buffers);
            _retainedVertices += count;
        }
        return buffers;
    }

    internal static SKColor Color(Vector4 value) => new(
        (byte)Math.Clamp((int)Math.Round(value.X * 255), 0, 255),
        (byte)Math.Clamp((int)Math.Round(value.Y * 255), 0, 255),
        (byte)Math.Clamp((int)Math.Round(value.Z * 255), 0, 255),
        (byte)Math.Clamp((int)Math.Round(value.W * 255), 0, 255));

    public void Dispose()
    {
        _buffers.Clear();
        _paint.Dispose();
    }
}
