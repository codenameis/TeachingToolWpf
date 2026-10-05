using System.Collections.Immutable;
using System.Diagnostics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>원본에서 만든 축소 영상으로 후보를 검색하고 후보 주변의 원본으로 최종 판정한다.</summary>
internal static class ShapePyramidSearch
{
    public static ShapeSearchOutput Run(MatchingImageReader reader, ImageReference image, ShapeModel model,
        RoiGeometry? region, SearchOptions options, Stopwatch total, double readTime, CancellationToken token, ShapeMatcher matcher,
        int preferredInterior = 256)
    {
        var sourceOpenTime = readTime;
        var phaseTimer = Stopwatch.StartNew();
        var coarse = model.AtPyramidLevel(model.Levels.Length - 1);
        var factor = model.Levels[^1].Factor;
        var radius = coarse.Bounds.Max(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y))) * model.Options.MaximumScale;
        var halo = (int)Math.Ceiling(radius) + 16;
        var interior = Math.Min(preferredInterior, (int)Math.Sqrt(PreparedSearchImage.MaximumPixels) - (2 * halo));
        if (interior < 32)
        {
            throw new InvalidOperationException("축소 모델이 검색 창 예산을 초과했습니다. 학습 ROI 또는 배율 범위를 줄이십시오.");
        }
        var bounds = region?.Bounds ?? new BoundsD(0, 0, image.Width, image.Height);
        var seeds = new PriorityQueue<ShapeMatch, double>();
        var diagnostics = ImmutableArray.CreateBuilder<SearchDiagnostics>();
        var incomplete = false;
        var windows = 0;
        var requests = new List<ShapeCoarseSearchScheduler.Request>();
        for (var y = Math.Max(0, (int)bounds.Top / factor / interior * interior); y < Math.Min(image.Height, bounds.Bottom) / factor; y += interior)
        {
            for (var x = Math.Max(0, (int)bounds.Left / factor / interior * interior); x < Math.Min(image.Width, bounds.Right) / factor; x += interior)
            {
                token.ThrowIfCancellationRequested();
                var left = Math.Max(0, x - halo) * factor;
                var top = Math.Max(0, y - halo) * factor;
                var right = Math.Min(image.Width, (x + interior + halo) * factor);
                var bottom = Math.Min(image.Height, (y + interior + halo) * factor);
                var ownership = new SearchRegion([new(x - (left / factor), y - (top / factor)),
                    new(x + interior - (left / factor), y - (top / factor)),
                    new(x + interior - (left / factor), y + interior - (top / factor)),
                    new(x - (left / factor), y + interior - (top / factor))]);
                requests.Add(new(left, top, right - left, bottom - top, SearchRegion.Intersect(ownership,
                    ScaledRegion(region, left, top, factor)?.ExpandedBy(4))));
            }
        }
        readTime += ShapeCoarseSearchScheduler.Run(requests, reader, factor, coarse, options, matcher, token, PublishWindow);
        var coarseTime = phaseTimer.Elapsed.TotalMilliseconds;
        phaseTimer.Restart();
        var refinement = ShapeCandidateRefinement.Run(reader, image, model, region, options,
            seeds.UnorderedItems.Select(value => value.Element).OrderByDescending(value => value.Score).ToArray(), token, matcher);
        readTime += refinement.ReadMilliseconds;
        diagnostics.AddRange(refinement.Results.Select(result => result.Diagnostics));
        var refinementTime = phaseTimer.Elapsed.TotalMilliseconds;
        phaseTimer.Restart();
        var matches = refinement.Results.SelectMany(result => result.Matches).ToList();
        var output = MatchSuppression.Apply(model, matches, options.MaximumOverlap, options.MaximumMatches + 1);
        return new(output.Take(options.MaximumMatches).ToImmutableArray(),
            windows, incomplete, readTime, total.Elapsed.TotalMilliseconds, diagnostics.ToImmutable(), output.Length > options.MaximumMatches)
        {
            Phases = new(sourceOpenTime, coarseTime, refinementTime, 0, phaseTimer.Elapsed.TotalMilliseconds)
        };

        void PublishWindow(ShapeCoarseSearchScheduler.Completion completion)
        {
            var result = completion.Result;
            windows++;
            diagnostics.Add(result.Diagnostics);
            incomplete |= result.Diagnostics.CandidateBudgetExceeded || result.Diagnostics.VerificationBudgetExceeded || result.Diagnostics.MatchLimitExceeded;
            foreach (var match in result.Matches)
            {
                seeds.Enqueue(match with
                {
                    Pose = match.Pose with
                    {
                        X = (match.Pose.X * factor) + completion.Left,
                        Y = (match.Pose.Y * factor) + completion.Top
                    }
                }, match.Score);
                if (seeds.Count > options.MaximumCandidates)
                {
                    seeds.Dequeue();
                    incomplete = true;
                }
            }
        }
    }

    private static SearchRegion? ScaledRegion(RoiGeometry? region, int left, int top, int factor)
        => region is null ? null : region.Radius > 0
            ? new(new((region.Center.X - left) / factor, (region.Center.Y - top) / factor), region.Radius / factor)
            : new(region.Outline.Select(point => new ImagePoint((point.X - left) / factor, (point.Y - top) / factor)));
}
