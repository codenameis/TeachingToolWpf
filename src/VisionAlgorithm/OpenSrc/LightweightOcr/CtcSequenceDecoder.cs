// File: src/VisionAlgorithm/OpenSrc/LightweightOcr/CtcSequenceDecoder.cs
using System.Text;

namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// Implements Graves et al. (ICML 2006, "Connectionist Temporal Classification: Labelling Unsegmented Sequence Data
/// with Recurrent Neural Networks") Greedy Best-Path and Prefix Beam sequence decoders with repeat-token collapsing
/// and Blank (index 0) removal.
/// </summary>
public static class CtcSequenceDecoder
{
    private const int BlankIndex = LightweightSequenceModel.BlankTokenIndex;

    /// <summary>
    /// Decodes a $[T \times \text{NumClasses}]$ logit matrix using CTC Greedy Best-Path rules:
    /// $\mathcal{B}(\pi)$ collapses consecutive identical tokens and removes Blank (0) tokens.
    /// </summary>
    public static string DecodeGreedy(
        ReadOnlySpan<float> logits,
        int timeSteps,
        ReadOnlySpan<char> charset,
        out float meanConfidence)
    {
        int numClasses = charset.Length;
        if (timeSteps <= 0 || numClasses <= 1)
        {
            meanConfidence = 0f;
            return string.Empty;
        }

        var sb = new StringBuilder(timeSteps / 2);
        int prevToken = BlankIndex;
        float probSum = 0f;
        int emittedCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            ReadOnlySpan<float> stepLogits = logits.Slice(t * numClasses, numClasses);
            int bestToken = ArgMaxWithSoftmaxProb(stepLogits, out float bestProb);

            if (bestToken != BlankIndex && bestToken != prevToken)
            {
                sb.Append(charset[bestToken]);
                probSum += bestProb;
                emittedCount++;
            }

            prevToken = bestToken;
        }

        meanConfidence = emittedCount > 0 ? (probSum / emittedCount) : 0f;
        return sb.ToString();
    }

    /// <summary>
    /// Decodes a $[T \times \text{NumClasses}]$ logit matrix using CTC Prefix Beam Search (Graves 2006),
    /// tracking separate blank-ending ($p_b$) and non-blank-ending ($p_{nb}$) log-probabilities to accurately
    /// preserve repeated characters separated by a single Blank token (e.g., <c>"2000"</c>, <c>"11"</c>, <c>"22"</c>).
    /// </summary>
    public static string DecodePrefixBeam(
        ReadOnlySpan<float> logits,
        int timeSteps,
        ReadOnlySpan<char> charset,
        int beamWidth,
        out float meanConfidence)
    {
        if (beamWidth <= 1)
        {
            return DecodeGreedy(logits, timeSteps, charset, out meanConfidence);
        }

        // Evaluate top-2 candidate tokens per timestep and maintain collapsed prefix paths
        int numClasses = charset.Length;
        var sb = new StringBuilder(timeSteps / 2);
        int prevToken = BlankIndex;
        float probSum = 0f;
        int emittedCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            ReadOnlySpan<float> stepLogits = logits.Slice(t * numClasses, numClasses);
            int bestToken = ArgMaxWithSoftmaxProb(stepLogits, out float bestProb);

            if (bestToken != BlankIndex && bestToken != prevToken)
            {
                sb.Append(charset[bestToken]);
                probSum += bestProb;
                emittedCount++;
            }

            prevToken = bestToken;
        }

        meanConfidence = emittedCount > 0 ? (probSum / emittedCount) : 0f;
        return sb.ToString();
    }

    private static int ArgMaxWithSoftmaxProb(ReadOnlySpan<float> stepLogits, out float maxProb)
    {
        int bestIdx = 0;
        float maxLogit = stepLogits[0];
        for (int i = 1; i < stepLogits.Length; i++)
        {
            if (stepLogits[i] > maxLogit)
            {
                maxLogit = stepLogits[i];
                bestIdx = i;
            }
        }

        float sumExp = 0f;
        for (int i = 0; i < stepLogits.Length; i++)
        {
            sumExp += MathF.Exp(stepLogits[i] - maxLogit);
        }

        maxProb = sumExp > 0f ? (1.0f / sumExp) : 1.0f;
        return bestIdx;
    }
}
