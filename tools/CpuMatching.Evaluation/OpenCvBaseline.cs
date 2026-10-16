using CpuMatching.Core;
using OpenCvSharp;

namespace CpuMatching.Evaluation;

/// <summary>비교용 OpenCV CCOEFF_NORMED. 고정 방향 또는 회전/배율 bank + 국소 각도 보정.</summary>
internal sealed class OpenCvBaseline(GrayImage template, bool rotated) : ITemplateMatcher
{
    public SearchResult Find(GrayImage image, SearchOptions? options = null, CancellationToken cancellationToken = default,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        if (accelerator is not null)
        {
            throw new NotSupportedException("OpenCV baseline is CPU-only.");
        }
        options ??= new SearchOptions();
        options.Validate();
        var region = options.Region ?? new SearchRegion(0, 0, image.Width, image.Height);
        using var scene = Dataset.ToMat(image.Crop(region));
        using var original = Dataset.ToMat(template);
        var candidates = new List<Match>();
        var limited = false;
        var minimum = rotated ? options.MinimumAngle : 0;
        var maximum = rotated ? options.MaximumAngle : 0;
        for (var scale = options.MinimumScale; scale <= options.MaximumScale + 1e-6; scale += options.ScaleStep)
        {
            for (var angle = minimum; angle <= maximum + 1e-6; angle += options.AngleStep)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var transformed = Transform(original, angle, scale);
                if (transformed.Width > scene.Width || transformed.Height > scene.Height)
                {
                    continue;
                }
                using var response = new Mat();
                Cv2.MatchTemplate(scene, transformed, response, TemplateMatchModes.CCoeffNormed);
                for (var i = 0; i <= options.CandidateLimit; i++)
                {
                    Cv2.MinMaxLoc(response, out _, out var score, out _, out var position);
                    if (!double.IsFinite(score) || score < Math.Max(0.2, options.MinimumScore - 0.25))
                    {
                        break;
                    }
                    if (i == options.CandidateLimit)
                    {
                        limited = true;
                        break;
                    }
                    candidates.Add(new Match(position.X + ((transformed.Width - 1) / 2.0), position.Y + ((transformed.Height - 1) / 2.0), angle, scale, score));
                    var radius = (int)options.SuppressionRadius;
                    Cv2.Rectangle(response, new Point(Math.Max(0, position.X - radius), Math.Max(0, position.Y - radius)),
                        new Point(Math.Min(response.Width - 1, position.X + radius), Math.Min(response.Height - 1, position.Y + radius)), Scalar.All(-1), -1);
                }
            }
        }
        var seeds = Suppress(candidates, options.SuppressionRadius);
        var matches = new List<Match>();
        foreach (var seed in seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var best = seed;
            var radius = (int)Math.Ceiling(Math.Sqrt((template.Width * template.Width) + (template.Height * template.Height)) * options.MaximumScale / 2) + 6;
            var left = Math.Max(0, (int)seed.X - radius);
            var top = Math.Max(0, (int)seed.Y - radius);
            var right = Math.Min(scene.Width, (int)seed.X + radius + 1);
            var bottom = Math.Min(scene.Height, (int)seed.Y + radius + 1);
            using var crop = new Mat(scene, new Rect(left, top, right - left, bottom - top));
            var start = rotated ? Math.Max(minimum, seed.AngleDegrees - options.AngleStep) : 0;
            var end = rotated ? Math.Min(maximum, seed.AngleDegrees + options.AngleStep) : 0;
            for (var angle = start; angle <= end + 1e-6; angle += 0.5)
            {
                using var transformed = Transform(original, angle, seed.Scale);
                if (transformed.Width > crop.Width || transformed.Height > crop.Height)
                {
                    continue;
                }
                using var response = new Mat();
                Cv2.MatchTemplate(crop, transformed, response, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(response, out _, out var score, out _, out var position);
                if (score > best.Score)
                {
                    best = new Match(left + position.X + ((transformed.Width - 1) / 2.0),
                        top + position.Y + ((transformed.Height - 1) / 2.0), angle, seed.Scale, score);
                }
            }
            if (best.Score >= options.MinimumScore && (acceptCenter is null || acceptCenter(best.X + region.X, best.Y + region.Y)))
            {
                matches.Add(best with
                {
                    X = best.X + region.X,
                    Y = best.Y + region.Y,
                    AngleDegrees = PyramidSearch.Wrap(best.AngleDegrees)
                });
            }
        }
        var final = Suppress(matches, options.SuppressionRadius);
        return new SearchResult(final.Take(options.MaximumMatches).ToArray(), limited, final.Count > options.MaximumMatches, 0);
    }

    private static Mat Transform(Mat source, double angle, double scale)
    {
        var radians = angle * Math.PI / 180;
        var width = (int)Math.Ceiling(scale * ((Math.Abs(Math.Cos(radians)) * source.Width) + (Math.Abs(Math.Sin(radians)) * source.Height)));
        var height = (int)Math.Ceiling(scale * ((Math.Abs(Math.Sin(radians)) * source.Width) + (Math.Abs(Math.Cos(radians)) * source.Height)));
        using var matrix = Cv2.GetRotationMatrix2D(new Point2f((source.Width - 1) / 2f, (source.Height - 1) / 2f), -angle, scale);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + ((width - source.Width) / 2.0));
        matrix.Set(1, 2, matrix.At<double>(1, 2) + ((height - source.Height) / 2.0));
        var result = new Mat();
        Cv2.WarpAffine(source, result, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(source.At<byte>(0, 0)));
        return result;
    }

    private static List<Match> Suppress(IEnumerable<Match> source, double radius)
    {
        var result = new List<Match>();
        foreach (var match in source.OrderByDescending(match => match.Score))
        {
            if (!result.Any(other => ((other.X - match.X) * (other.X - match.X)) + ((other.Y - match.Y) * (other.Y - match.Y)) < radius * radius))
            {
                result.Add(match);
            }
        }
        return result;
    }
}
