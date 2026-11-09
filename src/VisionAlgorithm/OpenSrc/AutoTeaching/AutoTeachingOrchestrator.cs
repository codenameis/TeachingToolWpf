// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/AutoTeachingOrchestrator.cs
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// High-level orchestrator that recognizes all registered parts from a <see cref="PartModelPool"/> on a new product
/// image, summarizes per-part counts (e.g. <c>"#1: 6개, #2: 4개"</c>), and instantiates the full parent-child
/// inspection tree via <see cref="FixturePose2D.Compose"/>.
/// </summary>
public static class AutoTeachingOrchestrator
{
    /// <summary>
    /// Executes multi-part recognition and automatic parent-child ROI tree instantiation on a raw 8-bit image buffer.
    /// </summary>
    public static AutoTeachingResult RecognizeAndAutoTeach(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        PartModelPool pool,
        VisionWorkspace workspace,
        float minGeometricScore = MultiPartPoolRecognizer.DefaultMinGeometricScore,
        float minAppearanceCosine = MultiPartPoolRecognizer.DefaultMinAppearanceCosine,
        float maxMahalanobisDistance = MultiPartPoolRecognizer.DefaultMaxMahalanobisDistance)
    {
        ReadOnlyImageView2D<byte> view = new(image, width, height, stride);
        return RecognizeAndAutoTeach(view, pool, workspace, minGeometricScore, minAppearanceCosine, maxMahalanobisDistance);
    }

    /// <summary>
    /// Executes multi-part recognition and automatic parent-child ROI tree instantiation on a <see cref="ReadOnlyImageView2D{T}"/>.
    /// </summary>
    public static AutoTeachingResult RecognizeAndAutoTeach(
        ReadOnlyImageView2D<byte> image,
        PartModelPool pool,
        VisionWorkspace workspace,
        float minGeometricScore = MultiPartPoolRecognizer.DefaultMinGeometricScore,
        float minAppearanceCosine = MultiPartPoolRecognizer.DefaultMinAppearanceCosine,
        float maxMahalanobisDistance = MultiPartPoolRecognizer.DefaultMaxMahalanobisDistance)
    {
        image.EnsureValid();
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(workspace);

        List<DetectedPartInstance> instances = MultiPartPoolRecognizer.RecognizeAllParts(
            image,
            pool,
            workspace,
            out int sharedPyramidLevels,
            minGeometricScore,
            minAppearanceCosine,
            maxMahalanobisDistance);

        var counts = new Dictionary<PartModelId, int>(pool.Count);
        foreach (PartPoolEntry entry in pool.Entries)
        {
            counts[entry.Id] = 0;
        }

        foreach (DetectedPartInstance inst in instances)
        {
            counts[inst.PartId] = counts.GetValueOrDefault(inst.PartId) + 1;
        }

        var sb = new StringBuilder();
        bool first = true;
        foreach (PartPoolEntry entry in pool.Entries)
        {
            int c = counts[entry.Id];
            if (!first)
            {
                sb.Append(", ");
            }

            sb.Append($"{entry.Id}: {c}개");
            first = false;
        }

        return new AutoTeachingResult(
            instances.ToArray(),
            counts,
            sb.ToString(),
            sharedPyramidLevels);
    }
}
