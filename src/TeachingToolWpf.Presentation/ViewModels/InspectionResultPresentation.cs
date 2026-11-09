using System.Collections.Immutable;
using System.Numerics;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace TeachingToolWpf.ViewModels;

/// <summary>알고리즘 결과를 공급자와 무관한 캔버스 표시 값으로 변환한다.</summary>
internal static class InspectionResultPresentation
{
    public static ImmutableArray<MatchMarker> Markers(RoiGeometry teachingRegion, TemplateSearchOutput output)
    {
        var color = Color(InspectionDecision.Evaluate(output).Verdict);
        return output.Matches.Select((match, index) =>
        {
            var referencePoint = new PointD(match.ReferencePoint.X, match.ReferencePoint.Y);
            // 고정 방향 템플릿은 crop AABB가 아니라 학습 도형을 평행 이동한다.
            var detectedRegion = teachingRegion.WithFrame(referencePoint, teachingRegion.Width, teachingRegion.Height, teachingRegion.Angle);
            return new MatchMarker(detectedRegion.Outline.ToImmutableArray(), referencePoint, teachingRegion.Angle * Math.PI / 180,
                $"#{index + 1} NCC {match.Score:F3}", color, []);
        }).ToImmutableArray();
    }

    public static string Name(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Pass => "OK · 정상",
        InspectionVerdict.Fail => "NG · 불량",
        InspectionVerdict.Warning => "검사 확인 필요",
        InspectionVerdict.Review => "위치·방향 확인",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    public static string Hex(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Pass => "#32CD32",
        InspectionVerdict.Fail => "#FF6347",
        InspectionVerdict.Warning => "#FFA500",
        InspectionVerdict.Review => "#FFD700",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    public static Vector4 Color(InspectionVerdict verdict) => verdict switch
    {
        InspectionVerdict.Pass => new(50 / 255f, 205 / 255f, 50 / 255f, 1),
        InspectionVerdict.Fail => new(1, 99 / 255f, 71 / 255f, 1),
        InspectionVerdict.Warning => new(1, 165 / 255f, 0, 1),
        InspectionVerdict.Review => new(1, 215 / 255f, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    public static ImmutableArray<MatchMarker> Markers(ShapeModel model, RoiGeometry teachingRegion, ShapeSearchOutput output,
        RotationAcceptance? rotation = null)
    {
        var warning = InspectionDecision.Evaluate(output).Verdict == InspectionVerdict.Warning;
        return output.Matches.Select((match, index) =>
        {
            var verdict = warning ? InspectionVerdict.Warning : InspectionDecision.Evaluate(match, rotation, model.TrainingReferenceAngle).Verdict;
            var pose = match.Pose;
            var angle = RoiGeometry.NormalizeAngle(pose.Angle * 180 / Math.PI);
            var cosine = Math.Cos(pose.Angle) * pose.Scale;
            var sine = Math.Sin(pose.Angle) * pose.Scale;
            // 가장 세밀한 학습 특징을 원본 좌표로 옮긴다. 검출 영상에서 재측정한 대응점은 아니다.
            var features = model.Levels[0].Features.Select(feature => new PointD(
                pose.X + (cosine * feature.X) - (sine * feature.Y),
                pose.Y + (sine * feature.X) + (cosine * feature.Y))).ToImmutableArray();
            var keyFeatures = match.KeyMatchedFeatures.IsDefaultOrEmpty
                ? []
                : match.KeyMatchedFeatures.Select(p => new PointD(p.X, p.Y)).ToImmutableArray();
            var unmatchedFeatures = match.UnmatchedFeatures.IsDefaultOrEmpty
                ? []
                : match.UnmatchedFeatures.Select(p => new PointD(p.X, p.Y)).ToImmutableArray();
            // NMS용 마스크 AABB는 실제 ROI 윤곽과 다르다. 절대각을 한 번만 적용한다.
            var detectedRegion = teachingRegion.WithFrame(new(pose.X, pose.Y),
                teachingRegion.Width * pose.Scale, teachingRegion.Height * pose.Scale, angle);
            return new MatchMarker(
                detectedRegion.Outline.ToImmutableArray(),
                new(pose.X, pose.Y), pose.Angle,
                rotation is null ? $"#{index + 1} 점수 {match.Score:F3}" :
                    $"#{index + 1} {Name(verdict)} Δ{RotationAcceptance.DeviationDegrees(pose.Angle, model.TrainingReferenceAngle):F1}°",
                Color(verdict), features)
            {
                DiagnosticGroups = BuildDiagnosticGroups(keyFeatures, unmatchedFeatures),
                KeyFeatures = keyFeatures,
                UnmatchedFeatures = unmatchedFeatures
            };
        }).ToImmutableArray();
    }

    /// <summary>학습 직후 또는 파라미터 변경 시 선택한 ROI 위에 추출된 모델 특징점, 비대칭 문맥 키(AsymX, AsymY), 허용 거리 대역, 회전·배율 가이드를 즉시 시각적으로 미리 보여 준다.</summary>
    public static ImmutableArray<MatchMarker> TrainingPreviewMarkers(ShapeModel model, RoiGeometry teachingRegion,
        SearchOptions? searchOptions = null)
    {
        var angleRad = teachingRegion.Angle * Math.PI / 180;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var center = teachingRegion.Center;
        var level0 = model.Levels[0].Features;
        var asymFlags = !model.FeatureAsymmetryFlags.IsDefault && model.FeatureAsymmetryFlags.Length == level0.Length
            ? model.FeatureAsymmetryFlags
            : default;
        var strictContextActive = searchOptions is null || (searchOptions.StrictContext && searchOptions.MaxOcclusionRatio <= 0.2);
        var tolerancePixels = searchOptions?.ClutterTolerancePixels ?? 0;
        var features = ImmutableArray.CreateBuilder<PointD>(level0.Length);
        var keyFeatures = ImmutableArray.CreateBuilder<PointD>();
        var toleranceBand = tolerancePixels > 0 ? ImmutableArray.CreateBuilder<PointD>(level0.Length * 2) : null;
        var asymCount = 0;
        for (var i = 0; i < level0.Length; i++)
        {
            var f = level0[i];
            var pt = new PointD(center.X + (cos * f.X) - (sin * f.Y), center.Y + (sin * f.X) + (cos * f.Y));
            features.Add(pt);
            if (!asymFlags.IsDefault && (asymFlags[i] & 3) != 0)
            {
                asymCount++;
                if (strictContextActive)
                {
                    keyFeatures.Add(pt);
                }
            }
            if (toleranceBand is not null && (i % 2 == 0 || level0.Length <= 48))
            {
                var nx = (cos * f.NormalX) - (sin * f.NormalY);
                var ny = (sin * f.NormalX) + (cos * f.NormalY);
                toleranceBand.Add(new(pt.X + (nx * tolerancePixels), pt.Y + (ny * tolerancePixels)));
                toleranceBand.Add(new(pt.X - (nx * tolerancePixels), pt.Y - (ny * tolerancePixels)));
            }
        }
        var label = asymCount > 0
            ? strictContextActive
                ? $"학습 특징 {level0.Length}개 (비대칭 문맥 키 {asymCount}개)"
                : $"학습 특징 {level0.Length}개 (비대칭 키 {asymCount}개 완화됨)"
            : $"학습 특징 {level0.Length}개 (대칭 형상)";
        var keyImm = keyFeatures.ToImmutable();
        var bandImm = toleranceBand?.ToImmutable() ?? [];
        var (innerGuides, additionalGuides) = BuildGeometricGuides(teachingRegion,
            model.Options.MinimumAngleDegrees, model.Options.MaximumAngleDegrees,
            model.Options.MinimumScale, model.Options.MaximumScale);
        return
        [
            new MatchMarker(
                teachingRegion.Outline.ToImmutableArray(),
                center,
                angleRad,
                label,
                Color(InspectionVerdict.Pass),
                features.MoveToImmutable())
            {
                DiagnosticGroups = BuildDiagnosticGroups(keyImm, [], bandImm),
                KeyFeatures = keyImm,
                InnerContours = innerGuides,
                AdditionalContours = additionalGuides,
                BoundsColor = TrainingBoundsColor,
                BoundsThickness = TrainingBoundsThickness,
                BoundsDashLength = TrainingBoundsDashLength,
                BoundsGapLength = TrainingBoundsGapLength,
                InnerContourColor = GeometricGuideColor,
                InnerContourThickness = GeometricGuideThickness,
                InnerContourDashLength = GeometricGuideDashLength,
                InnerContourGapLength = GeometricGuideGapLength
            }
        ];
    }

    public static ImmutableArray<MatchMarker> CpuTrainingPreviewMarkers(CpuTemplate template,
        double minAngleDegrees = 0, double maxAngleDegrees = 0, double minScale = 1, double maxScale = 1)
    {
        var teachingRegion = template.TeachingRegion;
        var angleRad = teachingRegion.Angle * Math.PI / 180;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var center = teachingRegion.Center;
        var rawPoints = template.Matcher.PreviewPoints;
        var features = ImmutableArray.CreateBuilder<PointD>(rawPoints.Count);
        for (var i = 0; i < rawPoints.Count; i++)
        {
            var (x, y) = rawPoints[i];
            features.Add(new(center.X + (cos * x) - (sin * y), center.Y + (sin * x) + (cos * y)));
        }
        var kindLabel = template.Method == CpuTemplateMethod.Line2D ? "에지 특징" : "ZNCC 표본";
        var (innerGuides, additionalGuides) = BuildGeometricGuides(teachingRegion, minAngleDegrees, maxAngleDegrees, minScale, maxScale);
        return
        [
            new MatchMarker(
                teachingRegion.Outline.ToImmutableArray(),
                center,
                angleRad,
                $"{template.Method} {kindLabel} {rawPoints.Count}개",
                Color(InspectionVerdict.Pass),
                features.MoveToImmutable())
            {
                InnerContours = innerGuides,
                AdditionalContours = additionalGuides,
                BoundsColor = TrainingBoundsColor,
                BoundsThickness = TrainingBoundsThickness,
                BoundsDashLength = TrainingBoundsDashLength,
                BoundsGapLength = TrainingBoundsGapLength,
                InnerContourColor = GeometricGuideColor,
                InnerContourThickness = GeometricGuideThickness,
                InnerContourDashLength = GeometricGuideDashLength,
                InnerContourGapLength = GeometricGuideGapLength
            }
        ];
    }

    public static ImmutableArray<MatchMarker> TemplateTrainingPreviewMarkers(CorrelationTemplate model, RoiGeometry teachingRegion)
        =>
        [
            new MatchMarker(
                teachingRegion.Outline.ToImmutableArray(),
                teachingRegion.Center,
                teachingRegion.Angle * Math.PI / 180,
                $"OpenCV 템플릿 {model.Width}×{model.Height} px",
                Color(InspectionVerdict.Pass),
                [])
            {
                BoundsColor = TrainingBoundsColor,
                BoundsThickness = TrainingBoundsThickness,
                BoundsDashLength = TrainingBoundsDashLength,
                BoundsGapLength = TrainingBoundsGapLength
            }
        ];

    public static ImmutableArray<MatchMarker> PatMaxTrainingPreviewMarkers(
        PatMaxTrainedTemplate template,
        double minAngleDegrees = 0,
        double maxAngleDegrees = 0,
        double minScale = 1,
        double maxScale = 1,
        double elasticityPixels = 0)
    {
        var teachingRegion = template.TeachingRegion;
        var angleRad = teachingRegion.Angle * Math.PI / 180;
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var center = teachingRegion.Center;
        var rawPoints = template.PreviewPoints;
        var rawNormals = template.PreviewNormals;
        var coarsePoints = template.CoarsePreviewPoints;
        var elasticity = elasticityPixels > 0 ? elasticityPixels : template.TrainParams.ElasticityPixels;

        var features = ImmutableArray.CreateBuilder<PointD>(rawPoints.Length);
        var toleranceBand = elasticity > 0 && !rawNormals.IsDefaultOrEmpty
            ? ImmutableArray.CreateBuilder<PointD>(rawPoints.Length * 2)
            : null;
        var bandStride = Math.Max(1, rawPoints.Length / 192);

        for (var i = 0; i < rawPoints.Length; i++)
        {
            var pt = rawPoints[i];
            var worldPt = new PointD(center.X + (cos * pt.X) - (sin * pt.Y), center.Y + (sin * pt.X) + (cos * pt.Y));
            features.Add(worldPt);

            if (toleranceBand is not null && i < rawNormals.Length && (i % bandStride == 0))
            {
                var n = rawNormals[i];
                var nx = (cos * n.X) - (sin * n.Y);
                var ny = (sin * n.X) + (cos * n.Y);
                toleranceBand.Add(new(worldPt.X + (nx * elasticity), worldPt.Y + (ny * elasticity)));
                toleranceBand.Add(new(worldPt.X - (nx * elasticity), worldPt.Y - (ny * elasticity)));
            }
        }

        var keyFeatures = ImmutableArray.CreateBuilder<PointD>(coarsePoints.IsDefaultOrEmpty ? 0 : coarsePoints.Length);
        if (!coarsePoints.IsDefaultOrEmpty)
        {
            for (var i = 0; i < coarsePoints.Length; i++)
            {
                var cp = coarsePoints[i];
                keyFeatures.Add(new(center.X + (cos * cp.X) - (sin * cp.Y), center.Y + (sin * cp.X) + (cos * cp.Y)));
            }
        }

        var keyImm = keyFeatures.ToImmutable();
        var bandImm = toleranceBand?.ToImmutable() ?? [];
        var (innerGuides, additionalGuides) = BuildGeometricGuides(teachingRegion, minAngleDegrees, maxAngleDegrees, minScale, maxScale);
        var stabilityTag = template.Model.Stability.HasApertureWarning
            ? $"단일방향 경고 κ={template.Model.Stability.ConditionNumber:F1}"
            : $"안정 κ={template.Model.Stability.ConditionNumber:F1}";
        var coarseCount = coarsePoints.IsDefaultOrEmpty ? 0 : coarsePoints.Length;
        return
        [
            new MatchMarker(
                teachingRegion.Outline.ToImmutableArray(),
                center,
                angleRad,
                $"PatMax Fine {rawPoints.Length} · Coarse {coarseCount} · ±{elasticity:0.#}px ({stabilityTag})",
                Color(template.Model.Stability.HasApertureWarning ? InspectionVerdict.Warning : InspectionVerdict.Pass),
                features.MoveToImmutable())
            {
                DiagnosticGroups = BuildDiagnosticGroups(keyImm, [], bandImm),
                KeyFeatures = keyImm,
                InnerContours = innerGuides,
                AdditionalContours = additionalGuides,
                BoundsColor = TrainingBoundsColor,
                BoundsThickness = TrainingBoundsThickness,
                BoundsDashLength = TrainingBoundsDashLength,
                BoundsGapLength = TrainingBoundsGapLength,
                InnerContourColor = GeometricGuideColor,
                InnerContourThickness = GeometricGuideThickness,
                InnerContourDashLength = GeometricGuideDashLength,
                InnerContourGapLength = GeometricGuideGapLength
            }
        ];
    }

    private static (ImmutableArray<ImmutableArray<PointD>> InnerContours, ImmutableArray<ImmutableArray<PointD>> AdditionalContours) BuildGeometricGuides(
        RoiGeometry teachingRegion, double minAngleDegrees, double maxAngleDegrees, double minScale, double maxScale)
    {
        var inner = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
        var additional = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();
        if (Math.Abs(minScale - 1.0) > 1e-3 && minScale > 0)
        {
            var scaledMin = teachingRegion.WithFrame(teachingRegion.Center, teachingRegion.Width * minScale, teachingRegion.Height * minScale, teachingRegion.Angle);
            inner.Add(scaledMin.Outline.ToImmutableArray());
        }
        if (Math.Abs(maxScale - 1.0) > 1e-3 && maxScale > 0 && Math.Abs(maxScale - minScale) > 1e-3)
        {
            var scaledMax = teachingRegion.WithFrame(teachingRegion.Center, teachingRegion.Width * maxScale, teachingRegion.Height * maxScale, teachingRegion.Angle);
            inner.Add(scaledMax.Outline.ToImmutableArray());
        }
        var span = maxAngleDegrees - minAngleDegrees;
        if (span > 0.5)
        {
            var radius = Math.Max(8.0, 0.36 * Math.Min(teachingRegion.Width, teachingRegion.Height));
            var baseRad = teachingRegion.Angle * Math.PI / 180;
            var startRad = baseRad + (minAngleDegrees * Math.PI / 180);
            var endRad = baseRad + (maxAngleDegrees * Math.PI / 180);
            var steps = Math.Clamp((int)Math.Ceiling(span / 7.5), 4, 36);
            var fan = ImmutableArray.CreateBuilder<PointD>(steps + 2);
            var c = teachingRegion.Center;
            if (span < 359.5)
            {
                fan.Add(c);
            }
            for (var s = 0; s <= steps; s++)
            {
                var a = startRad + ((endRad - startRad) * s / steps);
                fan.Add(new(c.X + (Math.Cos(a) * radius), c.Y + (Math.Sin(a) * radius)));
            }
            inner.Add(fan.ToImmutable());
        }
        return (inner.ToImmutable(), additional.ToImmutable());
    }

    // 학습 기준 템플릿 외곽선: 얇은 하늘색(#38BDF8) 긴 대시선 (6px 선 / 4px 간격)
    private static readonly Vector4 TrainingBoundsColor = new(0.22f, 0.74f, 0.97f, 0.82f);
    private const double TrainingBoundsThickness = 1.25;
    private const double TrainingBoundsDashLength = 6.0;
    private const double TrainingBoundsGapLength = 4.0;

    // 배율(MinScale/MaxScale) 및 회전 범위 가이드: 반투명 라벤더(#A78BFA) 미세 점선 (3px 선 / 4px 간격)
    private static readonly Vector4 GeometricGuideColor = new(0.66f, 0.55f, 0.98f, 0.62f);
    private const double GeometricGuideThickness = 1.0;
    private const double GeometricGuideDashLength = 3.0;
    private const double GeometricGuideGapLength = 4.0;

    private static readonly Vector4 ToleranceBandColor = new(1f, 0.78f, 0.18f, 0.9f);

    private static ImmutableArray<DiagnosticPointGroup> BuildDiagnosticGroups(
        ImmutableArray<PointD> keyFeatures,
        ImmutableArray<PointD> unmatchedFeatures,
        ImmutableArray<PointD> toleranceBand = default)
    {
        if (keyFeatures.IsDefaultOrEmpty && unmatchedFeatures.IsDefaultOrEmpty && toleranceBand.IsDefaultOrEmpty)
        {
            return [];
        }
        var builder = ImmutableArray.CreateBuilder<DiagnosticPointGroup>(3);
        if (!toleranceBand.IsDefaultOrEmpty)
        {
            builder.Add(new(ToleranceBandColor, toleranceBand, 1.1));
        }
        if (!keyFeatures.IsDefaultOrEmpty)
        {
            builder.Add(new(MatchMarker.KeyFeatureColor, keyFeatures, 2.2));
        }
        if (!unmatchedFeatures.IsDefaultOrEmpty)
        {
            builder.Add(new(MatchMarker.UnmatchedFeatureColor, unmatchedFeatures, 2.2));
        }
        return builder.ToImmutable();
    }
}
