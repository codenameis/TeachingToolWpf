using System.Collections.Immutable;
using System.Diagnostics;
using VisionAlgorithm.OpenSrc.AutoTeaching;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// 11번 멀티파츠 풀 자동 인식 · 하위 ROI 자동 티칭 알고리즘.
/// </summary>
public sealed class AutoTeachingRoiAlgorithm : RoiAlgorithmBase
{
    private const int ArchetypeCanvasSize = 512;
    private const float ArchetypeCenterPx = 256.0f;
    private const float ArchetypeTeachSizePx = 200.0f;
    private const float MinCustomPartDimensionPx = 32.0f;
    private const double CustomCenterChildScale = 0.55;
    private const double CustomLeadOffsetXRatio = 0.22;
    private const double CustomLeadWidthRatio = 0.20;
    private const double CustomLeadHeightRatio = 0.16;
    private const double ChildCircleRadiusScale = 0.4;
    private const int ChildCirclePolygonSegments = 20;
    private const double DetectedPartBoundsRadiusPx = 90.0;
    private const int DetectedPartBoundsSegments = 32;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const float HoleCheckSizePx = 120.0f;
    private const float KeywayOffsetXPx = 42.0f;
    private const float KeywayWidthPx = 40.0f;
    private const float KeywayHeightPx = 24.0f;
    private const float FrameCheckWidthPx = 124.0f;
    private const float FrameCheckHeightPx = 84.0f;
    private const float Pin1DotOffsetXPx = -38.0f;
    private const float Pin1DotOffsetYPx = -20.0f;
    private const float Pin1DotSizePx = 24.0f;
    private const float CrossFiducialSizePx = 104.0f;

    public override InspectionSuiteAlgorithmKind Kind => InspectionSuiteAlgorithmKind.AutoTeaching;

    public override InspectionSuiteCategory Category => InspectionSuiteCategory.AiAndAutoTeaching;

    public override string ShortName => "11. 멀티파츠 풀 자동 인식 · 하위 ROI 자동 티칭";

    public override string ShortPillLabel => "멀티파츠 자동 티칭";

    public override string AssignmentBadge => "스위트 · 자동티칭";

    public override string Description =>
        "단일 샘플 티칭만으로 화면 내 다중 회전 부품을 자동 인식(Coarse Edge Proposal + 64B Signature + PatMax + ICGN)하고 하위 검사 ROI 월드 좌표를 즉시 생성합니다.";

    public override string UsageGuide =>
        "① 캔버스에서 학습할 부품 ROI를 선택하고 '＋ 선택 ROI 파츠 학습 등록'을 누르면 모델 풀에 64B 회전불변 서명과 하위 검사 ROI 오프셋(핀A/핀B/바디)이 즉시 티칭·등록됩니다 (샘플 열기 시 3종 파츠 자동 등록).\n② '전체 ROI 실행'을 누르면 1단계 에지 블롭 후보 추출 → 2단계 64B 서명 필터링 → 3단계 국소 PatMax + ICGN 정밀 보정으로 각 부품의 포즈와 하위 검사 ROI 월드 좌표를 자동 생성합니다.";

    public override string SampleSceneSlug => "autoteach";

    public override string SampleSceneTitle => "11. 멀티파츠 풀 자동 인식·하위 ROI 자동 티칭 샘플 (1200×800)";

    public override bool UsesReferenceRois => true;

    public override InspectionAlgorithmHelpSpec HelpSpec { get; } = new(
        "원클릭 멀티파츠 자동 티칭 & 하위 검사 ROI 자동 생성",
        "1단계 에지 블롭 후보 → 2단계 64-Byte 회전불변 방사/푸리에 서명 → 3단계 PatMax + ICGN 서브픽셀 보정",
        "• 수식 원리: 부품 1개를 샘플로 등록하면 외곽 형태와 내부 하위 요소(좌/우 리드 핀, 바디 등)의 상대 로컬 좌표계를 추출하고 64-Byte 회전불변 서명(방사형 프로파일 + Zernike/Fourier 진폭)을 생성합니다.\n" +
        "• 전체 실행 시 화면 내 임의 각도로 놓인 모든 부품 후보를 3단계 파이프라인(초고속 서명 필터링 → 국소 PatMax 다이폴 → 역합성 가우스-뉴턴 ICGN)으로 정밀 정합하고, 각 부품의 회전·이동이 반영된 하위 검사 ROI 월드 좌표를 자동 생성합니다.",
        "① 기준 부품 하나에 ROI를 그리고 [＋ 선택 ROI 파츠 학습 등록]을 눌러 모델 풀에 등록합니다(여러 종류의 이종 부품 동시 등록 가능).\n" +
        "② [전체 ROI 실행]을 누르면 화면 전체 또는 대상 영역에서 등록된 모든 파츠를 찾아 포즈(X, Y, θ)와 하위 핀/바디 검사 박스를 자동으로 펼쳐 줍니다.",
        "• 최소 기하 점수 (0.25~0.90): PatMax 기하 정합 통과 기준입니다.\n" +
        "• 최소 외관 코사인 유사도 (0.15~0.90): 64-Byte 서명 1차 필터링 기준입니다.\n" +
        "• 최대 마할라노비스 거리 (5~100): ICGN 정밀 정합 후 정상 외관 분포 허용 한계입니다.",
        "• 등록된 파츠 목록에서 개별 파츠 모델을 언제든 삭제하거나 추가 등록할 수 있습니다.");

