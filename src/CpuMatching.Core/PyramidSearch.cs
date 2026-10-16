using System.Diagnostics;

namespace CpuMatching.Core;

public static class PyramidSearch
{
    public const int MaximumSearchPixels = 8192 * 8192;
    public static SearchResult Find(GrayImage template, GrayImage image, SearchOptions options,
        Func<GrayImage, GrayImage, double, double, bool, IScoreLevel> prepare, CancellationToken token, bool refineEdges = false,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null, bool refineCoarsePositions = false)
    {
        options.Validate();
        token.ThrowIfCancellationRequested();
        var region = options.Region ?? new SearchRegion(0, 0, image.Width, image.Height);
        region.Validate(image);
        if ((long)region.Width * region.Height > MaximumSearchPixels || template.Width > 512 || template.Height > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Search region must be <=8192x8192 pixels; template <=512px per side.");
        }
        // 작게 줄인 영상부터 시작하고, 후보만 원본까지 따라 올라간다.
        var timer = Stopwatch.StartNew();
        var searchImage = image.Crop(region);
        var (images, levels) = PreparePyramid(template, searchImage, options, prepare, refineCoarsePositions, token);
        var preparationMs = timer.Elapsed.TotalMilliseconds;
        long evaluations = 0;
        timer.Restart();
        var (candidates, truncated) = FindCoarseCandidates(images[^1], levels[^1], options, accelerator, token, ref evaluations);
        var coarseMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var (current, angleRadius) = RefinePyramid(levels, candidates, options, refineCoarsePositions, token, ref evaluations);
        var refinementMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var refined = VerifyOriginal(template, searchImage, levels[0], current, region, options,
            angleRadius, refineEdges, acceptCenter, token, ref evaluations);
        var verificationMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var final = Suppress(refined, options.SuppressionRadius, 361, 10);
        return new SearchResult(final.Take(options.MaximumMatches).ToArray(), truncated, final.Count > options.MaximumMatches, evaluations)
        {
            Timings = new(preparationMs, coarseMs, refinementMs, verificationMs, timer.Elapsed.TotalMilliseconds)
        };
    }

