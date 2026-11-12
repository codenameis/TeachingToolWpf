using System.Numerics;
using SkiaSharp;
using VisionCanvas.Rendering.Abstractions;

namespace VisionCanvas.Skia.Rendering;

/// <summary>공통 레이어·핸들·미니맵 배치의 순서와 UV를 Skia 삼각형으로 표시한다.</summary>
internal sealed class SkiaSprites : IDisposable
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
            var texture = SkiaTexturePipeline.GetTexture(in batch);
            if (TryDrawImageRectangle(canvas, mesh, batch, texture, width, height))
            {
                continue;
            }
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

    private bool TryDrawImageRectangle(SKCanvas canvas, SpriteMesh mesh, SpriteBatch batch, SkiaTexture texture, double width, double height)
    {
        // 반투명 텍스트/마스크의 색 결합은 원래 삼각형 경로가 처리한다.
        if (batch.Count != 6 || !texture.IsOpaque)
        {
            return false;
        }
        var topLeft = mesh.Vertices[batch.Start];
        var topRight = mesh.Vertices[batch.Start + 1];
        var bottomRight = mesh.Vertices[batch.Start + 2];
        var bottomLeft = mesh.Vertices[batch.Start + 5];
        if (mesh.Vertices[batch.Start + 3] != topLeft || mesh.Vertices[batch.Start + 4] != bottomRight ||
            topLeft.Color != Vector4.One || topRight.Color != Vector4.One || bottomRight.Color != Vector4.One || bottomLeft.Color != Vector4.One ||
            !IsRectangle(topLeft.Position, topRight.Position, bottomRight.Position, bottomLeft.Position) ||
            !IsRectangle(topLeft.Uv, topRight.Uv, bottomRight.Uv, bottomLeft.Uv))
        {
            return false;
        }
        var left = (float)((topLeft.Position.X + 1) * width / 2);
        var top = (float)((1 - topLeft.Position.Y) * height / 2);
        var right = (float)((bottomRight.Position.X + 1) * width / 2);
        var bottom = (float)((1 - bottomRight.Position.Y) * height / 2);
        var sourceLeft = topLeft.Uv.X * texture.Width;
        var sourceTop = topLeft.Uv.Y * texture.Height;
        var sourceWidth = (bottomRight.Uv.X * texture.Width) - sourceLeft;
        var sourceHeight = (bottomRight.Uv.Y * texture.Height) - sourceTop;
        if (right <= left || bottom <= top || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return false;
        }
        var scaleX = (right - left) / sourceWidth;
        var scaleY = (bottom - top) / sourceHeight;
        // UV의 부분 영역과 기존 Clamp/보간을 그대로 유지한다. DrawImage의 자동 표본 변경은 사용하지 않는다.
        var matrix = SKMatrix.CreateScaleTranslation(scaleX, scaleY, left - (sourceLeft * scaleX), top - (sourceTop * scaleY));
        using var shader = texture.Bitmap.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(batch.PointSampling ? SKFilterMode.Nearest : SKFilterMode.Linear), matrix);
        _paint.Shader = shader;
        canvas.DrawRect(new SKRect(left, top, right, bottom), _paint);
        _paint.Shader = null;
        return true;
    }

    private static bool IsRectangle(Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft)
        => topLeft.X == bottomLeft.X && topRight.X == bottomRight.X && topLeft.Y == topRight.Y && bottomRight.Y == bottomLeft.Y;

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
