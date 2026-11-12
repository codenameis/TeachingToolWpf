using System.Diagnostics;
using VisionKernels;

namespace CpuMatching.Core;

public static partial class PyramidSearch
{
    /// <summary>연속 버퍼 경로의 예산. 더 큰 원본은 FindWindowed로 검색한다.</summary>
    public const int MaximumSearchPixels = 8192 * 8192;

    private const int MaximumTemplateSidePixels = 512;
    private const int MinimumPyramidTemplateSidePixels = 24;
    private const int MinimumPyramidImageSidePixels = 32;
    private const int FullFrameScorePixelBudget = 2_097_152; // [Math Why]: 2Mpx 이하 레벨은 전체 프레임을 한 번에 준비하고, 초과 시 타일 맵으로 분할한다.
    private const int PoseCachePixelBudget = 65_536;
    private const int MinimumPoseCacheCapacity = 1;
    private const int MaximumPoseCacheCapacity = 128;
    private const int LocalTileSafetyMarginPixels = 36; // [Math Why]: 최대 거리 전파 반경(32px) + Sobel/봉우리 3x3 이웃 여백(4px)을 합산한다.
    private const double CoarseScoreFloor = 0.2;
    private const double CoarseScoreSlack = 0.25;
    private const int ParallelRowPixelThreshold = 16_384;
    private const int ParallelCandidateCountThreshold = 4;
    private const int LocalEdgeRefinementMarginPixels = 12;
    private const double FullRotationSpanDegrees = 360.0;
    private const double UnboundedAngleSuppressionDegrees = 361.0; // [Math Why]: Wrap 거리 상한(180°)보다 크게 두어 최종 NMS에서 모든 각도를 공간 반경만으로 억제한다.
    private const double UnboundedScaleSuppressionDelta = 10.0; // [Math Why]: 허용 스케일 차이 상한을 넘어 최종 NMS에서 모든 스케일을 공간 반경만으로 억제한다.

