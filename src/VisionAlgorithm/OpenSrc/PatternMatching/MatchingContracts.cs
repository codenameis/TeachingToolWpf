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

public enum InspectionComputeMode
{
    Cpu,
    /// <summary>기존 직렬화 이름을 유지한다. 공급자가 지원하면 GPU 후보 점수와 CPU 원본 정밀 검증을 함께 사용한다.</summary>
    GpuPreprocessing
}

public sealed record TrainingOptions
{
    public int PyramidLevels { get; init; } = 3;
    public int MaximumFeatures { get; init; } = 160;
    public float MinimumGradient { get; init; } = 8;
    /// <summary>산포 특징 후보의 최소 강도/학습 최대 강도 비율. 전체 윤곽과 마스크는 생략하지 않는다.</summary>
    public double MinimumFeatureContrastRatio { get; init; } = 0.2;
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
    /// <summary>최종 기하 품질 계산에 필요한 전체 학습 윤곽과 마스크를 보관한다.</summary>
    public bool BuildGeometricModel { get; init; } = true;
    /// <summary>한 단계만 남은 작은 논문 형상의 원본 gradient 세부 검증. 추가 윤곽 감점과 함께 적용한다.</summary>
    public bool UseFineDetailValidation { get; init; }
    /// <summary>기하 상세 판정과 독립적으로 추가 윤곽 점수에 필요한 전체 윤곽과 마스크를 보관한다.</summary>
    public bool BuildClutterModel
    {
        get; init;
    }

    public void Validate()
    {
        if (PyramidLevels is < 1 or > 6 || MaximumFeatures is < 12 or > 512 ||
            !float.IsFinite(MinimumGradient) || MinimumGradient <= 0 ||
            !double.IsFinite(MinimumFeatureContrastRatio) || MinimumFeatureContrastRatio is < 0 or > 1 ||
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

/// <summary>Mask는 원본 픽셀마다 0/비0 한 바이트. 그래디언트 계산 후 특징 선택과 최종 기하 품질의 유효 영역에 적용한다.</summary>
public sealed record TrainingRegion(ReadOnlyMemory<byte> Mask, ImagePoint ReferencePoint, double ReferenceAngle = 0);

public sealed record SearchOptions
{
    public double CandidateThreshold { get; init; } = 0.65;
    public double MinimumScore { get; init; } = 0.72;
    /// <summary>방향 점수 × (1 - 가중치 × 추가 윤곽 에너지 비율).
    /// 작은 논문 모델은 원본 세부 설명 에너지 D의 (1 - 가중치 × (1-D))를 추가 적용한다. 0은 방향 점수를 유지한다.</summary>
    public double ClutterWeight
    {
        get; init;
    }
    public double MinimumSupport { get; init; } = 0.6;
    public int MaximumCandidates { get; init; } = 2048;
    public int MaximumVerifiedCandidates { get; init; } = 64;
    public int MaximumCoarseVerifiedCandidates { get; init; } = 256;
    public int MaximumMatches { get; init; } = 16;
    public double MaximumOverlap { get; init; } = 0.3;
    public bool Refine { get; init; } = true;
    public bool Verify { get; init; } = true;
    /// <summary>Verify가 활성이고 원본 모델일 때 최종 자세의 기하 품질을 측정한다.</summary>
    public bool EvaluateGeometry { get; init; } = true;
    /// <summary>최종 원본 검색 영상 픽셀 단위의 에지 대응 허용 거리.</summary>
    public double GeometricTolerancePixels { get; init; } = 3;
    /// <summary>선택 특징의 최소 대응 비율. 기본값 0은 추가 제한을 적용하지 않는다.</summary>
    public double MinimumCoverage
    {
        get; init;
    }
    /// <summary>최대 대응 거리 RMS. +Infinity는 추가 제한을 적용하지 않는다.</summary>
    public double MaximumFitErrorPixels { get; init; } = double.PositiveInfinity;
    /// <summary>학습 마스크 내부 장면 에지의 최대 설명 불가 비율. 기본값 1은 추가 제한을 적용하지 않는다.</summary>
    public double MaximumClutterRatio { get; init; } = 1;
    public bool UseSimd { get; init; } = true;
    public bool TiledPreprocessing
    {
        get; init;
    }
    public bool GroupTemplates
    {
        get; init;
    }
    public int MaximumParallelism { get; init; } = 1;
    public InspectionComputeMode ComputeMode { get; init; } = InspectionComputeMode.Cpu;
    public SearchRegion? Region
    {
        get; init;
    }

    public void Validate()
    {
        foreach (var value in new[] { CandidateThreshold, MinimumScore, MinimumSupport, MaximumOverlap, MinimumCoverage, MaximumClutterRatio, ClutterWeight })
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(SearchOptions));
            }
        }
        if (!double.IsFinite(GeometricTolerancePixels) || GeometricTolerancePixels is < 0.5 or > 8 ||
            double.IsNaN(MaximumFitErrorPixels) || MaximumFitErrorPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SearchOptions));
        }
        if (MaximumCandidates is < 1 or > 100000 || MaximumVerifiedCandidates is < 1 or > 4096 || MaximumCoarseVerifiedCandidates is < 1 or > 4096 ||
            MaximumMatches is < 1 or > 1000 || MaximumParallelism is < 1 or > 16 || !Enum.IsDefined(ComputeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(SearchOptions));
        }
    }
}

