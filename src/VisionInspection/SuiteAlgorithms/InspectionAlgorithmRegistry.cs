using System.Collections.Immutable;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 검사 스위트 알고리즘 등록소(Registry).
/// 생성자로 주입된 <see cref="IRoiInspectionAlgorithm"/> 목록을 키별 사전으로 인덱싱하여
/// 알고리즘 추가 시 구현체 1개와 등록 1줄만으로 서비스·뷰모델·도움말·모델 직렬화가 동작하게 한다.
/// </summary>
public sealed class InspectionAlgorithmRegistry
{
    private static readonly InspectionAlgorithmHelpSpec FallbackHelpSpec = new(
        "정밀 검사 스위트 매뉴얼",
        "HALCON / Cognex 급 상용 머신비전 알고리즘 가이드",
        "선택한 알고리즘의 수학적 모델에 따라 서브픽셀 계측·외관 검사·OCR·2D 코드 판독을 수행합니다.",
        "상단 콤보박스에서 알고리즘을 선택하고 전용 샘플 불러오기 또는 ROI 검사를 실행하세요.",
        "우측 파라미터 슬라이더를 조절하면 실시간으로 캔버스 프리뷰가 갱신됩니다.",
        "알고리즘별 사용 가이드와 상태 표시줄 메시지를 참고하세요.");

    private readonly ImmutableArray<IRoiInspectionAlgorithm> _ordered;
    private readonly Dictionary<InspectionSuiteAlgorithmKind, IRoiInspectionAlgorithm> _byKind;

    public InspectionAlgorithmRegistry(IEnumerable<IRoiInspectionAlgorithm> algorithms)
    {
        ArgumentNullException.ThrowIfNull(algorithms);

        var orderedBuilder = ImmutableArray.CreateBuilder<IRoiInspectionAlgorithm>();
        var map = new Dictionary<InspectionSuiteAlgorithmKind, IRoiInspectionAlgorithm>();

        foreach (IRoiInspectionAlgorithm algorithm in algorithms)
        {
            ArgumentNullException.ThrowIfNull(algorithm);
            if (!map.TryAdd(algorithm.Kind, algorithm))
            {
                throw new ArgumentException($"중복된 스위트 알고리즘 키가 등록되었습니다: {algorithm.Kind}", nameof(algorithms));
            }

            orderedBuilder.Add(algorithm);
        }

        if (orderedBuilder.Count == 0)
        {
            throw new ArgumentException("최소 1개 이상의 스위트 알고리즘이 등록되어야 합니다.", nameof(algorithms));
        }

        _ordered = orderedBuilder.ToImmutable();
        _byKind = map;
    }

    public IReadOnlyList<IRoiInspectionAlgorithm> All => _ordered;

    /// <summary>
    /// 표준 18종 검사 스위트 알고리즘이 순서대로 등록된 새 레지스트리 인스턴스를 생성한다.
    /// </summary>
    public static InspectionAlgorithmRegistry CreateDefault() => new(
    [
        new MetrologyRoiAlgorithm(),
        new GoldenVariationRoiAlgorithm(),
        new BlobRleRoiAlgorithm(),
        new CurvilinearRoiAlgorithm(),
        new FewShotRoiAlgorithm(),
        new PatchAnomalyRoiAlgorithm(),
        new CalibrationRoiAlgorithm(),
        new CornerAlignmentRoiAlgorithm(),
        new PpOcrRoiAlgorithm(),
        new AutoTeachingRoiAlgorithm(),
        new ShadingReconRoiAlgorithm(),
        new AdvancedMetrologyEllipseRoiAlgorithm(),
        new AdvancedBlobWatershedRoiAlgorithm(),
        new DataMatrix2DRoiAlgorithm(),
        new FftAndGaborRoiAlgorithm(),
        new Barcode1DRoiAlgorithm(),
        new PhotometricStereoRoiAlgorithm(),
        new GeometricTolerancingRoiAlgorithm(),
        new QrCode2DRoiAlgorithm()
    ]);

#pragma warning disable CS0618 // 기존 레시피 역직렬화 하위 호환을 위한 정규화 분기
    public InspectionSuiteAlgorithmKind NormalizeKind(InspectionSuiteAlgorithmKind kind) => kind switch
    {
        InspectionSuiteAlgorithmKind.IndustrialOcrOcv
            or InspectionSuiteAlgorithmKind.LightweightOcr
            or InspectionSuiteAlgorithmKind.CpuDeepOcr => InspectionSuiteAlgorithmKind.PpOcrOnnx,
        _ => kind
    };
#pragma warning restore CS0618

    public bool TryGet(InspectionSuiteAlgorithmKind kind, out IRoiInspectionAlgorithm? algorithm)
        => _byKind.TryGetValue(NormalizeKind(kind), out algorithm);

    public IRoiInspectionAlgorithm GetRequired(InspectionSuiteAlgorithmKind kind)
    {
        InspectionSuiteAlgorithmKind normalized = NormalizeKind(kind);
        if (_byKind.TryGetValue(normalized, out IRoiInspectionAlgorithm? algorithm))
        {
            return algorithm;
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, $"등록되지 않은 검사 스위트 알고리즘입니다: {kind}");
    }

    public string GetShortName(InspectionSuiteAlgorithmKind kind)
        => TryGet(kind, out IRoiInspectionAlgorithm? algorithm) && algorithm is not null
            ? algorithm.ShortName
            : kind.ToString();

    public string GetAssignmentBadge(InspectionSuiteAlgorithmKind kind)
        => TryGet(kind, out IRoiInspectionAlgorithm? algorithm) && algorithm is not null
            ? algorithm.AssignmentBadge
            : "18종 정밀 스위트";

    public InspectionSuiteCategory ResolveCategory(InspectionSuiteAlgorithmKind kind)
        => TryGet(kind, out IRoiInspectionAlgorithm? algorithm) && algorithm is not null
            ? algorithm.Category
            : InspectionSuiteCategory.MetrologyAndGeometry;

    public InspectionAlgorithmHelpSpec GetHelpSpec(InspectionSuiteAlgorithmKind kind)
        => TryGet(kind, out IRoiInspectionAlgorithm? algorithm) && algorithm is not null
            ? algorithm.HelpSpec
            : FallbackHelpSpec;

    public ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteAlgorithmKind kind,
        InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);

        return TryGet(kind, out IRoiInspectionAlgorithm? algorithm) && algorithm is not null
            ? algorithm.BuildGeometricGuides(target, parameters)
            : ImmutableArray<ImmutableArray<PointD>>.Empty;
    }
}
