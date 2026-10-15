namespace PoseMatch.Tdcm;

public static class PoseDecoder
{
    public static MatchOutput Decode(Prediction prediction, MatchOptions options, CancellationToken token = default)
    {
        options.Validate();
        var count = checked(prediction.Width * prediction.Height);
        if (prediction.Width < 1 || prediction.Height < 1 || new[] { prediction.Score, prediction.Sign, prediction.Cos, prediction.ScaleX, prediction.ScaleY }.Any(map => map.Length != count || map.Any(value => !float.IsFinite(value))))
        {
            throw new ArgumentException("추론 지도 크기/값이 유효하지 않습니다.", nameof(prediction));
        }

        var peaks = new PriorityQueue<int, (float Score, int Order)>();
        var truncated = false;
        for (var i = 0; i < count; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var score = prediction.Score[i];
            if (score < options.MinimumScore)
            {
                continue;
            }

            var x = i % prediction.Width;
            var y = i / prediction.Width;
            var peak = true;
            for (var dy = -1; dy <= 1 && peak; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= prediction.Width || ny >= prediction.Height)
                    {
                        continue;
                    }

                    var other = ny * prediction.Width + nx;
                    if (prediction.Score[other] > score || (prediction.Score[other] == score && other < i))
                    {
                        peak = false;
                        break;
                    }
                }
            }

            if (!peak)
            {
                continue;
            }

            peaks.Enqueue(i, (score, -i));
            if (peaks.Count > options.MaximumCandidates)
            {
                peaks.Dequeue();
                truncated = true;
            }
        }
        var result = new List<PoseMatchResult>();
        foreach (var i in peaks.UnorderedItems.Select(item => item.Element).OrderByDescending(i => prediction.Score[i]).ThenBy(i => i))
        {
            token.ThrowIfCancellationRequested();
            var x = i % prediction.Width;
            var y = i / prediction.Width;
            double cx = 0, cy = 0;
            var samples = 0;
            var radius = options.CenterRadius;
            for (var yy = Math.Max(0, y - radius); yy <= Math.Min(prediction.Height - 1, y + radius); yy++)
            {
                for (var xx = Math.Max(0, x - radius); xx <= Math.Min(prediction.Width - 1, x + radius); xx++)
                {
                    if ((xx - x) * (xx - x) + (yy - y) * (yy - y) <= radius * radius && prediction.Score[yy * prediction.Width + xx] > .5f)
                    {
                        cx += xx;
                        cy += yy;
                        samples++;
                    }
                }
            }

            if (samples > 0)
            {
                cx /= samples;
                cy /= samples;
            }
            else
            {
                cx = x;
                cy = y;
            }
            if (result.Any(match => Math.Pow(match.X - cx, 2) + Math.Pow(match.Y - cy, 2) < options.SuppressionRadius * options.SuppressionRadius))
            {
                continue;
            }

            if (result.Count == options.MaximumMatches)
            {
                return new(result, truncated, true);
            }

            var angle = -Math.Acos(Math.Clamp(prediction.Cos[i], -1, 1)) * 180 / Math.PI * (prediction.Sign[i] > .5 ? 1 : -1);
            result.Add(new(cx, cy, 36 * prediction.ScaleX[i], 36 * prediction.ScaleY[i], angle, prediction.Score[i]));
        }
        return new(result, truncated, false);
    }
}
