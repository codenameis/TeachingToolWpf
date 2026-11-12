using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed class ShapeModel
{
    internal double BoundsWidth { get; }
    internal double BoundsHeight { get; }
    internal TrainingPixelMask? PixelMask
    {
        get; init;
    }
    /// <summary>레벨 0 특징별 연결 윤곽 체인 ID(0=최외곽, 1..K-1=내부/분리 윤곽). 단일 윤곽이면 기본값(IsDefault)이다.</summary>
    internal ImmutableArray<byte> FeatureChains
    {
        get; init;
    }
    /// <summary>레벨 0 특징별 2D 공간 분할 블록 ID(0..7). 연속 부분 가림(Occlusion) 판정에 사용한다.</summary>
    internal ImmutableArray<byte> FeaturePartitions
    {
        get; init;
    }
    /// <summary>8개 공간 분할 블록 간 2D 인접 비트마스크(블록 k의 이웃 집합 = PartitionAdjacency[k]).</summary>
    internal ImmutableArray<byte> PartitionAdjacency
    {
        get; init;
    }
    /// <summary>레벨 0 모델 좌표계 기준 주 대칭 중심 (c_symX, c_symY).</summary>
    public ImagePoint SymmetryCenter
    {
        get; init;
    }
    /// <summary>레벨 0 특징별 자기 대칭성 분해 플래그(비트 1=IsAsymX 좌우 플립 구분 키, 비트 2=IsAsymY 상하 플립 구분 키).</summary>
    public ImmutableArray<byte> FeatureAsymmetryFlags
    {
        get; init;
    }
    /// <summary>레벨 0 모델 좌표계 기준 주 대칭 코어 직경(D_core).</summary>
    public double SymmetryCoreDiameter
    {
        get; init;
    }
    /// <summary>주 대칭 중심 주변 4방위(-X, +X, -Y, +Y) 문맥 구역의 평균 반경(r_X, r_Y).</summary>
    public ImagePoint ContextSectorRadii
    {
        get; init;
    }
    /// <summary>주 대칭 중심 주변 4방위(비트 0=-X, 비트 1=+X, 비트 2=-Y, 비트 3=+Y) 중 티칭된 접선 문맥 에지가 존재하는 방위 비트마스크.</summary>
    public byte OccupiedContextSectorMask
    {
        get; init;
    }
    /// <summary>주 대칭 중심 주변 4방위(비트 0=-X, 비트 1=+X, 비트 2=-Y, 비트 3=+Y) 중 학습 ROI 내부에 위치하면서 접선 문맥 에지가 없는 빈 배경(Free-Space) 방위 비트마스크.</summary>
    public byte EmptyContextSectorMask
    {
        get; init;
    }
    public Guid Id { get; } = Guid.NewGuid();
    public int FormatVersion => 1;
    public ImagePoint TrainingReferencePoint { get; }
    public double TrainingReferenceAngle { get; }
    public TrainingOptions Options { get; }
    public ImmutableArray<ModelLevel> Levels { get; }
    public ImmutableArray<ImagePoint> Bounds { get; }
    public int TemplateCount { get; init; }
    public long EstimatedBytes => Levels.Sum(l => (long)l.Features.Length * 32) +
        Templates.Sum(t => t.Levels.Sum(l => (long)l.Length * 12)) + (ContourModel?.EstimatedBytes ?? 0) + (FineDetail?.EstimatedBytes ?? 0) + (ContourModel is null ? PixelMask?.EstimatedBytes ?? 0 : 0);
    internal FineDetailModel? FineDetail
    {
        get;
    }
    public bool HasFineDetailModel => FineDetail is not null;
    internal ClutterModel? ContourModel
    {
        get;
    }
    internal ImmutableArray<PoseTemplate> Templates
    {
        get;
    }

    internal ShapeModel(TrainingRegion region, TrainingOptions options, ImmutableArray<ModelLevel> levels,
        ImmutableArray<ImagePoint> bounds, ImmutableArray<PoseTemplate> templates, ClutterModel? contourModel = null,
        FineDetailModel? fineDetail = null)
    {
        TrainingReferencePoint = region.ReferencePoint;
        TrainingReferenceAngle = region.ReferenceAngle;
        Options = options;
        Levels = levels;
        Bounds = bounds;
        Templates = templates;
        TemplateCount = templates.IsDefault ? 0 : templates.Length;
        if (!bounds.IsDefaultOrEmpty)
        {
            var minX = double.MaxValue;
            var maxX = double.MinValue;
            var minY = double.MaxValue;
            var maxY = double.MinValue;
            foreach (var point in bounds)
            {
                if (point.X < minX) minX = point.X;
                if (point.X > maxX) maxX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.Y > maxY) maxY = point.Y;
            }
            BoundsWidth = maxX - minX;
            BoundsHeight = maxY - minY;
        }
        ContourModel = contourModel;
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
            level == 0 ? ContourModel : null, level == 0 ? FineDetail : null)
        {
            PixelMask = level == 0 ? PixelMask : null,
            FeatureChains = level == 0 ? FeatureChains : default,
            FeaturePartitions = level == 0 ? FeaturePartitions : default,
            PartitionAdjacency = level == 0 ? PartitionAdjacency : default,
            FeatureAsymmetryFlags = level == 0 ? FeatureAsymmetryFlags : default,
            SymmetryCenter = level == 0 ? SymmetryCenter : default,
            SymmetryCoreDiameter = level == 0 ? SymmetryCoreDiameter : 0,
            ContextSectorRadii = level == 0 ? ContextSectorRadii : default,
            OccupiedContextSectorMask = level == 0 ? OccupiedContextSectorMask : (byte)0,
            EmptyContextSectorMask = level == 0 ? EmptyContextSectorMask : (byte)0
        };
    }
}