    /// <summary>동일 타일 공유는 prepare가 템플릿과 입력 픽셀만으로 읽기 전용 점수 맵을 만들 때만 허용한다.</summary>
    public static SearchResult Find(CpuGrayImage template, CpuGrayImage image, CpuSearchOptions options,
        Func<CpuGrayImage, CpuGrayImage, double, double, bool, IScoreLevel> prepare, CancellationToken token,
        PyramidSearchPlan? plan = null,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prepare);
        var activePlan = plan ?? PyramidSearchPlan.Default;
        options.Validate();
        token.ThrowIfCancellationRequested();
        var region = options.Region ?? new CpuSearchRegion(0, 0, image.Width, image.Height);
        region.Validate(image);
        if ((long)region.Width * region.Height > MaximumSearchPixels ||
            template.Width > MaximumTemplateSidePixels ||
            template.Height > MaximumTemplateSidePixels)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Search region must be <=8192x8192 pixels; template <=512px per side.");
        }
        // 작게 줄인 영상부터 시작하고, 후보만 원본까지 따라 올라간다.
        var timer = Stopwatch.StartNew();
        var searchImage = image.Crop(region);
        var original = new SearchImage(searchImage);
        var (images, levels) = PreparePyramid(template, searchImage, options, prepare,
            activePlan.RefineCoarsePositions, activePlan.ReuseIdenticalTiles, token);
        var preparationMs = timer.Elapsed.TotalMilliseconds;
        long evaluations = 0;
        var refinementMs = 0d;
        var verificationMs = 0d;
        var refined = new List<Match>();
        var coarse = VisitCoarseCandidates(images[^1], levels[^1], options, accelerator, token, candidates =>
        {
            timer.Restart();
            var (current, angleRadius) = RefinePyramid(levels, candidates, options, activePlan.RefineCoarsePositions, token, ref evaluations);
            refinementMs += timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            refined.AddRange(VerifyOriginal(template, original, levels[0], current, region, options,
                angleRadius, activePlan.RefineEdges, acceptCenter, token, ref evaluations));
            verificationMs += timer.Elapsed.TotalMilliseconds;
        });
        evaluations += coarse.Evaluations;
        timer.Restart();
        var final = Suppress(refined, options.SuppressionRadius, UnboundedAngleSuppressionDegrees, UnboundedScaleSuppressionDelta);
        return new SearchResult(final.Take(options.MaximumMatches).ToArray(), false, final.Count > options.MaximumMatches, evaluations)
        {
            CandidateCount = coarse.Candidates,
            CandidateBatches = coarse.Batches,
            Timings = new(preparationMs, coarse.Milliseconds, refinementMs, verificationMs, timer.Elapsed.TotalMilliseconds)
        };
    }

    private static (List<CpuGrayImage> Images, IScoreLevel[] Levels) PreparePyramid(CpuGrayImage template, CpuGrayImage image,
        CpuSearchOptions options, Func<CpuGrayImage, CpuGrayImage, double, double, bool, IScoreLevel> prepare, bool denseIntermediate,
        bool reuseIdenticalTiles, CancellationToken token)
    {
        var images = new List<CpuGrayImage> { image };
        var templates = new List<CpuGrayImage> { template };
        while (images.Count < options.PyramidLevels &&
            Math.Min(templates[^1].Width, templates[^1].Height) >= MinimumPyramidTemplateSidePixels &&
            Math.Min(images[^1].Width, images[^1].Height) >= MinimumPyramidImageSidePixels)
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
            var capacity = Math.Clamp(PoseCachePixelBudget / (templates[level].Width * templates[level].Height),
                MinimumPoseCacheCapacity, MaximumPoseCacheCapacity);
            var levelTemplate = templates[level];
            var fine = level == 0 || (denseIntermediate && level < images.Count - 1);
            IScoreLevel Prepare(CpuGrayImage scene) => new CachedLevel(prepare(levelTemplate, scene,
                (template.Width / (2.0 * factor)) - 0.5, (template.Height / (2.0 * factor)) - 0.5, fine), capacity);
            if (level < levels.Length - 1 && (long)images[level].Width * images[level].Height > FullFrameScorePixelBudget)
            {
                // 최대 템플릿 반경과 거리 맵의 최대 전파 거리(32px), 미분 여백을 함께 보존한다.
                var margin = (int)Math.Ceiling(Math.Sqrt((levelTemplate.Width * levelTemplate.Width) +
                    (levelTemplate.Height * levelTemplate.Height)) * options.MaximumScale / 2) + LocalTileSafetyMarginPixels;
                levels[level] = new CachedLevel(new LocalScoreLevel(images[level], margin, Prepare, token, reuseIdenticalTiles), capacity);
            }
            else
            {
                levels[level] = Prepare(images[level]);
            }
        }
        return (images, levels);
    }

    private static (long Evaluations, long Candidates, int Batches, double Milliseconds) VisitCoarseCandidates(CpuGrayImage image, IScoreLevel level,
        CpuSearchOptions options, ICoarseScoreAccelerator? accelerator, CancellationToken token, Action<List<Match>> inspect)
    {
        var candidates = new List<Match>(options.CandidateLimit);
        var timer = Stopwatch.StartNew();
        long evaluations = 0;
        long count = 0;
        var batches = 0;
        // 조잡한 레벨의 점수는 최종 합격 점수가 아니다. 최종 원본 점수는 그대로 유지한다.
        var coarseThreshold = Math.Max(CoarseScoreFloor, options.MinimumScore - CoarseScoreSlack);
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
                ScoreGrid(scorer, scores, width, height, level.SupportsParallelEvaluation, options.MaximumDegreeOfParallelism, accelerator, token);
                evaluations += scores.Length;
                for (var y = 1; y < height - 1; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (var x = 1; x < width - 1; x++)
                    {
                        var score = scores[(y * width) + x];
                        if (score < coarseThreshold || !IsPeak(scores, width, x, y))
                        {
                            continue;
                        }
                        candidates.Add(new Match(x, y, angle, scale, score));
                        count++;
                        if (candidates.Count == options.CandidateLimit)
                        {
                            InspectBatch();
                        }
                    }
                }
            }
        }
        if (candidates.Count > 0)
        {
            InspectBatch();
        }
        return (evaluations, count, batches, timer.Elapsed.TotalMilliseconds);

        void InspectBatch()
        {
            // 상한은 대기 후보의 묶음 크기다. 가득 차면 검사하고 계속 탐색하며 후보를 버리지 않는다.
            timer.Stop();
            inspect(candidates);
            candidates.Clear();
            batches++;
            timer.Start();
        }
    }

    private static void ScoreGrid(IPoseScore scorer, double[] scores, int width, int height,
        bool supportsParallel, int workers, ICoarseScoreAccelerator? accelerator, CancellationToken token)
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
        // 행 내부의 누적 순서는 유지하고, 독립 버퍼를 쓰는 행만 병렬로 계산한다.
        var independentRows = scorer is not IRowScore row || row.SupportsParallelRows;
        if (workers > 1 && supportsParallel && independentRows && scores.Length >= ParallelRowPixelThreshold)
        {
            Parallel.For(0, height, ParallelOptions(workers, token), ScoreRow);
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
        CpuSearchOptions options, bool refineCoarsePositions, CancellationToken token, ref long evaluations, bool skipOriginal = false)
    {
        var top = levels.Length - 1;
        var angleRadius = options.AngleStep;
        // NCC의 가장 작은 영상에서는 픽셀 위상으로 이웃 봉우리 순위가 뒤집힐 수 있다.
        // 그 점수로 자세를 다시 이동시키지 않고, 더 큰 영상의 전체 템플릿으로 보정한다.
        var startAtFinerLevel = refineCoarsePositions && top > 0;
        // 축소 점수의 순위로 다른 위치를 버리지 않는다. 같은 자세만 합치고
        // 공간 중복 제거는 모든 후보의 원본 검증이 끝난 뒤에 수행한다.
        var current = candidates.ToList();
        for (var level = startAtFinerLevel ? top - 1 : top; level >= (skipOriginal ? 1 : 0); level--)
        {
            var next = new Match[current.Count];
            var counts = new long[current.Count];
            ForEachCandidate(current, levels[level], options.MaximumDegreeOfParallelism, token, index =>
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
            current = next.DistinctBy(match => (match.X, match.Y, match.AngleDegrees, match.Scale)).ToList();
        }
        return (current, angleRadius);
    }

    private static List<Match> VerifyOriginal(CpuGrayImage template, SearchImage image, IScoreLevel level, List<Match> candidates,
        CpuSearchRegion region, CpuSearchOptions options, double angleRadius, bool refineEdges,
        Func<double, double, bool>? acceptCenter, CancellationToken token, ref long evaluations)
    {
        var checkedMatches = new Match[candidates.Count];
        var counts = new long[candidates.Count];
        var refiner = CreateOriginalRefiner(template, image, refineEdges);
        ForEachCandidate(candidates, level, options.MaximumDegreeOfParallelism, token, index =>
        {
            checkedMatches[index] = VerifyPose(template, image, level, candidates[index], options, angleRadius,
                refineEdges, refiner, ref counts[index]);
        });
        evaluations += counts.Sum();
        return FilterOriginalMatches(checkedMatches, region, options, acceptCenter, token);
    }

    private static EdgePoseRefiner? CreateOriginalRefiner(CpuGrayImage template, SearchImage image, bool refineEdges)
        => refineEdges && (long)image.Width * image.Height <= FullFrameScorePixelBudget ? new EdgePoseRefiner(template, image.ReadAll()) : null;

    private static Match VerifyPose(CpuGrayImage template, SearchImage image, IScoreLevel level, Match seed, CpuSearchOptions options,
        double angleRadius, bool refineEdges, EdgePoseRefiner? refiner, ref long evaluations)
    {
        var match = Refine(level, seed, options, angleRadius, 0.5, 1, ref evaluations);
        match = Refine(level, match, options, 0.5, 0.25, 1, ref evaluations);
        if (refiner is not null)
        {
            match = refiner.Refine(match, options);
        }
        else if (refineEdges)
        {
            match = RefineLocalEdges(template, image, match, options);
        }
        return match with
        {
            Score = level.Verify(match)
        };
    }

    private static List<Match> FilterOriginalMatches(Match[] checkedMatches, CpuSearchRegion region, CpuSearchOptions options,
        Func<double, double, bool>? acceptCenter, CancellationToken token)
    {
        CpuSearchDiagnostics.Write("Verified", checkedMatches);
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

    private static Match RefineLocalEdges(CpuGrayImage template, SearchImage image, Match match, CpuSearchOptions options)
    {
        var radius = Math.Ceiling(Math.Sqrt((template.Width * template.Width) + (template.Height * template.Height)) * options.MaximumScale / 2) + LocalEdgeRefinementMarginPixels;
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
    private static void ForEachCandidate(IReadOnlyList<Match> candidates, IScoreLevel level, int workers, CancellationToken token, Action<int> evaluate)
    {
        var count = candidates.Count;
        if (count >= ParallelCandidateCountThreshold && level.SupportsParallelEvaluation)
        {
            // 같은 지역의 맵을 연달아 사용하되 출력은 원래 후보 번호에 저장한다.
            var keys = new long[count];
            var order = new int[count];
            for (var i = 0; i < count; i++)
            {
                var ty = (int)Math.Floor(candidates[i].Y / LocalScoreLevel.CellSize);
                var tx = (int)Math.Floor(candidates[i].X / LocalScoreLevel.CellSize);
                keys[i] = ((long)ty << 32) | (uint)tx;
                order[i] = i;
            }
            Array.Sort(keys, order);
            Parallel.For(0, count, ParallelOptions(workers, token), index => evaluate(order[index]));
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

    private static ParallelOptions ParallelOptions(int workers, CancellationToken token) => new()
    {
        MaxDegreeOfParallelism = workers,
        CancellationToken = token
    };

    public static double Wrap(double angle) => AngleMath.WrapDegrees180(angle);

    public static double AngleDistance(double left, double right) => AngleMath.DistanceDegrees(left, right);

    private sealed class CachedLevel(IScoreLevel inner, int capacity) : IScoreLevel
    {
        private readonly Dictionary<(double Angle, double Scale), IPoseScore> _poses = new();
        private readonly Queue<(double Angle, double Scale)> _order = new();
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
                if (_poses.Count == capacity)
                {
                    _poses.Remove(_order.Dequeue());
                }
                // 처음 본 자세만 영구 보관하면 이후 자세는 매 점수 계산마다 다시 만들어진다.
                _poses.Add(key, pose);
                _order.Enqueue(key);
                return pose;
            }
        }

        public double Verify(Match match) => inner.Verify(match);
    }

    private static Match Refine(IScoreLevel level, Match seed, CpuSearchOptions options, double angleRadius,
        double positionStep, int positionRadius, ref long evaluations)
    {
        var best = seed with
        {
            Score = -1
        };
        var scaleRadius = options.MinimumScale == options.MaximumScale ? 0 : options.ScaleStep / 2;
        Span<double> scaleBuffer = stackalloc double[5];
        var scaleCount = FillSamples(Math.Max(options.MinimumScale, seed.Scale - scaleRadius),
            Math.Min(options.MaximumScale, seed.Scale + scaleRadius), Math.Max(0.01, scaleRadius), scaleBuffer);
        var fullRotation = options.MaximumAngle - options.MinimumAngle == FullRotationSpanDegrees;
        Span<double> angleBuffer = stackalloc double[5];
        var angleCount = FillSamples(fullRotation ? seed.AngleDegrees - angleRadius : Math.Max(options.MinimumAngle, seed.AngleDegrees - angleRadius),
            fullRotation ? seed.AngleDegrees + angleRadius : Math.Min(options.MaximumAngle, seed.AngleDegrees + angleRadius), Math.Max(0.25, angleRadius / 2), angleBuffer);

        for (var si = 0; si < scaleCount; si++)
        {
            var scale = scaleBuffer[si];
            for (var ai = 0; ai < angleCount; ai++)
            {
                var angle = angleBuffer[ai];
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

    private static int FillSamples(double minimum, double maximum, double step, Span<double> buffer)
        => RangeSampler.FillSamples(minimum, maximum, step, buffer);

    private static IEnumerable<double> Samples(double minimum, double maximum, double step)
        => RangeSampler.EnumerateSamples(minimum, maximum, step);

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
            if (!IsSuppressed(match, result, radius, angleRadius, scaleRadius))
            {
                result.Add(match);
            }
        }
        return result;
    }

    private static bool IsSuppressed(Match match, List<Match> existing, double radius, double angleRadius, double scaleRadius)
    {
        var radiusSquared = radius * radius;
        for (var i = 0; i < existing.Count; i++)
        {
            var other = existing[i];
            if (Math.Abs(match.Scale - other.Scale) <= scaleRadius &&
                AngleDistance(match.AngleDegrees, other.AngleDegrees) <= angleRadius)
            {
                var dx = match.X - other.X;
                var dy = match.Y - other.Y;
                if ((dx * dx) + (dy * dy) < radiusSquared)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
