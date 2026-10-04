using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public readonly record struct ImagePoint(double X, double Y);

/// <summary>원본 이미지 기준점 위치, 이미지 기준 절대 회전각(시계 방향 라디안), 양수 균일 배율.</summary>
public readonly record struct MatchPose(double X, double Y, double Angle, double Scale = 1);

public enum ContrastPolarity
{
    Preserve,
    IgnoreLocal
}

public sealed record TrainingOptions
{
    public int PyramidLevels { get; init; } = 3;
    public int MaximumFeatures { get; init; } = 160;
    public float MinimumGradient { get; init; } = 8;
    /// <summary>학습 ROI 방향에서 더한 시계 방향 회전 편차의 최솟값.</summary>
    public double MinimumAngleDegrees { get; init; } = -30;
    /// <summary>학습 ROI 방향에서 더한 시계 방향 회전 편차의 최댓값.</summary>
    public double MaximumAngleDegrees { get; init; } = 30;
    public double AngleStepDegrees { get; init; } = 5;
    public double MinimumScale { get; init; } = 1;
    public double MaximumScale { get; init; } = 1;
    public double ScaleStep { get; init; } = 0.1;
    public int MaximumTemplates { get; init; } = 2048;
    public ContrastPolarity Polarity { get; init; } = ContrastPolarity.Preserve;

    public void Validate()
    {
        if (PyramidLevels is < 1 or > 6 || MaximumFeatures is < 12 or > 512 ||
            !float.IsFinite(MinimumGradient) || MinimumGradient <= 0 ||
            !double.IsFinite(MinimumAngleDegrees) || !double.IsFinite(MaximumAngleDegrees) ||
            MaximumAngleDegrees < MinimumAngleDegrees || MaximumAngleDegrees - MinimumAngleDegrees > 360 ||
            !double.IsFinite(AngleStepDegrees) || AngleStepDegrees <= 0 || AngleStepDegrees > 45 ||
            !double.IsFinite(MinimumScale) || !double.IsFinite(MaximumScale) ||
            MinimumScale <= 0 || MaximumScale < MinimumScale || MaximumScale > 8 ||
            !double.IsFinite(ScaleStep) || ScaleStep <= 0 || MaximumTemplates is < 1 or > 16384 ||
            !Enum.IsDefined(Polarity))
        {
            throw new ArgumentOutOfRangeException(nameof(TrainingOptions));
        }
        var angles = Math.Ceiling((MaximumAngleDegrees - MinimumAngleDegrees) / AngleStepDegrees) + 1;
        var scales = Math.Ceiling((MaximumScale - MinimumScale) / ScaleStep) + 1;
        if (angles * scales > MaximumTemplates)
        {
            throw new ArgumentException("회전·배율 템플릿 예산을 초과했습니다. 범위 또는 간격을 조정하십시오.");
        }
    }
}

/// <summary>Mask는 원본 픽셀마다 0/비0 한 바이트. 그래디언트 계산 후 특징 선택에만 적용한다.</summary>
public sealed record TrainingRegion(ReadOnlyMemory<byte> Mask, ImagePoint ReferencePoint, double ReferenceAngle = 0);

public sealed record SearchOptions
{
    public double CandidateThreshold { get; init; } = 0.65;
    public double MinimumScore { get; init; } = 0.72;
    public double MinimumSupport { get; init; } = 0.6;
    public int MaximumCandidates { get; init; } = 2048;
    public int MaximumVerifiedCandidates { get; init; } = 64;
    public int MaximumMatches { get; init; } = 16;
    public double MaximumOverlap { get; init; } = 0.3;
    public bool Refine { get; init; } = true;
    public bool Verify { get; init; } = true;
    public bool UseSimd
    {
        get; init;
    }
    public bool TiledPreprocessing
    {
        get; init;
    }
    public bool GroupTemplates
    {
        get; init;
    }
    public int MaximumParallelism { get; init; } = 1;
    public SearchRegion? Region
    {
        get; init;
    }

    public void Validate()
    {
        foreach (var value in new[] { CandidateThreshold, MinimumScore, MinimumSupport, MaximumOverlap })
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(SearchOptions));
            }
        }
        if (MaximumCandidates is < 1 or > 100000 || MaximumVerifiedCandidates is < 1 or > 4096 ||
            MaximumMatches is < 1 or > 1000 || MaximumParallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(SearchOptions));
        }
    }
}

