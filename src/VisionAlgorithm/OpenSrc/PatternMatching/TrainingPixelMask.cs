namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>최종 pose에서 실제 학습 마스크와 검사 제외 픽셀의 겹침을 확인한다. 픽셀 값을 지우지 않아 가짜 경계를 만들지 않는다.</summary>
internal sealed class TrainingPixelMask(int width, int height, ReadOnlyMemory<byte> mask, ImagePoint reference, double angle)
{
    public long EstimatedBytes => mask.Length;

    public bool Overlaps(PreparedSearchImage image, MatchPose pose, IReadOnlyList<ImagePoint> bounds, CancellationToken token)
    {
        if (image.ExcludedPixels.IsEmpty)
        {
            return false;
        }
        const int SupportRadius = 4;
        var cos = Math.Cos(pose.Angle);
        var sin = Math.Sin(pose.Angle);
        var corners = bounds.Select(point => new ImagePoint(pose.X + (pose.Scale * ((cos * point.X) - (sin * point.Y))),
            pose.Y + (pose.Scale * ((sin * point.X) + (cos * point.Y))))).ToArray();
        var left = Math.Max(0, (int)Math.Floor(corners.Min(point => point.X)) - SupportRadius);
        var top = Math.Max(0, (int)Math.Floor(corners.Min(point => point.Y)) - SupportRadius);
        var right = Math.Min(image.Width, (int)Math.Ceiling(corners.Max(point => point.X)) + SupportRadius);
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(corners.Max(point => point.Y)) + SupportRadius);
        var inverseCos = Math.Cos(angle - pose.Angle) / pose.Scale;
        var inverseSin = Math.Sin(angle - pose.Angle) / pose.Scale;
        for (var y = top; y < bottom; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = left; x < right; x++)
            {
                if (image.ExcludedPixels.Span[(y * image.Width) + x] == 0)
                {
                    continue;
                }
                // Gaussian/Sobel와 에지 보간이 읽는 주변도 유효 영역이어야 한다.
                for (var dy = -SupportRadius; dy <= SupportRadius; dy++)
                {
                    for (var dx = -SupportRadius; dx <= SupportRadius; dx++)
                    {
                        var px = x + .5 + dx - pose.X;
                        var py = y + .5 + dy - pose.Y;
                        var tx = reference.X + (inverseCos * px) - (inverseSin * py);
                        var ty = reference.Y + (inverseSin * px) + (inverseCos * py);
                        if (tx >= 0 && ty >= 0 && tx < width && ty < height &&
                            (mask.IsEmpty || mask.Span[((int)ty * width) + (int)tx] != 0))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }
}
