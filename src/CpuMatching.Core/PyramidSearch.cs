namespace CpuMatching.Core;

public static class PyramidSearch
{
    public static SearchResult Find(GrayImage template, GrayImage image, SearchOptions options,
        Func<GrayImage, GrayImage, double, double, bool, IScoreLevel> prepare, CancellationToken token, bool refineEdges = false,
        Func<double, double, bool>? acceptCenter = null)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        var region = options.Region ?? new SearchRegion(0, 0, image.Width, image.Height);
        region.Validate(image);
        if ((long)region.Width * region.Height > 2_097_152 || template.Width > 512 || template.Height > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CPU prototype budget: search region <=2Mpx, template <=512px per side.");
        }
        var images = new List<GrayImage> { image.Crop(region) };
        var templates = new List<GrayImage> { template };
        while (images.Count < options.PyramidLevels && Math.Min(templates[^1].Width, templates[^1].Height) >= 24 &&
            Math.Min(images[^1].Width, images[^1].Height) >= 32)
        {
            token.ThrowIfCancellationRequested();
            images.Add(images[^1].Half());
            templates.Add(templates[^1].Half());
        }
        var levels = new IScoreLevel[images.Count];
        for (var level = 0; level < levels.Length; level++)
        {
            token.ThrowIfCancellationRequested();
            var factor = 1 << level;
            var prepared = prepare(templates[level], images[level], (template.Width / (2.0 * factor)) - 0.5,
                (template.Height / (2.0 * factor)) - 0.5, level == 0);
            // 큰 NCC 템플릿은 pose당 표본 배열도 크다. 한 검사/레벨 안에서만 제한해 재사용한다.
            var capacity = Math.Clamp(65_536 / (templates[level].Width * templates[level].Height), 1, 128);
            levels[level] = new CachedLevel(prepared, capacity);
        }
        var top = levels.Length - 1;
        var candidates = new PriorityQueue<Match, double>();
        var truncated = false;
        long evaluations = 0;
        // 조잡한 레벨의 점수는 최종 합격 점수가 아니다. 최종 원본 점수는 그대로 유지한다.
        var coarseThreshold = Math.Max(0.2, options.MinimumScore - 0.25);
        var width = images[top].Width;
        var height = images[top].Height;
        // 매 pose에서 모든 행을 덮어쓰므로 같은 응답 버퍼를 재사용할 수 있다.
        var scores = new double[width * height];
        foreach (var scale in Samples(options.MinimumScale, options.MaximumScale, options.ScaleStep))
        {
            foreach (var angle in Samples(options.MinimumAngle, options.MaximumAngle, options.AngleStep))
            {
                token.ThrowIfCancellationRequested();
                var scorer = levels[top].AtPose(angle, scale);
                for (var y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    if (scorer is IRowScore rowScore)
                    {
                        rowScore.ScoreRow(y, scores.AsSpan(y * width, width));
                        evaluations += width;
                        continue;
                    }
                    for (var x = 0; x < width; x++)
                    {
                        scores[(y * width) + x] = scorer.Score(x, y);
                        evaluations++;
                    }
                }
                for (var y = 1; y < height - 1; y++)
                {
                    for (var x = 1; x < width - 1; x++)
                    {
                        var score = scores[(y * width) + x];
                        if (score < coarseThreshold || !IsPeak(scores, width, x, y))
                        {
                            continue;
                        }
                        var candidate = new Match(x, y, angle, scale, score);
                        candidates.Enqueue(candidate, score);
                        if (candidates.Count > options.CandidateLimit)
                        {
                            candidates.Dequeue();
                            truncated = true;
                        }
                    }
                }
            }
        }
        var current = Suppress(candidates.UnorderedItems.Select(item => item.Element),
            Math.Max(1, options.SuppressionRadius / (1 << top)), options.AngleStep * 1.5, 0.15);
        var angleRadius = options.AngleStep;
        for (var level = top; level >= 0; level--)
        {
            var next = new List<Match>(current.Count);
            foreach (var candidate in current)
            {
                token.ThrowIfCancellationRequested();
                var center = level == top ? candidate : candidate with
                {
                    X = (candidate.X * 2) + 0.5,
                    Y = (candidate.Y * 2) + 0.5
                };
                next.Add(Refine(levels[level], center, options, angleRadius, 1, 2, ref evaluations));
            }
            angleRadius = Math.Max(0.5, angleRadius / 2);
            current = Suppress(next, Math.Max(1, options.SuppressionRadius / (1 << level)), options.AngleStep, 0.1);
        }
        var refined = new List<Match>();
        var refiner = refineEdges ? new EdgePoseRefiner(template, images[0]) : null;
        foreach (var candidate in current)
        {
            token.ThrowIfCancellationRequested();
            var match = Refine(levels[0], candidate, options, angleRadius, 0.5, 1, ref evaluations);
            match = Refine(levels[0], match, options, 0.5, 0.25, 1, ref evaluations);
            if (refiner is not null)
            {
                match = refiner.Refine(match, options);
            }
            match = match with
            {
                Score = levels[0].Verify(match)
            };
            // 실제 검색 도형의 중심 판정을 출력 제한과 최종 중복 제거보다 먼저 적용한다.
            if (match.Score >= options.MinimumScore && (acceptCenter is null || acceptCenter(match.X + region.X, match.Y + region.Y)))
            {
                refined.Add(match with
                {
                    X = match.X + region.X,
                    Y = match.Y + region.Y,
                    AngleDegrees = Wrap(match.AngleDegrees)
                });
            }
        }
        var final = Suppress(refined, options.SuppressionRadius, 361, 10);
        return new SearchResult(final.Take(options.MaximumMatches).ToArray(), truncated, final.Count > options.MaximumMatches, evaluations);
    }

    public static double Wrap(double angle) => ((angle + 540) % 360) - 180;

    public static double AngleDistance(double left, double right) => Math.Abs(Wrap(left - right));

    private sealed class CachedLevel(IScoreLevel inner, int capacity) : IScoreLevel
    {
        private readonly Dictionary<(double Angle, double Scale), IPoseScore> _poses = new();

        public IPoseScore AtPose(double angleDegrees, double scale)
        {
            var key = (angleDegrees, scale);
            if (_poses.TryGetValue(key, out var pose))
            {
                return pose;
            }
            pose = inner.AtPose(angleDegrees, scale);
            if (_poses.Count < capacity)
            {
                _poses.Add(key, pose);
            }
            return pose;
        }

        public double Verify(Match match) => inner.Verify(match);
    }

    private static Match Refine(IScoreLevel level, Match seed, SearchOptions options, double angleRadius,
        double positionStep, int positionRadius, ref long evaluations)
    {
        var best = seed with
        {
            Score = -1
        };
        var scaleRadius = options.MinimumScale == options.MaximumScale ? 0 : options.ScaleStep / 2;
        foreach (var scale in Samples(Math.Max(options.MinimumScale, seed.Scale - scaleRadius),
            Math.Min(options.MaximumScale, seed.Scale + scaleRadius), Math.Max(0.01, scaleRadius)))
        {
            var fullRotation = options.MaximumAngle - options.MinimumAngle == 360;
            foreach (var angle in Samples(fullRotation ? seed.AngleDegrees - angleRadius : Math.Max(options.MinimumAngle, seed.AngleDegrees - angleRadius),
                fullRotation ? seed.AngleDegrees + angleRadius : Math.Min(options.MaximumAngle, seed.AngleDegrees + angleRadius), Math.Max(0.25, angleRadius / 2)))
            {
                var scorer = level.AtPose(angle, scale);
                for (var dy = -positionRadius; dy <= positionRadius; dy++)
                {
                    for (var dx = -positionRadius; dx <= positionRadius; dx++)
                    {
                        var x = seed.X + (dx * positionStep);
                        var y = seed.Y + (dy * positionStep);
                        var score = scorer.Score(x, y);
                        evaluations++;
                        if (score > best.Score)
                        {
                            best = new Match(x, y, angle, scale, score);
                        }
                    }
                }
            }
        }
        return best;
    }

    private static IEnumerable<double> Samples(double minimum, double maximum, double step)
    {
        var count = (int)Math.Ceiling((maximum - minimum) / step);
        for (var i = 0; i <= count; i++)
        {
            yield return Math.Min(maximum, minimum + (i * step));
        }
    }

    private static bool IsPeak(double[] scores, int width, int x, int y)
    {
        var index = (y * width) + x;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var other = index + (dy * width) + dx;
                if (scores[other] > scores[index] || (other < index && scores[other] == scores[index]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static List<Match> Suppress(IEnumerable<Match> source, double radius, double angleRadius, double scaleRadius)
    {
        var result = new List<Match>();
        foreach (var match in source.OrderByDescending(match => match.Score).ThenBy(match => match.Y).ThenBy(match => match.X))
        {
            if (!result.Any(other => Math.Abs(match.Scale - other.Scale) <= scaleRadius &&
                AngleDistance(match.AngleDegrees, other.AngleDegrees) <= angleRadius &&
                ((match.X - other.X) * (match.X - other.X)) + ((match.Y - other.Y) * (match.Y - other.Y)) < radius * radius))
            {
                result.Add(match);
            }
        }
        return result;
    }
}
