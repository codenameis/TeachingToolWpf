using VisionGeometry;

namespace TeachingToolWpf.Diagnostics;

/// <summary>정답은 검색에 전달하지 않는다. 검출 개수와 독립적으로 pose를 일대일 비교한다.</summary>
internal static class TdcmAccuracy
{
    internal readonly record struct Pose(PointD Center, double AngleDegrees);
    internal static PoseMatch.Tdcm.PoseAccuracy.Report Evaluate(IReadOnlyList<Pose> expected, IReadOnlyList<Pose> actual,
        double associationRadius, bool groundTruthVerified)
        => PoseMatch.Tdcm.PoseAccuracy.Evaluate(expected.Select(Convert).ToArray(), actual.Select(Convert).ToArray(), associationRadius, groundTruthVerified);

    private static PoseMatch.Tdcm.PoseMatchResult Convert(Pose pose) => new(pose.Center.X, pose.Center.Y, 36, 36, pose.AngleDegrees, 1);
    internal static Pose[] SamplePins() =>
        (from cy in Enumerable.Range(0, 4)
         from cx in Enumerable.Range(0, 4)
         from row in Enumerable.Range(0, 2)
         from column in Enumerable.Range(0, 8)
         select new Pose(new(cx * 2048 + (column + 2) * 180 + 39.518,
             cy * 2048 + (row == 0 ? 546.178 : 1456.178)), 0)).ToArray();

    // 이전 수동 근사 좌표다. Kensa 원본의 정밀 라벨로 검증하기 전에는 합격을 선언하지 않는다.
    internal static Pose[] ApproximateKensaParts() =>
        (from y in new[] { 184, 245, 499, 560, 819, 879, 1130, 1190 }
         from x in new[] { 200, 296, 466, 558, 726, 822 }
         select new Pose(new(x, y), 0)).ToArray();
}
