namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>제한된 검증 예산을 거의 같은 자세가 독점하지 않도록 방문 순서만 정한다.</summary>
internal static class VerificationCandidateScheduler
{
    public static PoseCandidate[] Select(IReadOnlyList<PoseCandidate> candidates, ShapeModel model, int maximumCandidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Count <= maximumCandidates)
        {
            return candidates.ToArray();
        }

        var width = model.Bounds.Max(point => point.X) - model.Bounds.Min(point => point.X);
        var height = model.Bounds.Max(point => point.Y) - model.Bounds.Min(point => point.Y);
        var neighborhood = Math.Clamp(Math.Min(width, height) * 0.05, 0.5, 4);
        var radius = model.Bounds.Max(point => Math.Sqrt((point.X * point.X) + (point.Y * point.Y)));
        var visited = new Dictionary<(long X, long Y), List<PoseSignature>>();
        var selected = new List<PoseCandidate>(maximumCandidates);
        var selectedIndices = new HashSet<int>();
        for (var index = 0; index < candidates.Count && selected.Count < maximumCandidates; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = PoseSignature.Create(candidates[index].Pose);
            var cell = Cell(signature, neighborhood);
            if (HasNearbyPose(visited, signature, cell, neighborhood, radius))
            {
                continue;
            }
            if (!visited.TryGetValue(cell, out var occupants))
            {
                occupants = [];
                visited.Add(cell, occupants);
            }
            occupants.Add(signature);
            selected.Add(candidates[index]);
            selectedIndices.Add(index);
        }

        // 대표 후보가 최종 윤곽 검증에 실패할 수 있으므로 다른 자세를 삭제하지 않는다.
        // 남은 예산에는 원래 점수순의 후보를 다시 배정한다. NMS는 최종 점수 계산 후에만 한다.
        for (var index = 0; index < candidates.Count && selected.Count < maximumCandidates; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selectedIndices.Contains(index))
            {
                selected.Add(candidates[index]);
            }
        }
        return selected.ToArray();
    }

    private static (long X, long Y) Cell(PoseSignature signature, double size)
        => ((long)Math.Floor(signature.X / size), (long)Math.Floor(signature.Y / size));

    private static bool HasNearbyPose(Dictionary<(long X, long Y), List<PoseSignature>> visited,
        PoseSignature candidate, (long X, long Y) cell, double neighborhood, double radius)
    {
        for (var y = cell.Y - 1; y <= cell.Y + 1; y++)
        {
            for (var x = cell.X - 1; x <= cell.X + 1; x++)
            {
                if (!visited.TryGetValue((x, y), out var occupants))
                {
                    continue;
                }
                foreach (var previous in occupants)
                {
                    var dx = candidate.X - previous.X;
                    var dy = candidate.Y - previous.Y;
                    var translation = Math.Sqrt((dx * dx) + (dy * dy));
                    if (translation > neighborhood)
                    {
                        continue;
                    }
                    var da = candidate.CosScale - previous.CosScale;
                    var db = candidate.SinScale - previous.SinScale;
                    // 모든 모델 경계점의 이동량 상계다. 같은 중심의 다른 회전·배율을 구별한다.
                    var deformation = radius * Math.Sqrt((da * da) + (db * db));
                    if (translation + deformation <= neighborhood)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private readonly record struct PoseSignature(double X, double Y, double CosScale, double SinScale)
    {
        public static PoseSignature Create(MatchPose pose)
            => new(pose.X, pose.Y, pose.Scale * Math.Cos(pose.Angle), pose.Scale * Math.Sin(pose.Angle));
    }
}