    public override string FormatParameterSummary(InspectionSuiteParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        AutoTeachingOptions opt = parameters.AutoTeaching;
        return $"기하 매칭 최소점수={opt.MinGeometricScore:F2} · 64B 서명 코사인={opt.MinAppearanceCosine:F2} · 마할라노비스 상한={opt.MaxMahalanobis:F1}";
    }

    public override ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<InspectionSuiteRoiInput> rois = context.TargetRois;
        IReadOnlyList<InspectionSuiteRoiInput> referenceRois = context.ReferenceRois;
        AutoTeachingOptions opt = context.Parameters.AutoTeaching.Validate();

        using var visionWs = new VisionWorkspace();
        context.CancellationToken.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var fullImage = context.Reader.Read(
            0,
            0,
            context.ImageWidth,
            context.ImageHeight,
            context.CancellationToken,
            maximumPixels: CorrelationTemplate.MaximumSearchPixels);
        var fullView = new ReadOnlyImageView2D<byte>(fullImage.Pixels.Span, fullImage.Width, fullImage.Height, fullImage.Stride);

        using var pool = new PartModelPool();
        RegisterStandardArchetypeModels(pool, visionWs);

        for (int r = 0; r < referenceRois.Count; r++)
        {
            InspectionSuiteRoiInput refRoi = referenceRois[r];
            var refPose = new FixturePose2D((float)refRoi.Center.X, (float)refRoi.Center.Y, (float)refRoi.AngleRadians);
            var customChildren = new[]
            {
                new ChildRoiTemplateSpec(
                    $"Child_{r + 1}_Center",
                    ChildToolKind.SubpixelCaliperWidth,
                    new FixturePose2D(0.0f, 0.0f, 0.0f),
                    (float)(refRoi.Width * CustomCenterChildScale),
                    (float)(refRoi.Height * CustomCenterChildScale)),
                new ChildRoiTemplateSpec(
                    $"Child_{r + 1}_Lead",
                    ChildToolKind.FiducialCrosshairCheck,
                    new FixturePose2D((float)(refRoi.Width * CustomLeadOffsetXRatio), 0.0f, 0.0f),
                    (float)(refRoi.Width * CustomLeadWidthRatio),
                    (float)(refRoi.Height * CustomLeadHeightRatio))
            };
            pool.RegisterOrMergeOnTeach(
                fullView,
                in refPose,
                MathF.Max(MinCustomPartDimensionPx, (float)refRoi.Width),
                MathF.Max(MinCustomPartDimensionPx, (float)refRoi.Height),
                string.IsNullOrWhiteSpace(refRoi.Name) ? $"UserPart#{r + 1}" : refRoi.Name,
                customChildren,
                visionWs);
        }

        AutoTeachingResult autoRes = AutoTeachingOrchestrator.RecognizeAndAutoTeach(
            fullView,
            pool,
            visionWs,
            minGeometricScore: opt.MinGeometricScore,
            minAppearanceCosine: opt.MinAppearanceCosine,
            maxMahalanobisDistance: opt.MaxMahalanobis);
        sw.Stop();

        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(Math.Max(1, autoRes.Instances.Length));
        double amortizedUs = sw.Elapsed.TotalMicroseconds / Math.Max(1, autoRes.Instances.Length);

        for (int i = 0; i < autoRes.Instances.Length; i++)
        {
            DetectedPartInstance inst = autoRes.Instances[i];
            var pt = new PointD(inst.WorldPose.OriginX, inst.WorldPose.OriginY);
            var builder = new InspectionItemResultBuilder(
                Guid.NewGuid(),
                $"{inst.PartId} ({inst.PartName})",
                pt,
                inst.WorldPose.AngleRad,
                BuildCirclePolygon(pt, DetectedPartBoundsRadiusPx, DetectedPartBoundsSegments));

            builder.AddPrimaryPoint(pt);
            foreach (InstantiatedChildRoi child in inst.ChildRois)
            {
                var cc = new PointD(child.WorldCenter.X, child.WorldCenter.Y);
                builder.AddSecondaryPoint(cc);
                builder.AddContour(BuildCirclePolygon(cc, Math.Min(child.Width, child.Height) * ChildCircleRadiusScale, ChildCirclePolygonSegments));
            }

            results.Add(builder.Build(
                isPass: true,
                badgeLabel: $"{inst.PartId} · Score={inst.GeometricScore:F2} · 하위ROI={inst.ChildRois.Length}개",
                detailLine: $"자동 인식={inst.PartName} ({inst.PartId}), 중심=({pt.X:F2},{pt.Y:F2}), 각도={inst.WorldPose.AngleRad * RadiansToDegrees:F2}°, 하위 ROI {inst.ChildRois.Length}개 월드 좌표 자동 생성 완료 ({autoRes.SummaryText})",
                elapsedMicroseconds: amortizedUs));
        }