    private static (List<GrayImage> Images, IScoreLevel[] Levels) PreparePyramid(GrayImage template, GrayImage image,
        SearchOptions options, Func<GrayImage, GrayImage, double, double, bool, IScoreLevel> prepare, bool denseIntermediate, CancellationToken token)
    {
        var images = new List<GrayImage> { image };
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
            // 큰 NCC 템플릿은 pose당 표본 배열도 크다. 한 검사/레벨 안에서만 제한해 재사용한다.
            var capacity = Math.Clamp(65_536 / (templates[level].Width * templates[level].Height), 1, 128);
            var levelTemplate = templates[level];
            var fine = level == 0 || (denseIntermediate && level < images.Count - 1);
            IScoreLevel Prepare(GrayImage scene) => new CachedLevel(prepare(levelTemplate, scene,
                (template.Width / (2.0 * factor)) - 0.5, (template.Height / (2.0 * factor)) - 0.5, fine), capacity);
            if (level < levels.Length - 1 && (long)images[level].Width * images[level].Height > 2_097_152)
            {
                // 최대 템플릿 반경과 거리 맵의 최대 전파 거리(32px), 미분 여백을 함께 보존한다.
                var margin = (int)Math.Ceiling(Math.Sqrt((levelTemplate.Width * levelTemplate.Width) +
                    (levelTemplate.Height * levelTemplate.Height)) * options.MaximumScale / 2) + 36;
                levels[level] = new CachedLevel(new LocalScoreLevel(images[level], margin, Prepare, token), capacity);
            }
            else
            {
                levels[level] = Prepare(images[level]);
            }
        }
        return (images, levels);
    }

    private static (IEnumerable<Match> Candidates, bool Truncated) FindCoarseCandidates(GrayImage image, IScoreLevel level,
        SearchOptions options, ICoarseScoreAccelerator? accelerator, CancellationToken token, ref long evaluations)
    {
        var candidates = new PriorityQueue<Match, double>();
        var truncated = false;
        // 조잡한 레벨의 점수는 최종 합격 점수가 아니다. 최종 원본 점수는 그대로 유지한다.
        var coarseThreshold = Math.Max(0.2, options.MinimumScore - 0.25);
        var width = image.Width;
        var height = image.Height;
        // 매 pose에서 모든 행을 덮어쓰므로 같은 응답 버퍼를 재사용할 수 있다.
        var scores = new double[width * height];
        foreach (var scale in Samples(options.MinimumScale, options.MaximumScale, options.ScaleStep))
        {
            foreach (var angle in Samples(options.MinimumAngle, options.MaximumAngle, options.AngleStep))
            {
                token.ThrowIfCancellationRequested();
                var scorer = level.AtPose(angle, scale);
                ScoreGrid(scorer, scores, width, height, level.SupportsParallelEvaluation, accelerator, token);
                evaluations += scores.Length;
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
        return (candidates.UnorderedItems.Select(item => item.Element), truncated);
    }

    private static void ScoreGrid(IPoseScore scorer, double[] scores, int width, int height,
        bool supportsParallel, ICoarseScoreAccelerator? accelerator, CancellationToken token)
    {
        if (accelerator is not null)
        {
            if (scorer is not IAcceleratedPoseScore accelerated)
            {
                throw new NotSupportedException("This score does not support coarse-grid acceleration.");
            }
            accelerated.ScoreGrid(accelerator, scores, token);
            return;
        }
        // 행 버퍼를 가진 SIMD scorer는 순차 실행한다. 독립적인 점수만 행별로 나눈다.
        if (supportsParallel && scorer is not IRowScore && scores.Length >= 16_384)
        {
            Parallel.For(0, height, ParallelOptions(token), ScoreRow);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                ScoreRow(y);
            }
        }

        void ScoreRow(int y)
        {
            if (scorer is IRowScore rowScore)
            {
                rowScore.ScoreRow(y, scores.AsSpan(y * width, width));
                return;
            }
            for (var x = 0; x < width; x++)
            {
                scores[(y * width) + x] = scorer.Score(x, y);
            }
        }
    }

    private static (List<Match> Matches, double AngleRadius) RefinePyramid(IScoreLevel[] levels, IEnumerable<Match> candidates,
        SearchOptions options, bool refineCoarsePositions, CancellationToken token, ref long evaluations)
    {
        var top = levels.Length - 1;
        var angleRadius = options.AngleStep;
        // NCC의 가장 작은 영상에서는 픽셀 위상으로 이웃 봉우리 순위가 뒤집힐 수 있다.
        // 그 점수로 자세를 다시 이동시키지 않고, 더 큰 영상의 전체 템플릿으로 보정한다.
        var startAtFinerLevel = refineCoarsePositions && top > 0;
        var current = Suppress(candidates,
            Math.Max(1, options.SuppressionRadius / (1 << top)), options.AngleStep * 1.5, 0.15);
        for (var level = startAtFinerLevel ? top - 1 : top; level >= 0; level--)
        {
            var next = new Match[current.Count];
            var counts = new long[current.Count];
            ForEachCandidate(current.Count, levels[level], token, index =>
            {
                var candidate = current[index];
                var center = level == top ? candidate : candidate with
                {
                    X = (candidate.X * 2) + 0.5,
                    Y = (candidate.Y * 2) + 0.5
                };
                var firstFinerLevel = startAtFinerLevel && level == top - 1;
                next[index] = Refine(levels[level], center, options, angleRadius, 1,
                    firstFinerLevel ? 6 : 2, ref counts[index]);
            });
            evaluations += counts.Sum();
            angleRadius = Math.Max(0.5, angleRadius / 2);
            current = Suppress(next, Math.Max(1, options.SuppressionRadius / (1 << level)), options.AngleStep, 0.1);
        }
        return (current, angleRadius);
    }

    private static List<Match> VerifyOriginal(GrayImage template, GrayImage image, IScoreLevel level, List<Match> candidates,
        SearchRegion region, SearchOptions options, double angleRadius, bool refineEdges,
        Func<double, double, bool>? acceptCenter, CancellationToken token, ref long evaluations)
    {
        var checkedMatches = new Match[candidates.Count];
        var counts = new long[candidates.Count];
        var localEdges = (long)image.Width * image.Height > 2_097_152;
        var refiner = refineEdges && !localEdges ? new EdgePoseRefiner(template, image) : null;
        ForEachCandidate(candidates.Count, level, token, index =>
        {
            var match = Refine(level, candidates[index], options, angleRadius, 0.5, 1, ref counts[index]);
            match = Refine(level, match, options, 0.5, 0.25, 1, ref counts[index]);
            if (refiner is not null)
            {
                match = refiner.Refine(match, options);
            }
            else if (refineEdges)
            {
                // 전체 탐색에서 선택된 후보는 모두 보정한다. 8K 전체의 희박한 경계 배열만 피한다.
                match = RefineLocalEdges(template, image, match, options);
            }
            checkedMatches[index] = match with
            {
                Score = level.Verify(match)
            };
        });
        evaluations += counts.Sum();
        var refined = new List<Match>();
        foreach (var match in checkedMatches)
        {
            token.ThrowIfCancellationRequested();
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
        return refined;
    }

    private static Match RefineLocalEdges(GrayImage template, GrayImage image, Match match, SearchOptions options)
    {
        var radius = Math.Ceiling(Math.Sqrt((template.Width * template.Width) + (template.Height * template.Height)) * options.MaximumScale / 2) + 12;
        var left = Math.Max(0, (int)Math.Floor(match.X - radius));
        var top = Math.Max(0, (int)Math.Floor(match.Y - radius));
        var right = Math.Min(image.Width, (int)Math.Ceiling(match.X + radius));
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(match.Y + radius));
        var refiner = new EdgePoseRefiner(template, image.Crop(new(left, top, right - left, bottom - top)));
        var refined = refiner.Refine(match with
        {
            X = match.X - left,
            Y = match.Y - top
        }, options);
        return refined with
        {
            X = refined.X + left,
            Y = refined.Y + top
        };
    }

    // 결과를 원래 후보 번호에 저장하므로 완료 순서가 동점 판정이나 NMS 순서를 바꾸지 않는다.
    private static void ForEachCandidate(int count, IScoreLevel level, CancellationToken token, Action<int> evaluate)
    {
        if (count >= 4 && level.SupportsParallelEvaluation)
        {
            Parallel.For(0, count, ParallelOptions(token), evaluate);
        }
        else
        {
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                evaluate(index);
            }
        }
    }

    private static ParallelOptions ParallelOptions(CancellationToken token) => new()
    {
        MaxDegreeOfParallelism = 4,
        CancellationToken = token
    };

    public static double Wrap(double angle) => ((angle + 540) % 360) - 180;

    public static double AngleDistance(double left, double right) => Math.Abs(Wrap(left - right));

    private sealed class CachedLevel(IScoreLevel inner, int capacity) : IScoreLevel
    {
        private readonly Dictionary<(double Angle, double Scale), IPoseScore> _poses = new();
        public bool SupportsParallelEvaluation => inner.SupportsParallelEvaluation;

        public IPoseScore AtPose(double angleDegrees, double scale)
        {
            lock (_poses)
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
