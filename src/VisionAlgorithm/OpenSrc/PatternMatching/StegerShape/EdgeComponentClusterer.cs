namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// Clusters dense subpixel edge points into tangent-continuous connected components via path-compressed Union-Find
/// and builds cohesive <see cref="ComponentStatistics"/> per component.
/// </summary>
internal static class EdgeComponentClusterer
{
    public static (ModelFeature[] DenseModel, List<ComponentStatistics> Components) ClusterAndSummarize(
        List<ShapeEdgePoint> denseCandidates,
        double referenceX,
        double referenceY,
        double cos,
        double sin,
        SymmetryHeuristicLimits limits)
    {
        var count = denseCandidates.Count;
        var parent = new int[count];
        for (var i = 0; i < count; i++)
        {
            parent[i] = i;
        }

        for (var i = 0; i < count; i++)
        {
            var a = denseCandidates[i];
            for (var j = i + 1; j < count; j++)
            {
                var b = denseCandidates[j];
                if (!AreTangentConnected(a, b, limits))
                {
                    continue;
                }

                var rootA = FindRoot(parent, i);
                var rootB = FindRoot(parent, j);
                if (rootA != rootB)
                {
                    parent[rootB] = rootA;
                }
            }
        }

        var denseModel = new ModelFeature[count];
        var byRoot = new Dictionary<int, ComponentStatistics>();
        var orderedComponents = new List<ComponentStatistics>();

        for (var i = 0; i < count; i++)
        {
            var edge = denseCandidates[i];
            var rx = edge.X - referenceX;
            var ry = edge.Y - referenceY;
            var mx = (cos * rx) + (sin * ry);
            var my = (-sin * rx) + (cos * ry);
            var mnx = (cos * edge.NormalX) + (sin * edge.NormalY);
            var mny = (-sin * edge.NormalX) + (cos * edge.NormalY);
            denseModel[i] = new ModelFeature(mx, my, mnx, mny);

            var root = FindRoot(parent, i);
            if (byRoot.TryGetValue(root, out var stats))
            {
                stats.IncludeBoundsPoint(mx, my);
            }
            else
            {
                stats = new ComponentStatistics(root, mx, my);
                byRoot.Add(root, stats);
                orderedComponents.Add(stats);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var root = FindRoot(parent, i);
            byRoot[root].AccumulateMomentsAndQuadrants(denseModel[i]);
        }

        return (denseModel, orderedComponents);
    }

    private static bool AreTangentConnected(ShapeEdgePoint a, ShapeEdgePoint b, SymmetryHeuristicLimits limits)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var distanceSquared = (dx * dx) + (dy * dy);
        if (distanceSquared > limits.ClusterMaxDistanceSquared)
        {
            return false;
        }

        var normalDot = (a.NormalX * b.NormalX) + (a.NormalY * b.NormalY);
        if (normalDot < limits.ClusterMinNormalDot)
        {
            return false;
        }

        if (distanceSquared > limits.ClusterTangentGateDistanceSquared)
        {
            var tangentX = -a.NormalY;
            var tangentY = a.NormalX;
            var cross = Math.Abs(((b.X - a.X) * a.NormalX) + ((b.Y - a.Y) * a.NormalY));
            var along = Math.Abs(((b.X - a.X) * tangentX) + ((b.Y - a.Y) * tangentY));
            if (cross > limits.ClusterMaxNormalCrossOffset || along < cross)
            {
                return false;
            }
        }

        return true;
    }

    private static int FindRoot(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }
}
