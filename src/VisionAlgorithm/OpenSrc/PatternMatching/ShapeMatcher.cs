using System.Diagnostics;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed class ShapeMatcher
{
    public MatchResult Search(GrayImage image, ShapeModel model, SearchOptions options, CancellationToken cancellationToken = default)
        => Search(PreparedSearchImage.Create(image, model.Options.MinimumGradient, model.Levels.Length, cancellationToken, options.TiledPreprocessing),
            model, options, cancellationToken);

    public MatchResult Search(PreparedSearchImage image, ShapeModel model, SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (image.MinimumGradient != model.Options.MinimumGradient || image.LevelCount < model.Levels.Length)
        {
            throw new ArgumentException("전처리 설정이 모델과 다릅니다.", nameof(image));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var batch = CandidateSearch.Find(image, model, options, cancellationToken);
        var candidateTime = timer.Elapsed.TotalMilliseconds;
        return CompleteCandidates(image, model, options, batch, candidateTime, cancellationToken);
    }

    internal static MatchResult CompleteCandidates(PreparedSearchImage image, ShapeModel model, SearchOptions options,
        CandidateBatch batch, double candidateTime, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var candidates = batch.Candidates.Take(options.MaximumVerifiedCandidates).ToArray();
        var verified = new ShapeMatch?[candidates.Length];
        Parallel.For(0, candidates.Length, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaximumParallelism,
            CancellationToken = cancellationToken
        }, i =>
        {
            var candidate = candidates[i];
            var pose = candidate.Pose;
            var score = new VerifiedScore(candidate.Score, 0, 0);
            if (options.Verify)
            {
                (pose, score) = ContinuousVerifier.Optimize(image.Gradients[0], model, pose, options, cancellationToken);
            }
            if (pose.X < 0 || pose.Y < 0 || pose.X > image.Width || pose.Y > image.Height ||
                (options.Verify && (score.Score < options.MinimumScore || score.Support < options.MinimumSupport)))
            {
                return;
            }
            verified[i] = new(pose, candidate.Score, score.Score, score.Support, score.Quadrants,
                new(RefinementStatus.Disabled, pose, double.NaN, double.NaN, 0, 0, true));
        });
        var verificationTime = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var matches = new List<ShapeMatch>();
        foreach (var match in verified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match is null)
            {
                continue;
            }
            var final = options.Refine && options.Verify ? PointToLineRefiner.Refine(image, model, match, options, cancellationToken) : match;
            if ((options.Region?.Contains(final.Pose.X, final.Pose.Y) ?? true) &&
                final.Pose.X >= 0 && final.Pose.Y >= 0 && final.Pose.X <= image.Width && final.Pose.Y <= image.Height &&
                (!options.Verify || (final.Score >= options.MinimumScore && final.Support >= options.MinimumSupport)))
            {
                matches.Add(final);
            }
        }
        var refinementTime = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var output = MatchSuppression.Apply(model, matches, options.MaximumOverlap, options.MaximumMatches);
        return new(output, new(image.ElapsedMilliseconds, candidateTime, verificationTime, refinementTime,
            timer.Elapsed.TotalMilliseconds, batch.EvaluatedTemplates, batch.Candidates.Count, batch.BudgetExceeded,
            batch.Candidates.Count > options.MaximumVerifiedCandidates, image.EstimatedBytes + (candidates.Length * 128L),
            "180° 무부호 후보 → 선택한 극성의 연속 점수; 반환 각도는 이미지 기준 절대각이며 범위는 학습 ROI 대비 편차; 피라미드/국소 최대/후보 예산은 근사 탐색"));
    }
}