/// <summary>검색 영역은 모델 기준점의 허용 위치다. 영상 밖 특징은 점수 0이며 분모에는 남는다.</summary>
public sealed class SearchRegion
{
    private readonly SearchRegion[]? _intersection;
    private readonly SearchRegion? _expandedRegion;
    private readonly double _margin;

    private SearchRegion(SearchRegion region, double margin)
    {
        _expandedRegion = region;
        _margin = margin;
        Boundary = [];
    }

    public SearchRegion ExpandedBy(double margin)
    {
        if (!double.IsFinite(margin) || margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }
        return new(this, margin);
    }

    private SearchRegion(SearchRegion first, SearchRegion second)
    {
        _intersection = [first, second];
        Boundary = [];
    }

    public static SearchRegion Intersect(SearchRegion first, SearchRegion? second)
        => second is null ? first : new(first, second);

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
        if (_expandedRegion is { } expanded)
        {
            return expanded.ContainsWithin(x, y, _margin);
        }
        if (_intersection is { } regions)
        {
            return regions.All(region => region.Contains(x, y));
        }
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

    internal bool ContainsWithin(double x, double y, double margin)
    {
        if (_expandedRegion is { } expanded)
        {
            return expanded.ContainsWithin(x, y, margin + _margin);
        }
        if (_intersection is { } regions)
        {
            return regions.All(region => region.ContainsWithin(x, y, margin));
        }
        if (Contains(x, y))
        {
            return true;
        }
        if (CircleCenter is { } center)
        {
            return Math.Sqrt(((x - center.X) * (x - center.X)) + ((y - center.Y) * (y - center.Y))) <= Radius + margin;
        }
        for (var index = 0; index < Boundary.Length; index++)
        {
            var a = Boundary[index];
            var b = Boundary[(index + 1) % Boundary.Length];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            var fraction = lengthSquared == 0 ? 0 : Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / lengthSquared, 0, 1);
            var ex = x - a.X - (fraction * dx);
            var ey = y - a.Y - (fraction * dy);
            if ((ex * ex) + (ey * ey) <= margin * margin)
            {
                return true;
            }
        }
        return false;
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
        Templates.Sum(t => t.Levels.Sum(l => (long)l.Length * 12)) + (ContourModel?.EstimatedBytes ?? 0) + (FineDetail?.EstimatedBytes ?? 0);
    internal FineDetailModel? FineDetail { get; }
    public bool HasFineDetailModel => FineDetail is not null;
    internal GeometricQualityModel? GeometryModel => Options.BuildGeometricModel ? ContourModel : null;
    internal GeometricQualityModel? ContourModel
    {
        get;
    }
    internal ImmutableArray<PoseTemplate> Templates
    {
        get;
    }