        if (results.Count == 0 && rois.Count > 0)
        {
            results.Add(CreateZoneMarker(
                "AutoTeaching",
                rois[0].Center,
                rois[0].Width,
                rois[0].Height,
                isPass: false,
                badge: "인식 파츠 0개",
                detail: "화면에서 등록된 파츠 모델을 찾지 못했습니다."));
        }

        return results.ToImmutable();
    }

    private static void RegisterStandardArchetypeModels(PartModelPool pool, VisionWorkspace visionWs)
    {
        var childRoisA = new[]
        {
            new ChildRoiTemplateSpec("HoleCheck_A", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(0.0f, 0.0f, 0.0f), HoleCheckSizePx, HoleCheckSizePx),
            new ChildRoiTemplateSpec("KeywayTab_A", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D(KeywayOffsetXPx, 0.0f, 0.0f), KeywayWidthPx, KeywayHeightPx)
        };
        var childRoisB = new[]
        {
            new ChildRoiTemplateSpec("FrameCheck_B", ChildToolKind.SubpixelCaliperWidth, new FixturePose2D(0.0f, 0.0f, 0.0f), FrameCheckWidthPx, FrameCheckHeightPx),
            new ChildRoiTemplateSpec("Pin1Dot_B", ChildToolKind.GoldenDifferenceAnomaly, new FixturePose2D(Pin1DotOffsetXPx, Pin1DotOffsetYPx, 0.0f), Pin1DotSizePx, Pin1DotSizePx)
        };
        var childRoisC = new[]
        {
            new ChildRoiTemplateSpec("CrossFiducial_C", ChildToolKind.FiducialCrosshairCheck, new FixturePose2D(0.0f, 0.0f, 0.0f), CrossFiducialSizePx, CrossFiducialSizePx)
        };

        int pixelCount = ArchetypeCanvasSize * ArchetypeCanvasSize;
        byte[] g1 = new byte[pixelCount];
        byte[] g2 = new byte[pixelCount];
        byte[] g3 = new byte[pixelCount];
        Array.Fill(g1, StandardPartArchetypeRenderer.DefaultBackgroundIntensity);
        Array.Fill(g2, StandardPartArchetypeRenderer.DefaultBackgroundIntensity);
        Array.Fill(g3, StandardPartArchetypeRenderer.DefaultBackgroundIntensity);
        StandardPartArchetypeRenderer.RenderPartIntoBuffer(g1, ArchetypeCanvasSize, ArchetypeCanvasSize, ArchetypeCanvasSize, 1, ArchetypeCenterPx, ArchetypeCenterPx, 0.0f);
        StandardPartArchetypeRenderer.RenderPartIntoBuffer(g2, ArchetypeCanvasSize, ArchetypeCanvasSize, ArchetypeCanvasSize, 2, ArchetypeCenterPx, ArchetypeCenterPx, 0.0f);
        StandardPartArchetypeRenderer.RenderPartIntoBuffer(g3, ArchetypeCanvasSize, ArchetypeCanvasSize, ArchetypeCanvasSize, 3, ArchetypeCenterPx, ArchetypeCenterPx, 0.0f);

        var centerPose = new FixturePose2D(ArchetypeCenterPx, ArchetypeCenterPx, 0.0f);
        pool.RegisterOrMergeOnTeach(new ReadOnlyImageView2D<byte>(g1, ArchetypeCanvasSize, ArchetypeCanvasSize, ArchetypeCanvasSize), in centerPose, ArchetypeTeachSizePx, ArchetypeTeachSizePx, "Part#1_Bracket", childRoisA, visionWs);
        pool.RegisterOrMergeOnTeach(new ReadOnlyImageView2D<byte>(g2, ArchetypeCanvasSize, ArchetypeCanvasSize, ArchetypeCanvasSize), in centerPose, ArchetypeTeachSizePx, ArchetypeTeachSizePx, "Part#2_Connector", childRoisB, visionWs);
        pool.RegisterOrMergeOnTeach(new ReadOnlyImageView2D<byte>(g3, ArchetypeCanvasSize, ArchetypeCanvasSize, ArchetypeCanvasSize), in centerPose, ArchetypeTeachSizePx, ArchetypeTeachSizePx, "Part#3_ShieldCan", childRoisC, visionWs);
    }
}
