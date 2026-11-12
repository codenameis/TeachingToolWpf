using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// ROI별 검사 스위트 알고리즘의 공통 템플릿 메서드(취소 확인 → 타이밍 측정 → 로컬 크롭 읽기 → 결과 빌드)와
/// 기하 가이드 생성 유틸리티를 제공하는 기반 클래스.
/// </summary>
public abstract class RoiAlgorithmBase : IRoiInspectionAlgorithm
{
    private const double HalfFactor = 0.5;
    private const double MicrosecondsPerSecond = 1_000_000.0;
    private const double DefaultInnerBoxMinHalfWidthPx = 20.0;
    private const double DefaultInnerBoxMinHalfHeightPx = 10.0;
    private const double DefaultInnerBoxWidthRatio = 0.46;
    private const double DefaultInnerBoxHeightRatio = 0.36;

    protected internal readonly record struct LocalRoiCrop(
        ShapeGrayImage Crop,
        Vector2 LocalCenter,
        int OffsetX,
        int OffsetY);

    public abstract InspectionSuiteAlgorithmKind Kind { get; }

    public abstract InspectionSuiteCategory Category { get; }

    public abstract string ShortName { get; }

    public abstract string ShortPillLabel { get; }

    public abstract string AssignmentBadge { get; }

    public abstract string Description { get; }

    public abstract string UsageGuide { get; }

    public abstract string SampleSceneSlug { get; }

    public abstract string SampleSceneTitle { get; }

    public virtual bool UsesReferenceRois => false;

    public virtual bool SuppressesGuideOverlayWhenEvaluated => false;

    public abstract InspectionAlgorithmHelpSpec HelpSpec { get; }

    public virtual string GetUsageGuide(InspectionSuiteParameters parameters) => UsageGuide;

    public abstract string FormatParameterSummary(InspectionSuiteParameters parameters);

    public virtual ImmutableArray<ImmutableArray<PointD>> BuildGeometricGuides(
        InspectionSuiteRoiInput target,
        InspectionSuiteParameters parameters)
        => ImmutableArray<ImmutableArray<PointD>>.Empty;

    public abstract ImmutableArray<InspectionSuiteItemResult> Execute(RoiInspectionContext context);

    protected delegate InspectionSuiteItemResult RoiCropEvaluator<in TState>(
        InspectionSuiteRoiInput target,
        LocalRoiCrop localCrop,
        TState state,
        double elapsedMicrosecondsBeforeBuild,
        Func<double> getElapsedMicroseconds);

