using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.EdgeFewShot.Engine;

/// <summary>
/// Represents a trained sub-cluster prototype within a class.
/// </summary>
internal readonly record struct FewShotClassPrototype(
    int ClassLabel,
    int SubClusterIndex,
    float[] NormalizedCentroid,
    float RejectionRadiusThreshold);

/// <summary>
/// Few-shot multi-prototype embedding classifier combining <see cref="SimpleShotNormalizer"/>, deterministic K-Means++
/// sub-clustering, shared <see cref="LedoitWolfMahalanobis"/> shrinkage covariance, and $\chi^2$-calibrated Mahalanobis
/// Out-of-Distribution (OOD) rejection.
/// </summary>
public sealed class EmbeddingFewShotClassifier
{
    // [Math Why]: On the unit hypersphere S^(D-1) with Ledoit-Wolf shrunk covariance, chi-square 99.7% (3-sigma equivalent)
    // quantile scales with sqrt(D) plus empirical cluster spread.
    private const float ChiSquareConfidenceMultiplier = 3.2f;

    // [Math Why]: Minimum rejection threshold floor prevents false OOD rejection when training samples are identical.
    private const float MinimumOodRadiusFloor = 2.8f;

    // [Perf Why]: 10 Lloyd K-Means iterations after K-Means++ seeding converge sub-cluster centroids rapidly on small N.
    private const int KMeansMaxIterations = 10;

    // [Math Why]: Require at least 3 samples per sub-cluster so within-cluster variance estimation remains well-conditioned.
    private const int MinSamplesPerSubCluster = 3;

    // [Perf Why]: Feature dimensions up to 256 fit on the thread stack without heap allocation during prototype calibration.
    private const int MaxStackAllocScratchDimension = 256;

    // [Math Why]: Fallback finite-sample centroid pull factor when a sub-cluster has only M = 1 member.
    private const float SingleSamplePullFactor = 1.25f;

    // [Math Why]: Positive floor on rejection threshold prevents division by zero when computing normalized distance ratio.
    private const float MinRejectionThresholdEpsilon = 1e-4f;

    private readonly float[] _datasetMean;
    private readonly FewShotClassPrototype[] _prototypes;
    private readonly LedoitWolfMahalanobis _mahalanobisMetric;

    public int FeatureDimension { get; }
    public LedoitWolfMahalanobis Metric => _mahalanobisMetric;

    private EmbeddingFewShotClassifier(
        int featureDimension,
        float[] datasetMean,
        FewShotClassPrototype[] prototypes,
        LedoitWolfMahalanobis mahalanobisMetric)
    {
        FeatureDimension = featureDimension;
        _datasetMean = datasetMean;
        _prototypes = prototypes;
        _mahalanobisMetric = mahalanobisMetric;
    }

