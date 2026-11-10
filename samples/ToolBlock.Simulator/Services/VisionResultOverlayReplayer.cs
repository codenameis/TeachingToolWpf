// File: samples/ToolBlock.Simulator/Services/VisionResultOverlayReplayer.cs
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;
using VisionCanvas.Rendering;
using VisionGeometry;

namespace ToolBlock.Simulator.Services;

/// <summary>
/// Reconstructs <c>VisionCanvas</c> <see cref="MatchMarker"/> and <see cref="ResultOverlay"/> graphics
/// 100% deterministically from any <see cref="VisionInspectionEnvelope"/> (<see cref="VisionSpcRecord"/> + <see cref="VisionReplayPayload"/>).
/// Supports both live ToolBlock execution and historical database/SPC replay without re-running vision algorithms.
/// </summary>
public static class VisionResultOverlayReplayer
{
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const double DefaultBoxHalfSize = 12.0;
    private const double MinShapeDimension = 2.0;
    private const double FixtureAxisLength = 24.0;
    private const double DefaultBoundsThickness = 2.1;
    private const int CirclePolygonSegments = 36;

    private static readonly Vector4 ColorPass = new(0.20f, 0.83f, 0.60f, 1.0f);
    private static readonly Vector4 ColorFail = new(0.97f, 0.44f, 0.44f, 1.0f);
    private static readonly Vector4 ColorWarning = new(0.98f, 0.75f, 0.14f, 1.0f);
    private static readonly Vector4 ColorFixture = new(0.22f, 0.74f, 0.97f, 1.0f);

    /// <summary>
    /// Reconstructs a single <see cref="MatchMarker"/> from a <see cref="VisionInspectionEnvelope"/>.
    /// </summary>
    public static MatchMarker ToMatchMarker(in VisionInspectionEnvelope envelope, string? nodeNameOverride = null)
    {
        VisionSpcRecord spc = envelope.Spc;
        VisionReplayPayload replay = envelope.Replay;

        Vector4 color = ResolveMarkerColor(in spc);
        PointD refPoint = new(spc.WorldX, spc.WorldY);
        double angleRad = replay.WorldPose.AngleRad;

        double width = Math.Max(MinShapeDimension, replay.WidthOrRadius > 0.0f ? replay.WidthOrRadius : DefaultBoxHalfSize * 2.0);
        double height = Math.Max(MinShapeDimension, replay.HeightOrSpan > 0.0f ? replay.HeightOrSpan : width);

        ImmutableArray<PointD> primaryBounds = BuildPrimaryBounds(in replay, refPoint, width, height, angleRad);
        ImmutableArray<ImmutableArray<PointD>> additionalContours = BuildAdditionalContours(in spc, in replay, refPoint, angleRad);
        ImmutableArray<PointD> featurePoints = BuildFeaturePoints(in replay, refPoint);

        string label = BuildMarkerLabel(in spc, in replay, nodeNameOverride);

        return new MatchMarker(
            Bounds: primaryBounds,
            ReferencePoint: refPoint,
            AngleRadians: angleRad,
            Label: label,
            Color: color,
            Features: featurePoints)
        {
            AdditionalContours = additionalContours,
            BoundsColor = color,
            BoundsThickness = DefaultBoundsThickness
        };
    }

    /// <summary>
    /// Reconstructs a full <see cref="ResultOverlay"/> from a sequence of saved or live <see cref="VisionInspectionEnvelope"/> records.
    /// </summary>
    public static ResultOverlay ToResultOverlay(
        IReadOnlyList<VisionInspectionEnvelope> envelopes,
        string summaryHeader = "[DB/SPC Replay]")
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        var markers = ImmutableArray.CreateBuilder<MatchMarker>(envelopes.Count);
        int passCount = 0;

        for (int i = 0; i < envelopes.Count; i++)
        {
            VisionInspectionEnvelope env = envelopes[i];
            if (env.IsPass)
            {
                passCount++;
            }

            markers.Add(ToMatchMarker(in env));
        }

        bool allPassed = envelopes.Count > 0 && passCount == envelopes.Count;
        string summary = $"{summaryHeader} {passCount}/{envelopes.Count} Passed";

