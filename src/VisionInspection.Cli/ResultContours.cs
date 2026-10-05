using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace VisionInspection.Cli;

internal static class ResultContours
{
    public static ImagePoint[] Transform(RoiGeometry teachingRoi, ImagePoint center, double angleDegrees, double scale)
        => teachingRoi.WithFrame(new(center.X, center.Y), teachingRoi.Width * scale, teachingRoi.Height * scale, angleDegrees)
            .Outline.Select(point => new ImagePoint(point.X, point.Y)).ToArray();

    public static DetectionResult FromShape(RoiGeometry teachingRoi, ShapeMatch match)
    {
        var pose = match.Pose;
        var quality = match.Geometry;
        var refinement = match.Refinement;
        return new()
        {
            Center = new(pose.X, pose.Y),
            AngleRadians = pose.Angle,
            Scale = pose.Scale,
            Score = match.Score,
            DirectionScore = match.DirectionScore,
            Support = match.Support,
            ClutterRatio = match.ClutterRatio,
            ClutterPenaltyRatio = match.ClutterPenaltyRatio,
            Contour = Transform(teachingRoi, new(pose.X, pose.Y), pose.Angle * 180 / Math.PI, pose.Scale),
            Geometry = quality is null ? null : new(quality.Coverage, FiniteOrNull(quality.FitErrorPixels), quality.ClutterRatio,
                quality.MatchedFeatures, quality.ModelFeatures, quality.SceneEdges)
            {
                ClutterEnergyRatio = quality.ClutterEnergyRatio
            },
            Refinement = new(refinement.Status.ToString(), FiniteOrNull(refinement.InitialResidual), FiniteOrNull(refinement.FinalResidual),
                refinement.Iterations, refinement.Correspondences, refinement.AngleObservable)
        };
    }

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;
}
