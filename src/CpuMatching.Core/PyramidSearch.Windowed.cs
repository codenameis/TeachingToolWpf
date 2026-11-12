using System.Collections.Concurrent;
using System.Diagnostics;

namespace CpuMatching.Core;

public static partial class PyramidSearch
{
    private const int MinimumSearchRegionSidePixels = 4;
    private const int WindowedCoarseBandPixelBudget = 1_048_576;

    /// <summary>영상 크기 제한 없이 전역 피라미드 격자를 순회하고 원본 후보 주변만 읽는다.</summary>
    public static SearchResult FindWindowed(CpuGrayImage template, IGrayImageSource source, CpuSearchOptions options,
        Func<CpuGrayImage, CpuGrayImage, double, double, bool, IScoreLevel> prepare, CancellationToken token,
        PyramidSearchPlan? plan = null,
        Func<double, double, bool>? acceptCenter = null, ICoarseScoreAccelerator? accelerator = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prepare);
        var activePlan = plan ?? PyramidSearchPlan.Default;
        options.Validate();
        token.ThrowIfCancellationRequested();
        var region = options.Region ?? new CpuSearchRegion(0, 0, source.Width, source.Height);
        if (region.X < 0 || region.Y < 0 ||
            region.Width < MinimumSearchRegionSidePixels || region.Height < MinimumSearchRegionSidePixels ||
            (long)region.X + region.Width > source.Width || (long)region.Y + region.Height > source.Height ||
            template.Width > MaximumTemplateSidePixels || template.Height > MaximumTemplateSidePixels)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Search region must be inside the image; template <=512px per side.");
        }
        var timer = Stopwatch.StartNew();
        var templates = new List<CpuGrayImage> { template };
        while (templates.Count < options.PyramidLevels &&
            Math.Min(templates[^1].Width, templates[^1].Height) >= MinimumPyramidTemplateSidePixels &&
            Math.Min(region.Width, region.Height) / (1 << (templates.Count - 1)) >= MinimumPyramidImageSidePixels)
        {
            templates.Add(templates[^1].Half());
        }
        var stages = new WindowedPyramidStage[templates.Count];
        var localLevels = new List<LocalScoreLevel>();
        for (var level = 0; level < stages.Length; level++)
        {
            var factor = 1 << level;
            var levelTemplate = templates[level];
            var fine = level == 0 || (activePlan.RefineCoarsePositions && level < stages.Length - 1);
            var capacity = Math.Clamp(PoseCachePixelBudget / (levelTemplate.Width * levelTemplate.Height),
                MinimumPoseCacheCapacity, MaximumPoseCacheCapacity);
            var image = new SearchImage(region.Width / factor, region.Height / factor, area => source.Read(new(
                region.X + (area.X * factor), region.Y + (area.Y * factor), area.Width * factor, area.Height * factor), factor, token));
            IScoreLevel Prepare(CpuGrayImage scene) => new CachedLevel(prepare(levelTemplate, scene,
                (template.Width / (2.0 * factor)) - .5, (template.Height / (2.0 * factor)) - .5, fine), capacity);
            // 템플릿 반경, 32px 거리 전파, 미분과 봉우리의 이웃까지 포함한다.
            var margin = (int)Math.Ceiling(Math.Sqrt((levelTemplate.Width * levelTemplate.Width) +
                (levelTemplate.Height * levelTemplate.Height)) * options.MaximumScale / 2) + LocalTileSafetyMarginPixels;
            IScoreLevel scoreLevel;
            if ((long)image.Width * image.Height <= FullFrameScorePixelBudget)
            {
                scoreLevel = Prepare(image.ReadAll());
            }
            else
            {
                var local = new LocalScoreLevel(image, margin, Prepare, token, activePlan.ReuseIdenticalTiles);
                localLevels.Add(local);
                scoreLevel = new CachedLevel(local, capacity);
            }
            stages[level] = new(image, scoreLevel, Prepare, margin);
        }
        var preparationMs = timer.Elapsed.TotalMilliseconds;
        long evaluations = 0;
        var refinementMs = 0d;
        var verificationMs = 0d;
        var refined = new List<Match>();
        var coarse = VisitWindowedCoarse(stages[^1].Image, stages[^1].Prepare, stages[^1].Margin, options, accelerator, token, candidates =>
        {
            CpuSearchDiagnostics.Write("Candidates", candidates);
            timer.Restart();
            var levels = stages.Select(s => s.Level).ToArray();
            var (current, angleRadius) = RefinePyramid(levels, candidates, options, activePlan.RefineCoarsePositions, token, ref evaluations, skipOriginal: true);
            refinementMs += timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            refined.AddRange(RefineAndVerifyOriginal(template, stages[0].Image, stages[0].Level, current, region, options,
                angleRadius, stages.Length > 1, activePlan.RefineCoarsePositions && stages.Length == 2,
                activePlan.RefineEdges, acceptCenter, token, ref evaluations));
            verificationMs += timer.Elapsed.TotalMilliseconds;
            foreach (var local in localLevels)
            {
                local.ReportProfile();
            }
        });
        evaluations += coarse.Evaluations;
        timer.Restart();
        var final = Suppress(refined, options.SuppressionRadius, UnboundedAngleSuppressionDegrees, UnboundedScaleSuppressionDelta);
        CpuSearchDiagnostics.Write("Accepted", refined);
        CpuSearchDiagnostics.Write("Suppressed", final);
        return new SearchResult(final.Take(options.MaximumMatches).ToArray(), false, final.Count > options.MaximumMatches, evaluations)
        {
            CandidateCount = coarse.Candidates,
            CandidateBatches = coarse.Batches,
            Timings = new(preparationMs, coarse.Milliseconds, refinementMs, verificationMs, timer.Elapsed.TotalMilliseconds)
        };
    }

    private static List<Match> RefineAndVerifyOriginal(CpuGrayImage template, SearchImage image, IScoreLevel level, List<Match> candidates,
        CpuSearchRegion region, CpuSearchOptions options, double angleRadius, bool scaleCenters, bool densePositions,
        bool refineEdges, Func<double, double, bool>? acceptCenter, CancellationToken token, ref long evaluations)
    {
        var original = new Match[candidates.Count];
        var counts = new long[candidates.Count];
        var checks = new ConcurrentDictionary<(double X, double Y, double Angle, double Scale), Lazy<(Match Match, long Evaluations)>>();
        var refiner = CreateOriginalRefiner(template, image, refineEdges);
        ForEachCandidate(candidates, level, options.MaximumDegreeOfParallelism, token, index =>
        {
            var seed = scaleCenters ? candidates[index] with
            {
                X = (candidates[index].X * 2) + 0.5,
                Y = (candidates[index].Y * 2) + 0.5
            } : candidates[index];
            var pose = Refine(level, seed, options, angleRadius, 1, densePositions ? 6 : 2, ref counts[index]);
            original[index] = pose;
            // 원본 맵이 아직 캐시에 있을 때 검증한다. 기존 DistinctBy와 같은 자세만 공유한다.
            // Lazy는 동시에 같은 자세에 도착해도 검증과 점수 평가를 정확히 한 번 수행한다.
            _ = checks.GetOrAdd((pose.X, pose.Y, pose.AngleDegrees, pose.Scale), _ => new(() =>
            {
                long count = 0;
                var match = VerifyPose(template, image, level, pose, options, Math.Max(0.5, angleRadius / 2),
                    refineEdges, refiner, ref count);
                return (match, count);
            })).Value;
        });
        evaluations += counts.Sum();
        // 완료 순서가 아닌 기존 후보 순서로 복원한 뒤 같은 위치/자세만 합친다.
        var distinct = original.DistinctBy(match => (match.X, match.Y, match.AngleDegrees, match.Scale)).ToArray();
        CpuSearchDiagnostics.Write("Refined", distinct);
        var checkedMatches = new Match[distinct.Length];
        for (var index = 0; index < distinct.Length; index++)
        {
            var pose = distinct[index];
            var result = checks[(pose.X, pose.Y, pose.AngleDegrees, pose.Scale)].Value;
            checkedMatches[index] = result.Match;
            evaluations += result.Evaluations;
        }
        return FilterOriginalMatches(checkedMatches, region, options, acceptCenter, token);
    }

    private static (long Evaluations, long Candidates, int Batches, double Milliseconds) VisitWindowedCoarse(
        SearchImage image, Func<CpuGrayImage, IScoreLevel> prepare, int margin, CpuSearchOptions options,
        ICoarseScoreAccelerator? accelerator, CancellationToken token, Action<List<Match>> inspect)
    {
        const int Cell = 256;
        var timer = Stopwatch.StartNew();
        var candidates = new List<Match>(options.CandidateLimit);
        long evaluations = 0;
        long count = 0;
        var batches = 0;
        var threshold = Math.Max(CoarseScoreFloor, options.MinimumScore - CoarseScoreSlack);
        // 한 띠의 후보도 제한된 높이로 모으고, 기존 angle/scale/y/x 순서로 다음 단계에 전달한다.
        var bandHeight = Math.Clamp(WindowedCoarseBandPixelBudget / image.Width, 1, Cell);
        foreach (var scale in Samples(options.MinimumScale, options.MaximumScale, options.ScaleStep))
        {
            foreach (var angle in Samples(options.MinimumAngle, options.MaximumAngle, options.AngleStep))
            {
                for (var y = 0; y < image.Height;)
                {
                    var endY = y + Math.Min(bandHeight, image.Height - y);
                    var band = new List<Match>();
                    for (var x = 0; x < image.Width;)
                    {
                        token.ThrowIfCancellationRequested();
                        var endX = x + Math.Min(Cell, image.Width - x);
                        var left = Math.Max(0, x - margin);
                        var top = Math.Max(0, y - margin);
                        var right = (int)Math.Min(image.Width, (long)endX + margin);
                        var bottom = (int)Math.Min(image.Height, (long)endY + margin);
                        var pixels = image.Crop(new(left, top, right - left, bottom - top));
                        var level = prepare(pixels);
                        var scores = new double[pixels.Width * pixels.Height];
                        ScoreGrid(level.AtPose(angle, scale), scores, pixels.Width, pixels.Height,
                            level.SupportsParallelEvaluation, options.MaximumDegreeOfParallelism, accelerator, token);
                        evaluations += scores.Length;
                        for (var row = Math.Max(1, y); row < Math.Min(endY, image.Height - 1); row++)
                        {
                            token.ThrowIfCancellationRequested();
                            for (var column = Math.Max(1, x); column < Math.Min(endX, image.Width - 1); column++)
                            {
                                var score = scores[((row - top) * pixels.Width) + column - left];
                                if (score >= threshold && IsPeak(scores, pixels.Width, column - left, row - top))
                                {
                                    band.Add(new(column, row, angle, scale, score));
                                }
                            }
                        }
                        x = endX;
                    }
                    foreach (var candidate in band.OrderBy(match => match.Y).ThenBy(match => match.X))
                    {
                        candidates.Add(candidate);
                        count++;
                        if (candidates.Count == options.CandidateLimit)
                        {
                            InspectBatch();
                        }
                    }
                    y = endY;
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
            timer.Stop();
            inspect(candidates);
            candidates.Clear();
            batches++;
            timer.Start();
        }
    }

    private readonly record struct WindowedPyramidStage(
        SearchImage Image,
        IScoreLevel Level,
        Func<CpuGrayImage, IScoreLevel> Prepare,
        int Margin);
}