    internal ShapeModel(TrainingRegion region, TrainingOptions options, ImmutableArray<ModelLevel> levels,
        ImmutableArray<ImagePoint> bounds, ImmutableArray<PoseTemplate> templates, GeometricQualityModel? geometryModel = null,
        FineDetailModel? fineDetail = null)
    {
        TrainingReferencePoint = region.ReferencePoint;
        TrainingReferenceAngle = region.ReferenceAngle;
        Options = options;
        Levels = levels;
        Bounds = bounds;
        Templates = templates;
        ContourModel = geometryModel;
        FineDetail = fineDetail;
    }

    /// <summary>원본에서 같은 2×2 면적 평균으로 축소한 영상에 사용하는 불변 모델 뷰.</summary>
    public ShapeModel AtPyramidLevel(int level)
    {
        if (level < 0 || level >= Levels.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
        var factor = Levels[level].Factor;
        return new(new(ReadOnlyMemory<byte>.Empty,
                new(TrainingReferencePoint.X / factor, TrainingReferencePoint.Y / factor), TrainingReferenceAngle),
            Options with
            {
                PyramidLevels = Levels.Length - level
            },
            Levels.Skip(level).Select(value => value with { Factor = value.Factor / factor }).ToImmutableArray(),
            Bounds.Select(point => new ImagePoint(point.X / factor, point.Y / factor)).ToImmutableArray(),
            Templates.Select(template => template with { Levels = template.Levels.Skip(level).ToImmutableArray() }).ToImmutableArray(),
            level == 0 ? ContourModel : null, level == 0 ? FineDetail : null);
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
/// <summary>최종 반환 자세의 기하 품질. Coverage는 선택된 학습 특징 전체 중 대응 비율이며,
/// FitErrorPixels는 대응점의 유클리드 거리 RMS(원본 검색 픽셀), 무대응이면 +Infinity다.
/// ClutterRatio는 변환된 학습 마스크 안의 장면 에지 중 전체 학습 윤곽으로 설명되지 않는 비율이다.</summary>
public sealed record GeometricMatchQuality(double Coverage, double FitErrorPixels, double ClutterRatio,
    int MatchedFeatures, int ModelFeatures, int SceneEdges)
{
    /// <summary>설명되지 않는 윤곽의 gradient 강도 제곱 합 / 전체 윤곽의 강도 제곱 합.</summary>
    public double ClutterEnergyRatio { get; init; } = ClutterRatio;
}
public sealed record ShapeMatch(MatchPose Pose, double CandidateScore, double Score, double Support,
    int SupportedQuadrants, RefinementDiagnostics Refinement)
{
    /// <summary>작은 논문 모델의 원본 gradient 설명 에너지(0~1). null은 세부 검증 미적용.</summary>
    public double? DetailConsistency { get; init; }
    /// <summary>추가 윤곽 감점 전의 학습 특징 방향 일치도.</summary>
    public double DirectionScore { get; init; } = Score;
    /// <summary>최종 자세의 실제 학습 마스크 내부 추가 윤곽 비율. null은 측정하지 않았음을 뜻한다.</summary>
    public double? ClutterRatio
    {
        get; init;
    }
    /// <summary>추가 윤곽 점수에 사용한 대비 에너지 비율. 윤곽 개수 비율과 구별한다.</summary>
    public double? ClutterPenaltyRatio
    {
        get; init;
    }
    public GeometricMatchQuality? Geometry
    {
        get; init;
    }
}
public sealed record SearchDiagnostics(double PreprocessingMilliseconds, double CandidateMilliseconds,
    double VerificationMilliseconds, double RefinementMilliseconds, double SuppressionMilliseconds,
    int TemplatesEvaluated, int Candidates, bool CandidateBudgetExceeded, bool VerificationBudgetExceeded,
    long EstimatedWorkspaceBytes, string SearchPolicy, bool MatchLimitExceeded = false)
{
    public double CandidateGridMilliseconds
    {
        get; init;
    }
    public double CandidatePoseMilliseconds
    {
        get; init;
    }
    public int CandidatesBeforeRefinement
    {
        get; init;
    }
}
public sealed record MatchResult(ImmutableArray<ShapeMatch> Matches, SearchDiagnostics Diagnostics);