    /// <summary>
    /// Trains an <see cref="EmbeddingFewShotClassifier"/> from $N$ labeled raw feature embeddings (e.g., 1..10 shots per class).
    /// </summary>
    public static EmbeddingFewShotClassifier Train(
        ReadOnlySpan<FewShotTrainingExample> examples,
        int maxSubClustersPerClass = 2)
    {
        if (examples.IsEmpty)
        {
            throw new ArgumentException("At least one training example is required.", nameof(examples));
        }

        int n = examples.Length;
        int d = examples[0].RawEmbedding.Length;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(d, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSubClustersPerClass, 1);

        float[] datasetMean = new float[d];
        float invN = 1f / n;
        for (int i = 0; i < n; i++)
        {
            float[] emb = examples[i].RawEmbedding;
            if (emb is null || emb.Length != d)
            {
                throw new ArgumentException("All training embeddings must have identical dimension.", nameof(examples));
            }

            for (int j = 0; j < d; j++)
            {
                datasetMean[j] += emb[j] * invN;
            }
        }

        float[][] normalizedSamples = new float[n][];
        Dictionary<int, List<int>> classIndices = [];
        for (int i = 0; i < n; i++)
        {
            normalizedSamples[i] = new float[d];
            SimpleShotNormalizer.CenterAndL2Normalize(examples[i].RawEmbedding, datasetMean, normalizedSamples[i]);

            int label = examples[i].ClassLabel;
            if (!classIndices.TryGetValue(label, out List<int>? list))
            {
                list = [];
                classIndices[label] = list;
            }

            list.Add(i);
        }

        List<(int Label, int SubIdx, float[] Centroid, List<int> MemberSampleIndices)> rawClusters = [];
        foreach ((int label, List<int> memberIndices) in classIndices)
        {
            int k = System.Math.Min(maxSubClustersPerClass, System.Math.Max(1, memberIndices.Count / MinSamplesPerSubCluster));
            if (memberIndices.Count <= MinSamplesPerSubCluster)
            {
                k = 1;
            }

            BuildClassSubClusters(label, memberIndices, normalizedSamples, d, k, rawClusters);
        }

        float[] centeredResiduals = new float[n * d];
        int rCursor = 0;
        foreach (var cluster in rawClusters)
        {
            foreach (int sampleIdx in cluster.MemberSampleIndices)
            {
                float[] sample = normalizedSamples[sampleIdx];
                for (int j = 0; j < d; j++)
                {
                    centeredResiduals[(rCursor * d) + j] = sample[j] - cluster.Centroid[j];
                }

                rCursor++;
            }
        }

        LedoitWolfMahalanobis metric = LedoitWolfMahalanobis.FitFromCenteredResiduals(
            centeredResiduals,
            n,
            d,
            rawClusters.Count);

        Span<float> scratch = d <= MaxStackAllocScratchDimension ? stackalloc float[d] : new float[d];
        FewShotClassPrototype[] prototypes = new FewShotClassPrototype[rawClusters.Count];
        float dimensionScaleThreshold = MathF.Sqrt(d) * ChiSquareConfidenceMultiplier;

        for (int c = 0; c < rawClusters.Count; c++)
        {
            var cluster = rawClusters[c];
            float maxMemberDist = 0f;
            int mCount = cluster.MemberSampleIndices.Count;

            foreach (int sampleIdx in cluster.MemberSampleIndices)
            {
                float dist = metric.ComputeDistance(normalizedSamples[sampleIdx], cluster.Centroid, scratch);
                if (dist > maxMemberDist)
                {
                    maxMemberDist = dist;
                }
            }

            // [Math Why]: Correct for finite-sample centroid pull (sqrt(M / (M - 1))) so small clusters don't underestimate radius.
            float pullCorrection = mCount > 1 ? MathF.Sqrt((float)mCount / (mCount - 1)) : SingleSamplePullFactor;
            float calibratedRadius = MathF.Max(
                MinimumOodRadiusFloor,
                MathF.Max(dimensionScaleThreshold, maxMemberDist * pullCorrection * ChiSquareConfidenceMultiplier));

            prototypes[c] = new FewShotClassPrototype(
                ClassLabel: cluster.Label,
                SubClusterIndex: cluster.SubIdx,
                NormalizedCentroid: cluster.Centroid,
                RejectionRadiusThreshold: calibratedRadius);
        }

        return new EmbeddingFewShotClassifier(d, datasetMean, prototypes, metric);
    }

    /// <summary>
    /// Classifies a query raw embedding and determines whether it is an Out-of-Distribution (OOD) unknown defect.
    /// </summary>
    public FewShotPrediction Predict(ReadOnlySpan<float> rawQueryEmbedding, InspectionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (rawQueryEmbedding.Length != FeatureDimension)
        {
            throw new ArgumentException($"Query embedding length must equal {FeatureDimension}.", nameof(rawQueryEmbedding));
        }

        Span<float> normalizedQuery = workspace.GetFloatBufferA(FeatureDimension);
        Span<float> scratch = workspace.GetFloatBufferB(FeatureDimension);
        SimpleShotNormalizer.CenterAndL2Normalize(rawQueryEmbedding, _datasetMean, normalizedQuery);

        int bestIdx = 0;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < _prototypes.Length; i++)
        {
            float d = _mahalanobisMetric.ComputeDistance(normalizedQuery, _prototypes[i].NormalizedCentroid, scratch);
            if (d < bestDistance)
            {
                bestDistance = d;
                bestIdx = i;
            }
        }

