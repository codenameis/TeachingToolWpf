namespace PoseMatch.Tdcm;

/// <summary>정답 pose와 검출 pose의 일대일 비교. 허용 오차는 위치1px/각도1°다.</summary>
public static class PoseAccuracy
{
    public sealed record Report(int Expected, int Found, int Missing, int FalsePositives, int Duplicates,
        int PositionFailures, int AngleFailures, double? MaximumPositionError, double? MaximumAngleError,
        bool GroundTruthVerified, bool Passed);

    public static Report Evaluate(IReadOnlyList<PoseMatchResult> expected, IReadOnlyList<PoseMatchResult> actual,
        double associationRadius, bool groundTruthVerified)
    {
        var pairs = (from e in Enumerable.Range(0, expected.Count)
                     from a in Enumerable.Range(0, actual.Count)
                     let distance = Math.Sqrt(Math.Pow(expected[e].X - actual[a].X, 2) + Math.Pow(expected[e].Y - actual[a].Y, 2))
                     where distance <= associationRadius
                     orderby distance, e, a
                     select (e, a, distance)).ToArray();
        var assignedExpected = new HashSet<int>();
        var assignedActual = new HashSet<int>();
        var positionFailures = 0;
        var angleFailures = 0;
        double? maximumPosition = null;
        double? maximumAngle = null;
        foreach (var (e, a, distance) in pairs)
        {
            if (assignedExpected.Contains(e) || assignedActual.Contains(a))
            {
                continue;
            }

            assignedExpected.Add(e);
            assignedActual.Add(a);
            // 각도 오차가 정답 위치까지 이동시키지 않도록 정답 pose는 고정한다.
            var angle = Math.Abs(Math.IEEERemainder(actual[a].AngleDegrees - expected[e].AngleDegrees, 360));
            maximumPosition = Math.Max(maximumPosition ?? 0, distance);
            maximumAngle = Math.Max(maximumAngle ?? 0, angle);
            if (distance > 1)
            {
                positionFailures++;
            }

            if (angle > 1)
            {
                angleFailures++;
            }
        }
        var duplicates = Enumerable.Range(0, actual.Count).Count(a => !assignedActual.Contains(a) && pairs.Any(p => p.a == a));
        var falsePositives = actual.Count - assignedActual.Count - duplicates;
        var missing = expected.Count - assignedExpected.Count;
        return new(expected.Count, assignedExpected.Count, missing, falsePositives, duplicates,
            positionFailures, angleFailures, maximumPosition, maximumAngle, groundTruthVerified,
            groundTruthVerified && missing == 0 && falsePositives == 0 && duplicates == 0 &&
            positionFailures == 0 && angleFailures == 0);
    }

}