        return new ResultOverlay(
            Summary: summary,
            Color: allPassed ? ColorPass : ColorFail,
            SearchBoundary: [],
            Matches: markers.MoveToImmutable(),
            ShowFeatures: true);
    }

    private static Vector4 ResolveMarkerColor(in VisionSpcRecord spc)
    {
        if (!spc.IsFound || spc.Verdict == VisionVerdict.Fail || spc.Status == VisionExecutionStatus.InvalidInput)
        {
            return ColorFail;
        }

        if (spc.Verdict == VisionVerdict.Warning)
        {
            return ColorWarning;
        }

        if (spc.Category is VisionToolCategory.PatternMatching or VisionToolCategory.CornerAlignment)
        {
            return ColorFixture;
        }

        return ColorPass;
    }

    private static ImmutableArray<PointD> BuildPrimaryBounds(
        in VisionReplayPayload replay,
        PointD refPoint,
        double width,
        double height,
        double angleRad)
    {
        switch (replay.ShapeKind)
        {
            case ReplayShapeKind.Circle:
                return BuildCircleContour(refPoint, Math.Max(MinShapeDimension, replay.WidthOrRadius));

            case ReplayShapeKind.LineSegment:
                return
                [
                    new PointD(replay.SegmentAStart.X, replay.SegmentAStart.Y),
                    new PointD(replay.SegmentAEnd.X, replay.SegmentAEnd.Y)
                ];

            default:
                PointD boxCenter = new(replay.WorldPose.CenterX, replay.WorldPose.CenterY);
                if (Math.Abs(boxCenter.X) < 1e-6 && Math.Abs(boxCenter.Y) < 1e-6)
                {
                    boxCenter = refPoint;
                }

                return BuildOrientedBox(boxCenter, width, height, angleRad);
        }
    }

    private static ImmutableArray<ImmutableArray<PointD>> BuildAdditionalContours(
        in VisionSpcRecord spc,
        in VisionReplayPayload replay,
        PointD refPoint,
        double angleRad)
    {
        var builder = ImmutableArray.CreateBuilder<ImmutableArray<PointD>>();

        if (spc.Category == VisionToolCategory.PatternMatching && spc.IsFound)
        {
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            PointD xEnd = new(refPoint.X + (FixtureAxisLength * cos), refPoint.Y + (FixtureAxisLength * sin));
            PointD yEnd = new(refPoint.X - (FixtureAxisLength * sin), refPoint.Y + (FixtureAxisLength * cos));
            builder.Add([refPoint, xEnd]);
            builder.Add([refPoint, yEnd]);
        }

        if (replay.ShapeKind is ReplayShapeKind.DualLineCorner or ReplayShapeKind.EdgePair)
        {
            builder.Add([
                new PointD(replay.SegmentAStart.X, replay.SegmentAStart.Y),
                new PointD(replay.SegmentAEnd.X, replay.SegmentAEnd.Y)
            ]);
            builder.Add([
                new PointD(replay.SegmentBStart.X, replay.SegmentBStart.Y),
                new PointD(replay.SegmentBEnd.X, replay.SegmentBEnd.Y)
            ]);
        }

        ReadOnlySpan<ReplaySubRegion> subRegions = replay.SubRegions.Span;
        for (int i = 0; i < subRegions.Length; i++)
        {
            ReplaySubRegion sub = subRegions[i];
            PointD subCenter = new(sub.CenterX, sub.CenterY);
            builder.Add(BuildOrientedBox(
                subCenter,
                Math.Max(MinShapeDimension, sub.Width),
                Math.Max(MinShapeDimension, sub.Height),
                sub.AngleRadians));
        }

        if (replay.ShapeKind == ReplayShapeKind.Polyline && !replay.InlierPointsWorld.IsEmpty)
        {
            ReadOnlySpan<Vector2> pts = replay.InlierPointsWorld.Span;
            var poly = ImmutableArray.CreateBuilder<PointD>(pts.Length);
            for (int i = 0; i < pts.Length; i++)
            {
                poly.Add(new PointD(pts[i].X, pts[i].Y));
            }

            builder.Add(poly.MoveToImmutable());
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<PointD> BuildFeaturePoints(in VisionReplayPayload replay, PointD refPoint)
    {
        int totalPts = 1 + replay.InlierPointsWorld.Length + replay.OutlierPointsWorld.Length;
        var builder = ImmutableArray.CreateBuilder<PointD>(totalPts);
        builder.Add(refPoint);

        ReadOnlySpan<Vector2> inliers = replay.InlierPointsWorld.Span;
        for (int i = 0; i < inliers.Length; i++)
        {
            builder.Add(new PointD(inliers[i].X, inliers[i].Y));
        }

        ReadOnlySpan<Vector2> outliers = replay.OutlierPointsWorld.Span;
        for (int i = 0; i < outliers.Length; i++)
        {
            builder.Add(new PointD(outliers[i].X, outliers[i].Y));
        }

        return builder.MoveToImmutable();
    }

    private static string BuildMarkerLabel(in VisionSpcRecord spc, in VisionReplayPayload replay, string? nodeNameOverride)
    {
        string prefix = !string.IsNullOrWhiteSpace(nodeNameOverride)
            ? $"{nodeNameOverride} · "
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(replay.SummaryLabel))
        {
            return prefix + replay.SummaryLabel;
        }

        if (spc.Category == VisionToolCategory.PatternMatching && spc.MatchedModelIndex >= 0)
        {
            return $"{prefix}Model #{spc.MatchedModelIndex} (ID:{spc.MatchedModelId}) · Score={spc.QualityScore:F2} · θ={spc.WorldAngleDeg:+0.0;-0.0;0.0}°";
        }

        return $"{prefix}{spc.Category} · Val={spc.PrimaryMetric:F2} · Q={spc.QualityScore:F2}";
    }

    private static ImmutableArray<PointD> BuildOrientedBox(PointD center, double width, double height, double angleRad)
    {
        double hw = width * 0.5;
        double hh = height * 0.5;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);

        PointD Rotate(double lx, double ly)
            => new(center.X + (lx * cos) - (ly * sin), center.Y + (lx * sin) + (ly * cos));

        return
        [
            Rotate(-hw, -hh),
            Rotate(hw, -hh),
            Rotate(hw, hh),
            Rotate(-hw, hh)
        ];
    }

    private static ImmutableArray<PointD> BuildCircleContour(PointD center, double radius)
    {
        var pts = ImmutableArray.CreateBuilder<PointD>(CirclePolygonSegments + 1);
        for (int i = 0; i <= CirclePolygonSegments; i++)
        {
            double ang = (2.0 * Math.PI * i) / CirclePolygonSegments;
            pts.Add(new PointD(center.X + (radius * Math.Cos(ang)), center.Y + (radius * Math.Sin(ang))));
        }

        return pts.MoveToImmutable();
    }
}
