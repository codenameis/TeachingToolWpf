using System.Collections.Immutable;
using System.Diagnostics;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed class ShapeMatcher
{
    /// <summary>분할 검색에서도 후보 유무와 무관하게 원본 모델의 검사 정책을 검증한다.</summary>
    public static void ValidateSearchOptions(ShapeModel model, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ValidateGeometricPolicy(model, options);
    }

    private readonly IImagePreprocessor? _preprocessor;
    private readonly IShapeCandidateScorer? _candidateScorer;
    private readonly bool _optimizedLocalSearch;

    /// <summary>전처리 구현의 수명은 호출자가 소유한다. CPU 모드에서는 주입된 구현을 사용하지 않는다.</summary>
    public ShapeMatcher(IImagePreprocessor? preprocessor = null, IShapeCandidateScorer? candidateScorer = null,
        bool optimizedLocalSearch = false)
    {
        _preprocessor = preprocessor;
        _candidateScorer = candidateScorer;
        _optimizedLocalSearch = optimizedLocalSearch;
    }

    public MatchResult RefineCandidate(GrayImage image, ShapeModel model, MatchPose initial, double candidateScore,
        SearchOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        ValidateGeometricPolicy(model, options);
        var preprocessor = GetPreprocessor(options);
        if (!double.IsFinite(initial.X) || !double.IsFinite(initial.Y) || !double.IsFinite(initial.Angle) ||
            !double.IsFinite(initial.Scale) || !double.IsFinite(candidateScore) || candidateScore is < 0 or > 1 ||
            initial.X < 0 || initial.Y < 0 || initial.X > image.Width || initial.Y > image.Height ||
            !ContinuousVerifier.Allowed(initial, model, options))
        {
            throw new ArgumentException("정밀화 초기 자세 또는 후보 점수가 모델의 유효 범위를 벗어납니다.", nameof(initial));
        }
        var prepared = PreparedSearchImage.CreateForRefinement(image, model.Options.MinimumGradient, model.Levels.Length,
            cancellationToken, options.TiledPreprocessing, preprocessor, RequiresEdges(model, options), model.FineDetail is not null && options.ClutterWeight > 0);
        var timer = Stopwatch.StartNew();
        var pose = initial;
        VerifiedScore? verification = null;
        if (options.Verify)
        {
            // 위치가 정해진 후보의 원본 검증에는 전체 응답맵 검색을 다시 수행하지 않는다.
            for (var level = model.Levels.Length - 1; level >= 0; level--)
            {
                var factor = model.Levels[level].Factor;
                var local = pose with
                {
                    X = pose.X / factor,
                    Y = pose.Y / factor
                };
                var result = ContinuousVerifier.Optimize(prepared.Gradients[level], model, local, options, cancellationToken, level);
                pose = result.Pose with
                {
                    X = result.Pose.X * factor,
                    Y = result.Pose.Y * factor
                };
                verification = result.Score;
            }
        }
        var candidate = new PoseCandidate(pose, candidateScore, 0, verification);
        return CompleteCandidates(prepared, model, options, new([candidate], false, 0), timer.Elapsed.TotalMilliseconds, cancellationToken);
    }

    public MatchResult Search(GrayImage image, ShapeModel model, SearchOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        ValidateGeometricPolicy(model, options);
        var preprocessor = GetPreprocessor(options);
        var createLinearResponses = options.ComputeMode != InspectionComputeMode.GpuPreprocessing || _candidateScorer is null;
        return Search(PreparedSearchImage.CreateForSearch(image, model.Options.MinimumGradient, model.Levels.Length,
            RequiresEdges(model, options), cancellationToken, options.TiledPreprocessing, preprocessor, createLinearResponses,
            model.FineDetail is not null && options.ClutterWeight > 0),
            model, options, cancellationToken);
    }

    public MatchResult Search(PreparedSearchImage image, ShapeModel model, SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        ValidateGeometricPolicy(model, options);
        if (image.MinimumGradient != model.Options.MinimumGradient || image.LevelCount < model.Levels.Length || image.Maps.Length < model.Levels.Length)
        {
            throw new ArgumentException("전처리 설정이 모델과 다릅니다.", nameof(image));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var scorer = options.ComputeMode == InspectionComputeMode.GpuPreprocessing ? _candidateScorer : null;
        var batch = CandidateSearch.Find(image, model, options, cancellationToken, scorer, _optimizedLocalSearch);
        var candidateTime = timer.Elapsed.TotalMilliseconds;
        return CompleteCandidates(image, model, options, batch, candidateTime, cancellationToken);
    }

    internal static MatchResult CompleteCandidates(PreparedSearchImage image, ShapeModel model, SearchOptions options,
        CandidateBatch batch, double candidateTime, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var candidates = VerificationCandidateScheduler.Select(batch.Candidates, model, options.MaximumVerifiedCandidates, cancellationToken);
        var detail = options.Verify && options.ClutterWeight > 0 ? model.FineDetail : null;
        if (detail is not null && image.Original is null)
        {
            throw new ArgumentException("작은 형상 세부 검증에는 retainOriginal 전처리가 필요합니다.", nameof(image));
        }
        var verified = new ShapeMatch?[candidates.Length];
        Parallel.For(0, candidates.Length, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaximumParallelism,
            CancellationToken = cancellationToken
        }, i =>
        {
            var candidate = candidates[i];
            var pose = candidate.Pose;
            var score = candidate.Verification ?? new VerifiedScore(candidate.Score, 0, 0);
            if (options.Verify && candidate.Verification is null)
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
            if (detail is not null)
            {
                var refined = options.Refine ? detail.Optimize(image.Original!, model, pose, options, cancellationToken)
                    : (Pose: pose, Score: detail.Score(image.Original!, pose, model.Options.Polarity));
                var directional = ContinuousVerifier.Score(image.Gradients[0], model, refined.Pose);
                verified[i] = verified[i]! with
                {
                    Pose = refined.Pose,
                    Score = directional.Score,
                    Support = directional.Support,
                    SupportedQuadrants = directional.Quadrants,
                    DetailConsistency = refined.Score
                };
            }
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
            var final = detail is null && options.Refine && options.Verify ? PointToLineRefiner.Refine(image, model, match, options, cancellationToken) : match;
            final = final with
            {
                DirectionScore = final.Score
            };
            if (!(options.Region?.Contains(final.Pose.X, final.Pose.Y) ?? true) ||
                final.Pose.X < 0 || final.Pose.Y < 0 || final.Pose.X > image.Width || final.Pose.Y > image.Height ||
                (options.Verify && (final.Score < options.MinimumScore || final.Support < options.MinimumSupport)))
            {
                continue;
            }
            if (options.Verify && options.EvaluateGeometry && model.GeometryModel is not null)
            {
                var quality = GeometricQualityEvaluator.Evaluate(image.Gradients[0], model, final.Pose,
                    options.GeometricTolerancePixels, cancellationToken);
                if (quality.Coverage < options.MinimumCoverage || quality.FitErrorPixels > options.MaximumFitErrorPixels ||
                    quality.ClutterRatio > options.MaximumClutterRatio)
                {
                    continue;
                }
                final = final with
                {
                    Geometry = quality,
                    ClutterRatio = quality.ClutterRatio,
                    ClutterPenaltyRatio = quality.ClutterEnergyRatio
                };
            }
            if (options.ClutterWeight > 0)
            {
                var clutter = final.Geometry is { } geometry
                    ? new ClutterMeasurement(geometry.ClutterRatio, geometry.SceneEdges, geometry.ClutterEnergyRatio)
                    : GeometricQualityEvaluator.EvaluateClutter(image.Gradients[0], model,
                        final.Pose, options.GeometricTolerancePixels, cancellationToken);
                final = final with
                {
                    ClutterRatio = clutter.Ratio,
                    ClutterPenaltyRatio = clutter.EnergyRatio,
                    Score = final.DirectionScore * (1 - (options.ClutterWeight * clutter.EnergyRatio)) *
                        (1 - (options.ClutterWeight * (1 - (final.DetailConsistency ?? 1))))
                };
                if (final.Score < options.MinimumScore)
                {
                    continue;
                }
            }
            matches.Add(final);
        }
        var refinementTime = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var output = MatchSuppression.Apply(model, matches, options.MaximumOverlap, int.MaxValue);
        return new(output.Take(options.MaximumMatches).ToImmutableArray(), new(image.ElapsedMilliseconds, candidateTime, verificationTime, refinementTime,
            timer.Elapsed.TotalMilliseconds, batch.EvaluatedTemplates, batch.Candidates.Count, batch.BudgetExceeded,
            batch.Candidates.Count > options.MaximumVerifiedCandidates, image.EstimatedBytes + (candidates.Length * 128L),
            "180° 무부호 후보 → 선택한 극성의 연속 방향 점수 × (1 - 추가 윤곽 가중치 × 대비 에너지 비율); 개수 비율과 기하 제한은 별도 유지; 반환 각도는 이미지 기준 절대각이며 범위는 학습 ROI 대비 편차; 검증 상한에서는 서로 다른 근접 자세 묶음을 먼저 방문하며 피라미드/국소 최대/후보 예산은 근사 탐색", output.Length > options.MaximumMatches)
        {
            CandidateGridMilliseconds = batch.GridMilliseconds,
            CandidatePoseMilliseconds = batch.PoseMilliseconds,
            CandidatesBeforeRefinement = batch.CandidatesBeforeRefinement
        });
    }

    private IImagePreprocessor? GetPreprocessor(SearchOptions options)
    {
        if (options.ComputeMode == InspectionComputeMode.Cpu || _candidateScorer is not null)
        {
            // 후보 점수 GPU 배치에서는 SIMD 전처리와 원본 검증을 CPU worker가 병렬 실행한다.
            // 작은 crop의 왕복 전송을 피하는 고정 정책이며, 장치 오류 후 폴백이 아니다.
            return null;
        }
        return _preprocessor ?? throw new InvalidOperationException("GPU 실행에는 초기화된 전처리 또는 후보 점수 구현이 필요합니다.");
    }

    private static bool RequiresEdges(ShapeModel model, SearchOptions options)
        => options.Verify && (options.Refine || (options.EvaluateGeometry && model.GeometryModel is not null) || options.ClutterWeight > 0);

    private static void ValidateGeometricPolicy(ShapeModel model, SearchOptions options)
    {
        if (options.ClutterWeight > 0 && (!options.Verify || model.ContourModel is null))
        {
            throw new ArgumentException("추가 윤곽 점수에는 연속 검증과 전체 윤곽·마스크를 보관한 원본 모델이 필요합니다.", nameof(model));
        }
        if (options.Verify && options.EvaluateGeometry && model.GeometryModel is null &&
            (options.MinimumCoverage > 0 || double.IsFinite(options.MaximumFitErrorPixels) || options.MaximumClutterRatio < 1))
        {
            throw new ArgumentException("기하 품질 제한에는 전체 윤곽과 마스크를 보관한 원본 모델이 필요합니다.", nameof(model));
        }
    }
}
