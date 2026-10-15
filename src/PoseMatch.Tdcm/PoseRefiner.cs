namespace PoseMatch.Tdcm;

/// <summary>논문의 명암 코사인 유사도 기반 각도/X/Y 배율 보정. C# 역방향 bilinear 샘플링을 사용한다.</summary>
internal static class PoseRefiner
{
    public static PoseMatchResult Refine(PixelImage image, PixelImage template, PoseMatchResult initial, CancellationToken token)
    {
        var current = SearchAngle(initial, 20, 2);
        if (Similarity(current) < .9)
        {
            current = SearchAngle(initial, 180, 10);
        }

        current = SearchAngle(current, 5, .5);
        var best = current;
        var score = Similarity(best);
        for (var step = -7; step <= 7; step++)
        {
            var candidate = current with
            {
                Width = current.Width + step * .03 * 36
            };
            Try(candidate);
        }
        if (score >= .92)
        {
            current = best;
        }

        best = current;
        score = Similarity(best);
        for (var step = -10; step <= 10; step++)
        {
            var candidate = current with
            {
                Height = current.Height + step * .015 * 36
            };
            Try(candidate);
        }
        if (score >= .92)
        {
            current = best;
        }

        current = SearchAngle(current, 5, .5);
        return current with
        {
            AngleDegrees = ((current.AngleDegrees + 540) % 360) - 180
        };

        PoseMatchResult SearchAngle(PoseMatchResult pose, double range, double step)
        {
            var result = pose;
            var maximum = Similarity(pose);
            for (var angle = pose.AngleDegrees - range; angle <= pose.AngleDegrees + range; angle += step)
            {
                token.ThrowIfCancellationRequested();
                var candidate = pose with
                {
                    AngleDegrees = angle
                };
                var value = Similarity(candidate);
                if (value > maximum)
                {
                    maximum = value;
                    result = candidate;
                }
            }
            return result;
        }

        void Try(PoseMatchResult candidate)
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Width is <= 10.8 or >= 108 || candidate.Height is <= 10.8 or >= 108)
            {
                return;
            }

            var value = Similarity(candidate);
            if (value > score)
            {
                score = value;
                best = candidate;
            }
        }

        double Similarity(PoseMatchResult pose)
        {
            var radians = pose.AngleDegrees * Math.PI / 180;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            double dot = 0, aa = 0, bb = 0;
            for (var y = 0; y < 36; y++)
            {
                for (var x = 0; x < 36; x++)
                {
                    var localX = (x + .5 - 18) * pose.Width / 36;
                    var localY = (y + .5 - 18) * pose.Height / 36;
                    var px = pose.X + localX * cosine - localY * sine;
                    var py = pose.Y + localX * sine + localY * cosine;
                    if (px < 0 || py < 0 || px > image.Width - 1 || py > image.Height - 1)
                    {
                        return 0;
                    }

                    for (var c = 0; c < 3; c++)
                    {
                        var a = Sample(template, (x + .5) * template.Width / 36 - .5, (y + .5) * template.Height / 36 - .5, c);
                        var b = Sample(image, px, py, c);
                        dot += a * b;
                        aa += a * a;
                        bb += b * b;
                    }
                }
            }

            return aa > 0 && bb > 0 ? dot / Math.Sqrt(aa * bb) : 0;
        }
    }

    private static double Sample(PixelImage image, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var c = image.Channels == 1 ? 0 : channel;
        var pixels = image.Pixels.Span;
        return (pixels[y0 * image.Stride + x0 * image.Channels + c] * (1 - fx) + pixels[y0 * image.Stride + x1 * image.Channels + c] * fx) * (1 - fy) +
            (pixels[y1 * image.Stride + x0 * image.Channels + c] * (1 - fx) + pixels[y1 * image.Stride + x1 * image.Channels + c] * fx) * fy;
    }
}
