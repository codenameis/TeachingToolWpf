using System.Collections.Immutable;
using System.Diagnostics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>원본 읽기는 한 소유자가 공간 순서로 수행하고 독립 crop의 계산만 제한된 병렬도로 실행한다.</summary>
internal static class ShapeCandidateRefinement
{
    // 기존 단일 8 Mpx crop은 허용하되, 병렬 crop의 합은 2 Mpx를 넘기지 않는다.
    internal const int ParallelPixelBudget = PreparedSearchImage.MaximumPixels;

    internal sealed record Output(MatchResult[] Results, double ReadMilliseconds);

    private sealed record Job(int Index, ShapeMatch Seed, int Left, int Top, int Width, int Height)
    {
        public long Pixels => (long)Width * Height;
    }

    public static Output Run(MatchingImageReader reader, ImageReference image, ShapeModel model,
        RoiGeometry? region, SearchOptions options, ShapeMatch[] seeds, CancellationToken token, ShapeMatcher matcher)
    {
        var factor = model.Levels[^1].Factor;
        var jobs = seeds.Select((seed, index) =>
            {
                token.ThrowIfCancellationRequested();
                return CreateJob(image, model, seed, index, factor);
            })
            .OrderBy(job => job.Top / 512).ThenBy(job => job.Left / 512).ThenBy(job => job.Index).ToArray();
        var results = new MatchResult[seeds.Length];
        var readTime = 0d;
        var next = 0;
        while (next < jobs.Length)
        {
            token.ThrowIfCancellationRequested();
            var batch = new List<(Job Job, GrayImage Image)>();
            var pixels = 0L;
            var timer = Stopwatch.StartNew();
            while (next < jobs.Length && batch.Count < options.MaximumParallelism)
            {
                var job = jobs[next];
                if (batch.Count > 0 && pixels + job.Pixels > ParallelPixelBudget)
                {
                    break;
                }
                batch.Add((job, reader.Read(job.Left, job.Top, job.Width, job.Height, token, ShapeTrainer.MaximumPixels)));
                pixels += job.Pixels;
                next++;
            }
            readTime += timer.Elapsed.TotalMilliseconds;
            if (batch.Count == 1)
            {
                Execute(batch[0]);
            }
            else
            {
                Parallel.ForEach(batch, new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaximumParallelism,
                    CancellationToken = token
                }, Execute);
            }
        }
        token.ThrowIfCancellationRequested();
        return new(results, readTime);

        void Execute((Job Job, GrayImage Image) input)
        {
            var job = input.Job;
            var result = matcher.RefineCandidate(input.Image, model,
                job.Seed.Pose with
                {
                    X = job.Seed.Pose.X - job.Left,
                    Y = job.Seed.Pose.Y - job.Top
                },
                job.Seed.CandidateScore, options with
                {
                    MaximumParallelism = 1,
                    Region = ShapeMatchingService.ToSearchRegion(region, job.Left, job.Top)
                }, token);
            // 완료 순서와 읽기 순서가 기존 점수 동률/NMS 순서를 바꾸지 않게 원래 위치로 돌려놓는다.
            results[job.Index] = result with
            {
                Matches = result.Matches.Select(match => match with
                {
                    Pose = match.Pose with
                    {
                        X = match.Pose.X + job.Left,
                        Y = match.Pose.Y + job.Top
                    },
                    Refinement = match.Refinement with
                    {
                        InitialPose = match.Refinement.InitialPose with
                        {
                            X = match.Refinement.InitialPose.X + job.Left,
                            Y = match.Refinement.InitialPose.Y + job.Top
                        }
                    }
                }).ToImmutableArray()
            };
        }
    }

    private static Job CreateJob(ImageReference image, ShapeModel model, ShapeMatch seed, int index, int factor)
    {
        var outline = MatchSuppression.TransformBounds(model, seed.Pose);
        var padding = 8 * factor;
        var left = Math.Max(0, ((int)Math.Floor(outline.Min(point => point.X)) - padding) / factor * factor);
        var top = Math.Max(0, ((int)Math.Floor(outline.Min(point => point.Y)) - padding) / factor * factor);
        var right = Math.Min(image.Width, (int)Math.Ceiling(outline.Max(point => point.X)) + padding);
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(outline.Max(point => point.Y)) + padding);
        return new(index, seed, left, top, right - left, bottom - top);
    }
}