    /// <summary>
    /// 모든 대상 ROI에 대해 취소 확인 → 타이밍 시작 → 로컬 크롭 추출 → 개별 알고리즘 본문 실행을 일관되게 수행한다.
    /// </summary>
    protected static ImmutableArray<InspectionSuiteItemResult> ExecuteEach<TState>(
        RoiInspectionContext context,
        int marginPixels,
        TState state,
        Func<RoiInspectionContext, InspectionSuiteRoiInput, LocalRoiCrop, TState, Func<double>, InspectionSuiteItemResult> evaluateTarget)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluateTarget);

        IReadOnlyList<InspectionSuiteRoiInput> targets = context.TargetRois;
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);

        for (int i = 0; i < targets.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            LocalRoiCrop localCrop = ReadLocalCrop(
                context.Reader,
                context.ImageWidth,
                context.ImageHeight,
                target,
                marginPixels,
                context.CancellationToken);

            double MeasureElapsedMicroseconds() => (Stopwatch.GetTimestamp() - startTick) * MicrosecondsPerSecond / Stopwatch.Frequency;

            results.Add(evaluateTarget(context, target, localCrop, state, MeasureElapsedMicroseconds));
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// 단일 대상 ROI에서 1개 이상의 검사 결과(<see cref="InspectionSuiteItemResult"/>)를 복수 방출하는 알고리즘(예: 다중 바코드 리더)을 위한 공통 실행 루프.
    /// </summary>
    protected static ImmutableArray<InspectionSuiteItemResult> ExecuteMany<TState>(
        RoiInspectionContext context,
        int marginPixels,
        TState state,
        Action<RoiInspectionContext, InspectionSuiteRoiInput, LocalRoiCrop, TState, Func<double>, ImmutableArray<InspectionSuiteItemResult>.Builder> evaluateTargetMany)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluateTargetMany);

        IReadOnlyList<InspectionSuiteRoiInput> targets = context.TargetRois;
        var results = ImmutableArray.CreateBuilder<InspectionSuiteItemResult>(targets.Count);

        for (int i = 0; i < targets.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            InspectionSuiteRoiInput target = targets[i];
            long startTick = Stopwatch.GetTimestamp();

            LocalRoiCrop localCrop = ReadLocalCrop(
                context.Reader,
                context.ImageWidth,
                context.ImageHeight,
                target,
                marginPixels,
                context.CancellationToken);

            double MeasureElapsedMicroseconds() => (Stopwatch.GetTimestamp() - startTick) * MicrosecondsPerSecond / Stopwatch.Frequency;

            evaluateTargetMany(context, target, localCrop, state, MeasureElapsedMicroseconds, results);
        }

        return results.ToImmutable();
    }

    internal static LocalRoiCrop ReadLocalCrop(
        MatchingImageReader reader,
        int imageWidth,
        int imageHeight,
        InspectionSuiteRoiInput roi,
        int marginPixels,
        CancellationToken token)
    {
        double absCos = Math.Abs(Math.Cos(roi.AngleRadians));
        double absSin = Math.Abs(Math.Sin(roi.AngleRadians));
        int radiusX = (int)Math.Ceiling(((roi.Width * absCos) + (roi.Height * absSin)) * HalfFactor) + marginPixels;
        int radiusY = (int)Math.Ceiling(((roi.Width * absSin) + (roi.Height * absCos)) * HalfFactor) + marginPixels;

        int left = Math.Clamp((int)Math.Floor(roi.Center.X) - radiusX, 0, Math.Max(0, imageWidth - 1));
        int top = Math.Clamp((int)Math.Floor(roi.Center.Y) - radiusY, 0, Math.Max(0, imageHeight - 1));
        int right = Math.Clamp((int)Math.Ceiling(roi.Center.X) + radiusX, left + 1, imageWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(roi.Center.Y) + radiusY, top + 1, imageHeight);

        ShapeGrayImage crop = reader.Read(
            left,
            top,
            right - left,
            bottom - top,
            token,
            maximumPixels: CorrelationTemplate.MaximumSearchPixels);
        var localCenter = new Vector2((float)(roi.Center.X - left), (float)(roi.Center.Y - top));
        return new LocalRoiCrop(crop, localCenter, left, top);
    }

    protected static PointD LocalToWorld(InspectionSuiteRoiInput target, double localX, double localY)
    {
        // [Perf Why]: Preserve exact IEEE-754 evaluation with Math.Cos / Math.Sin (Math.SinCos can differ by 1 ULP).
        double cos = Math.Cos(target.AngleRadians);
        double sin = Math.Sin(target.AngleRadians);
        return new PointD(
            target.Center.X + (cos * localX) - (sin * localY),
            target.Center.Y + (sin * localX) + (cos * localY));
    }

    protected static ImmutableArray<ImmutableArray<PointD>> BuildStandardFramedScanGuides(
        InspectionSuiteRoiInput target,
        double minHalfWidthPx = DefaultInnerBoxMinHalfWidthPx,
        double minHalfHeightPx = DefaultInnerBoxMinHalfHeightPx,
        double widthRatio = DefaultInnerBoxWidthRatio,
        double heightRatio = DefaultInnerBoxHeightRatio)
    {
        double halfW = Math.Max(minHalfWidthPx, target.Width * widthRatio);
        double halfH = Math.Max(minHalfHeightPx, target.Height * heightRatio);
        var guides = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>(2);
        guides.Add([
            LocalToWorld(target, -halfW, -halfH),
            LocalToWorld(target, halfW, -halfH),
            LocalToWorld(target, halfW, halfH),
            LocalToWorld(target, -halfW, halfH)
        ]);
        guides.Add([LocalToWorld(target, -halfW, 0), LocalToWorld(target, halfW, 0)]);
        return guides.MoveToImmutable();
    }

    protected static ImmutableArray<PointD> BuildCirclePolygon(PointD center, double radius, int segments)
    {
        var pts = ImmutableArray.CreateBuilder<PointD>(segments);
        for (int i = 0; i < segments; i++)
        {
            double angle = 2.0 * Math.PI * i / segments;
            pts.Add(new PointD(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius)));
        }

        return pts.MoveToImmutable();
    }

    protected static ImmutableArray<PointD> BuildEllipsePolygon(PointD center, double semiMajor, double semiMinor, double angleRad, int segments)
    {
        var pts = ImmutableArray.CreateBuilder<PointD>(segments);
        (double sinR, double cosR) = Math.SinCos(angleRad);
        for (int i = 0; i < segments; i++)
        {
            double t = 2.0 * Math.PI * i / segments;
            double lx = semiMajor * Math.Cos(t);
            double ly = semiMinor * Math.Sin(t);
            pts.Add(new PointD(
                center.X + (lx * cosR) - (ly * sinR),
                center.Y + (lx * sinR) + (ly * cosR)));
        }

        return pts.MoveToImmutable();
    }

    protected static InspectionSuiteItemResult CreateZoneMarker(
        string name,
        PointD center,
        double width,
        double height,
        bool isPass,
        string badge,
        string detail)
    {
        double hw = width * HalfFactor;
        double hh = height * HalfFactor;
        ImmutableArray<PointD> bounds =
        [
            new PointD(center.X - hw, center.Y - hh),
            new PointD(center.X + hw, center.Y - hh),
            new PointD(center.X + hw, center.Y + hh),
            new PointD(center.X - hw, center.Y + hh)
        ];

        return new InspectionSuiteItemResult(
            Guid.NewGuid(),
            name,
            isPass,
            badge,
            detail,
            center,
            0.0,
            bounds,
            [],
            [center],
            [],
            [],
            0.0);
    }
}