/// <summary>검색 영역은 모델 기준점의 허용 위치다. 영상 밖 특징은 점수 0이며 분모에는 남는다.</summary>
public sealed class SearchRegion
{
    public ImmutableArray<ImagePoint> Boundary
    {
        get;
    }
    public ImagePoint? CircleCenter
    {
        get;
    }
    public double Radius
    {
        get;
    }

    public SearchRegion(ImagePoint center, double radius)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(radius) || radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        CircleCenter = center;
        Radius = radius;
        Boundary = [];
    }

    public SearchRegion(IEnumerable<ImagePoint> boundary)
    {
        Boundary = boundary.ToImmutableArray();
        if (Boundary.Length < 3 || Boundary.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
        {
            throw new ArgumentException("검색 영역에는 유한한 꼭짓점이 세 개 이상 필요합니다.", nameof(boundary));
        }
    }

    public bool Contains(double x, double y)
    {
        if (CircleCenter is { } center)
        {
            return ((x - center.X) * (x - center.X)) + ((y - center.Y) * (y - center.Y)) <= Radius * Radius;
        }
        var inside = false;
        for (var i = 0; i < Boundary.Length; i++)
        {
            var a = Boundary[i];
            var b = Boundary[(i + 1) % Boundary.Length];
            var cross = ((x - a.X) * (b.Y - a.Y)) - ((y - a.Y) * (b.X - a.X));
            if (Math.Abs(cross) < 1e-8 && x >= Math.Min(a.X, b.X) && x <= Math.Max(a.X, b.X) &&
                y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y))
            {
                return true;
            }
            if ((a.Y > y) != (b.Y > y) && x < a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y)))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}

public readonly record struct ModelFeature(double X, double Y, double NormalX, double NormalY);
public sealed record ModelLevel(int Factor, ImmutableArray<ModelFeature> Features);
internal readonly record struct QuantizedFeature(int X, int Y, int Bin);
internal sealed record PoseTemplate(double Angle, double Scale, ImmutableArray<ImmutableArray<QuantizedFeature>> Levels);

public sealed class ShapeModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public int FormatVersion => 1;
    public ImagePoint TrainingReferencePoint
    {
        get;
    }
    public double TrainingReferenceAngle
    {
        get;
    }
    public TrainingOptions Options
    {
        get;
    }
    public ImmutableArray<ModelLevel> Levels
    {
        get;
    }
    public ImmutableArray<ImagePoint> Bounds
    {
        get;
    }
    public int TemplateCount => Templates.Length;
    public long EstimatedBytes => Levels.Sum(l => (long)l.Features.Length * 32) +
        Templates.Sum(t => t.Levels.Sum(l => (long)l.Length * 12));
    internal ImmutableArray<PoseTemplate> Templates
    {
        get;
    }

    internal ShapeModel(TrainingRegion region, TrainingOptions options, ImmutableArray<ModelLevel> levels,
        ImmutableArray<ImagePoint> bounds, ImmutableArray<PoseTemplate> templates)
    {
        TrainingReferencePoint = region.ReferencePoint;
        TrainingReferenceAngle = region.ReferenceAngle;
        Options = options;
        Levels = levels;
        Bounds = bounds;
        Templates = templates;
    }
}

public enum RefinementStatus
{
    Disabled,
    Converged,
    IterationLimit,
    InsufficientSupport,
    IllConditioned,
    Rejected
}

public sealed record RefinementDiagnostics(RefinementStatus Status, MatchPose InitialPose, double InitialResidual,
    double FinalResidual, int Iterations, int Correspondences, bool AngleObservable);
public sealed record ShapeMatch(MatchPose Pose, double CandidateScore, double Score, double Support,
    int SupportedQuadrants, RefinementDiagnostics Refinement);
public sealed record SearchDiagnostics(double PreprocessingMilliseconds, double CandidateMilliseconds,
    double VerificationMilliseconds, double RefinementMilliseconds, double SuppressionMilliseconds,
    int TemplatesEvaluated, int Candidates, bool CandidateBudgetExceeded, bool VerificationBudgetExceeded,
    long EstimatedWorkspaceBytes, string SearchPolicy);
public sealed record MatchResult(ImmutableArray<ShapeMatch> Matches, SearchDiagnostics Diagnostics);