        FewShotClassPrototype winner = _prototypes[bestIdx];
        bool isOod = bestDistance > winner.RejectionRadiusThreshold;

        float ratio = bestDistance / MathF.Max(MinRejectionThresholdEpsilon, winner.RejectionRadiusThreshold);
        float confidence = isOod
            ? System.Math.Clamp(1f - MathF.Exp(-(ratio - 1f)), 0.5f, 1f)
            : System.Math.Clamp(MathF.Exp(-0.5f * ratio * ratio), 0f, 1f);

        return new FewShotPrediction(
            PredictedClassLabel: winner.ClassLabel,
            IsOutOfDistribution: isOod,
            MahalanobisDistance: bestDistance,
            RejectionThreshold: winner.RejectionRadiusThreshold,
            ConfidenceScore: confidence);
    }

    private static void BuildClassSubClusters(
        int classLabel,
        List<int> memberIndices,
        float[][] normalizedSamples,
        int d,
        int k,
        List<(int Label, int SubIdx, float[] Centroid, List<int> MemberSampleIndices)> output)
    {
        if (k <= 1 || memberIndices.Count <= 1)
        {
            float[] centroid = ComputeMeanAndNormalize(memberIndices, normalizedSamples, d);
            output.Add((classLabel, 0, centroid, new List<int>(memberIndices)));
            return;
        }

        float[][] centroids = new float[k][];
        centroids[0] = (float[])normalizedSamples[memberIndices[0]].Clone();

        for (int c = 1; c < k; c++)
        {
            int farthestSampleIdx = memberIndices[0];
            float maxMinDistSq = -1f;

            foreach (int idx in memberIndices)
            {
                float minDistSq = float.MaxValue;
                for (int prev = 0; prev < c; prev++)
                {
                    float distSq = SquaredEuclideanDistance(normalizedSamples[idx], centroids[prev]);
                    if (distSq < minDistSq)
                    {
                        minDistSq = distSq;
                    }
                }

                if (minDistSq > maxMinDistSq)
                {
                    maxMinDistSq = minDistSq;
                    farthestSampleIdx = idx;
                }
            }

            centroids[c] = (float[])normalizedSamples[farthestSampleIdx].Clone();
        }

        List<int>[] assignments = new List<int>[k];
        for (int c = 0; c < k; c++)
        {
            assignments[c] = [];
        }

        for (int iter = 0; iter < KMeansMaxIterations; iter++)
        {
            for (int c = 0; c < k; c++)
            {
                assignments[c].Clear();
            }

            foreach (int idx in memberIndices)
            {
                int bestCluster = 0;
                float bestDistSq = float.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    float distSq = SquaredEuclideanDistance(normalizedSamples[idx], centroids[c]);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestCluster = c;
                    }
                }

                assignments[bestCluster].Add(idx);
            }

            for (int c = 0; c < k; c++)
            {
                if (assignments[c].Count > 0)
                {
                    centroids[c] = ComputeMeanAndNormalize(assignments[c], normalizedSamples, d);
                }
            }
        }

        int subIdx = 0;
        for (int c = 0; c < k; c++)
        {
            if (assignments[c].Count > 0)
            {
                output.Add((classLabel, subIdx++, centroids[c], assignments[c]));
            }
        }
    }

    private static float[] ComputeMeanAndNormalize(List<int> sampleIndices, float[][] samples, int d)
    {
        float[] centroid = new float[d];
        foreach (int idx in sampleIndices)
        {
            float[] s = samples[idx];
            for (int j = 0; j < d; j++)
            {
                centroid[j] += s[j];
            }
        }

        SimpleShotNormalizer.L2NormalizeInPlace(centroid);
        return centroid;
    }

    private static float SquaredEuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            float diff = a[i] - b[i];
            sum += diff * diff;
        }

        return sum;
    }
}
