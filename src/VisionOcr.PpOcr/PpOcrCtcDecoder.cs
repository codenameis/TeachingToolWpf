using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VisionOcr.PpOcr;

/// <summary>
/// Official PaddleOCR <c>BaseRecLabelDecode</c> CTC collapse &amp; calibrated softmax probability decoder,
/// using the first-timestep probability of each collapsed non-blank token run (including emitted space tokens)
/// and inverse-mapping rotated crop coordinates back into the caller's original ROI frame.
/// </summary>
internal static class PpOcrCtcDecoder
{
    internal enum CropOrientation
    {
        Upright = 0,
        Rotate180 = 1,
        Rotate90Counterclockwise = 2,
        Rotate90Clockwise = 3,
    }

    internal sealed record DecodedCropLine(
        string Text,
        IReadOnlyList<PpOcrCharacterBox> Characters,
        float MeanConfidence,
        float MinConfidence)
    {
        public static DecodedCropLine Empty { get; } = new(string.Empty, [], 0f, 0f);
    }

    private const int CtcBlankIndex = 0;
    private const float ProbabilityRowSumTolerance = 0.025f;
    private const float ProbabilityMinFloor = -0.001f;
    private const float ProbabilityMaxCeiling = 1.001f;

    public static DecodedCropLine DecodeBatchItem(
        PpOcrSessionHost.RecognizerSlot slot,
        Tensor<float> output,
        int batchIndex,
        int timeSteps,
        int numClasses,
        int targetWidth,
        int resizedContentWidth,
        int orientedCropWidth,
        int orientedCropHeight,
        ReadOnlySpan<bool> allowedMask)
    {
        float activeStepLimit = Math.Max(1.0f, (resizedContentWidth / (float)targetWidth) * timeSteps);
        int maxValidTimeStep = Math.Clamp((int)MathF.Ceiling(activeStepLimit) + 1, 1, timeSteps);

        DenseTensor<float>? denseOutput = output as DenseTensor<float>;
        ReadOnlySpan<float> batchSpan = denseOutput is not null
            ? denseOutput.Buffer.Span.Slice(batchIndex * timeSteps * numClasses, timeSteps * numClasses)
            : default;

        return DecodeCore(
            slot,
            output,
            batchSpan,
            hasContiguousSpan: denseOutput is not null,
            batchIndex,
            maxValidTimeStep,
            timeSteps,
            numClasses,
            targetWidth,
            resizedContentWidth,
            orientedCropWidth,
            orientedCropHeight,
            allowedMask);
    }

    public static DecodedCropLine DecodeContiguousMatrix(
        ReadOnlySpan<char> vocabulary,
        ReadOnlySpan<float> matrix,
        int timeSteps,
        int numClasses,
        int targetWidth,
        int resizedContentWidth,
        int orientedCropWidth,
        int orientedCropHeight,
        ReadOnlySpan<bool> allowedMask)
    {
        float activeStepLimit = Math.Max(1.0f, (resizedContentWidth / (float)targetWidth) * timeSteps);
        int maxValidTimeStep = Math.Clamp((int)MathF.Ceiling(activeStepLimit) + 1, 1, timeSteps);

        var sb = new StringBuilder();
        var chars = new List<PpOcrCharacterBox>();
        int activeRunClass = CtcBlankIndex;
        int runStartStep = 0;
        int runEndStep = 0;
        float runFirstProb = 0f;
        bool pendingSpace = false;
        float pendingSpaceProb = 1.0f;
        float sumConf = 0f;
        int confCount = 0;
        float minConf = 1.0f;

        for (int t = 0; t < maxValidTimeStep; t++)
        {
            int bestIdx = FindBestAllowedClassFromRow(
                matrix.Slice(t * numClasses, numClasses),
                numClasses,
                allowedMask,
                out float bestProb);

            if (bestIdx == activeRunClass && bestIdx != CtcBlankIndex)
            {
                runEndStep = t;
            }
            else
            {
                if (activeRunClass != CtcBlankIndex)
                {
                    FlushVocabularyTokenRun(
                        vocabulary,
                        activeRunClass,
                        runStartStep,
                        runEndStep,
                        runFirstProb,
                        timeSteps,
                        targetWidth,
                        resizedContentWidth,
                        orientedCropWidth,
                        orientedCropHeight,
                        sb,
                        chars,
                        ref pendingSpace,
                        ref pendingSpaceProb,
                        ref sumConf,
                        ref confCount,
                        ref minConf);
                }

                activeRunClass = bestIdx;
                runStartStep = t;
                runEndStep = t;
                runFirstProb = Math.Clamp(bestProb, 0f, 1f);
            }
        }

        if (activeRunClass != CtcBlankIndex)
        {
            FlushVocabularyTokenRun(
                vocabulary,
                activeRunClass,
                runStartStep,
                runEndStep,
                runFirstProb,
                timeSteps,
                targetWidth,
                resizedContentWidth,
                orientedCropWidth,
                orientedCropHeight,
                sb,
                chars,
                ref pendingSpace,
                ref pendingSpaceProb,
                ref sumConf,
                ref confCount,
                ref minConf);
        }

        if (chars.Count == 0 || sb.Length == 0 || confCount == 0)
        {
            return DecodedCropLine.Empty;
        }

        return new DecodedCropLine(
            sb.ToString(),
            chars,
            sumConf / confCount,
            minConf);
    }

    public static DecodedCropLine RemapOrientationToOriginalFrame(
        DecodedCropLine decoded,
        CropOrientation orientation,
        int originalWidth,
        int originalHeight)
    {
        if (orientation == CropOrientation.Upright || decoded.Characters.Count == 0)
        {
            return decoded;
        }

        int maxOrigX = Math.Max(0, originalWidth - 1);
        int maxOrigY = Math.Max(0, originalHeight - 1);
        var remapped = new PpOcrCharacterBox[decoded.Characters.Count];

        for (int i = 0; i < decoded.Characters.Count; i++)
        {
            PpOcrCharacterBox c = decoded.Characters[i];
            int startCol;
            int endCol;
            int topRow;
            int bottomRow;

            switch (orientation)
            {
                case CropOrientation.Rotate180:
                    // Rotate180 crop has dimensions (originalWidth, originalHeight): x_orig = (W - 1) - x_rot, y_orig = (H - 1) - y_rot.
                    startCol = Math.Clamp(maxOrigX - c.EndCol, 0, maxOrigX);
                    endCol = Math.Clamp(maxOrigX - c.StartCol, startCol, maxOrigX);
                    topRow = Math.Clamp(maxOrigY - c.BottomRow, 0, maxOrigY);
                    bottomRow = Math.Clamp(maxOrigY - c.TopRow, topRow, maxOrigY);
                    break;

                case CropOrientation.Rotate90Counterclockwise:
                    // Rotate90CCW maps (x_orig, y_orig) -> (x_rot = y_orig, y_rot = (W - 1) - x_orig).
                    // Inverse: x_orig = (W - 1) - y_rot, y_orig = x_rot.
                    startCol = Math.Clamp(maxOrigX - c.BottomRow, 0, maxOrigX);
                    endCol = Math.Clamp(maxOrigX - c.TopRow, startCol, maxOrigX);
                    topRow = Math.Clamp(c.StartCol, 0, maxOrigY);
                    bottomRow = Math.Clamp(c.EndCol, topRow, maxOrigY);
                    break;

                case CropOrientation.Rotate90Clockwise:
                    // Rotate90CW maps (x_orig, y_orig) -> (x_rot = (H - 1) - y_orig, y_rot = x_orig).
                    // Inverse: x_orig = y_rot, y_orig = (H - 1) - x_rot.
                    startCol = Math.Clamp(c.TopRow, 0, maxOrigX);
                    endCol = Math.Clamp(c.BottomRow, startCol, maxOrigX);
                    topRow = Math.Clamp(maxOrigY - c.EndCol, 0, maxOrigY);
                    bottomRow = Math.Clamp(maxOrigY - c.StartCol, topRow, maxOrigY);
                    break;

                default:
                    startCol = c.StartCol;
                    endCol = c.EndCol;
                    topRow = c.TopRow;
                    bottomRow = c.BottomRow;
                    break;
            }

            remapped[i] = new PpOcrCharacterBox(
                c.Character,
                c.Confidence,
                c.LineIndex,
                startCol,
                endCol,
                topRow,
                bottomRow,
                c.PrecededBySpace);
        }

        return decoded with { Characters = remapped };
    }

    private static DecodedCropLine DecodeCore(
        PpOcrSessionHost.RecognizerSlot slot,
        Tensor<float> fallbackTensor,
        ReadOnlySpan<float> batchSpan,
        bool hasContiguousSpan,
        int batchIndex,
        int maxValidTimeStep,
        int timeSteps,
        int numClasses,
        int targetWidth,
        int resizedContentWidth,
        int orientedCropWidth,
        int orientedCropHeight,
        ReadOnlySpan<bool> allowedMask)
    {
        var sb = new StringBuilder();
        var chars = new List<PpOcrCharacterBox>();

        int activeRunClass = CtcBlankIndex;
        int runStartStep = 0;
        int runEndStep = 0;
        float runFirstProb = 0f;
        bool pendingSpace = false;
        float pendingSpaceProb = 1.0f;
        float sumConf = 0f;
        int confCount = 0;
        float minConf = 1.0f;

        for (int t = 0; t < maxValidTimeStep; t++)
        {
            int bestIdx = hasContiguousSpan
                ? FindBestAllowedClassFromRow(batchSpan.Slice(t * numClasses, numClasses), numClasses, allowedMask, out float bestProb)
                : FindBestAllowedClassFromTensor(fallbackTensor, batchIndex, t, numClasses, allowedMask, out bestProb);

            if (bestIdx == activeRunClass && bestIdx != CtcBlankIndex)
            {
                // [Math Why]: Official PaddleOCR BaseRecLabelDecode(is_remove_duplicate=True) selects index t_start
                // (`selection[1:] = text_index[1:] != text_index[:-1]`), using `text_prob[t_start]` (runFirstProb), NOT run max.
                runEndStep = t;
            }
            else
            {
                if (activeRunClass != CtcBlankIndex)
                {
                    FlushSlotTokenRun(
                        slot,
                        activeRunClass,
                        runStartStep,
                        runEndStep,
                        runFirstProb,
                        timeSteps,
                        targetWidth,
                        resizedContentWidth,
                        orientedCropWidth,
                        orientedCropHeight,
                        sb,
                        chars,
                        ref pendingSpace,
                        ref pendingSpaceProb,
                        ref sumConf,
                        ref confCount,
                        ref minConf);
                }

                activeRunClass = bestIdx;
                runStartStep = t;
                runEndStep = t;
                runFirstProb = Math.Clamp(bestProb, 0f, 1f);
            }
        }

        if (activeRunClass != CtcBlankIndex)
        {
            FlushSlotTokenRun(
                slot,
                activeRunClass,
                runStartStep,
                runEndStep,
                runFirstProb,
                timeSteps,
                targetWidth,
                resizedContentWidth,
                orientedCropWidth,
                orientedCropHeight,
                sb,
                chars,
                ref pendingSpace,
                ref pendingSpaceProb,
                ref sumConf,
                ref confCount,
                ref minConf);
        }

        if (chars.Count == 0 || sb.Length == 0 || confCount == 0)
        {
            return DecodedCropLine.Empty;
        }

        return new DecodedCropLine(
            sb.ToString(),
            chars,
            sumConf / confCount,
            minConf);
    }

    private static int FindBestAllowedClassFromRow(
        ReadOnlySpan<float> row,
        int numClasses,
        ReadOnlySpan<bool> allowedMask,
        out float calibratedBestProbability)
    {
        float minVal = float.PositiveInfinity;
        float maxVal = float.NegativeInfinity;
        float sum = 0f;
        int bestIdx = CtcBlankIndex;
        float bestRaw = float.NegativeInfinity;

        for (int k = 0; k < numClasses; k++)
        {
            float v = row[k];
            if (v < minVal) minVal = v;
            if (v > maxVal) maxVal = v;
            sum += v;

            if (allowedMask[k] && v > bestRaw)
            {
                bestRaw = v;
                bestIdx = k;
            }
        }

        bool isAlreadySoftmax = minVal >= ProbabilityMinFloor
            && maxVal <= ProbabilityMaxCeiling
            && MathF.Abs(sum - 1.0f) <= ProbabilityRowSumTolerance;

        if (isAlreadySoftmax)
        {
            calibratedBestProbability = bestRaw;
            return bestIdx;
        }

        double expSum = 0.0;
        for (int k = 0; k < numClasses; k++)
        {
            expSum += Math.Exp(row[k] - maxVal);
        }

        calibratedBestProbability = expSum > 0.0
            ? (float)(Math.Exp(bestRaw - maxVal) / expSum)
            : (1.0f / numClasses);
        return bestIdx;
    }

    private static int FindBestAllowedClassFromTensor(
        Tensor<float> fallbackTensor,
        int batchIndex,
        int timeStep,
        int numClasses,
        ReadOnlySpan<bool> allowedMask,
        out float calibratedBestProbability)
    {
        float minVal = float.PositiveInfinity;
        float maxVal = float.NegativeInfinity;
        float sum = 0f;
        int bestIdx = CtcBlankIndex;
        float bestRaw = float.NegativeInfinity;

        for (int k = 0; k < numClasses; k++)
        {
            float v = fallbackTensor[batchIndex, timeStep, k];
            if (v < minVal) minVal = v;
            if (v > maxVal) maxVal = v;
            sum += v;

            if (allowedMask[k] && v > bestRaw)
            {
                bestRaw = v;
                bestIdx = k;
            }
        }

        bool tensorAlreadySoftmax = minVal >= ProbabilityMinFloor
            && maxVal <= ProbabilityMaxCeiling
            && MathF.Abs(sum - 1.0f) <= ProbabilityRowSumTolerance;

        if (tensorAlreadySoftmax)
        {
            calibratedBestProbability = bestRaw;
            return bestIdx;
        }

        double fallbackExpSum = 0.0;
        for (int k = 0; k < numClasses; k++)
        {
            fallbackExpSum += Math.Exp(fallbackTensor[batchIndex, timeStep, k] - maxVal);
        }

        calibratedBestProbability = fallbackExpSum > 0.0
            ? (float)(Math.Exp(bestRaw - maxVal) / fallbackExpSum)
            : (1.0f / numClasses);
        return bestIdx;
    }

    private static void FlushSlotTokenRun(
        PpOcrSessionHost.RecognizerSlot slot,
        int classIdx,
        int startStep,
        int endStep,
        float firstProb,
        int totalTimeSteps,
        int targetWidth,
        int resizedContentWidth,
        int orientedWidth,
        int orientedHeight,
        StringBuilder sb,
        List<PpOcrCharacterBox> chars,
        ref bool pendingSpace,
        ref float pendingSpaceProb,
        ref float sumConf,
        ref int confCount,
        ref float minConf)
    {
        if (!slot.TryGetCharacter(classIdx, out char ch))
        {
            return;
        }

        EmitDecodedCharacter(
            ch,
            startStep,
            endStep,
            firstProb,
            totalTimeSteps,
            targetWidth,
            resizedContentWidth,
            orientedWidth,
            orientedHeight,
            sb,
            chars,
            ref pendingSpace,
            ref pendingSpaceProb,
            ref sumConf,
            ref confCount,
            ref minConf);
    }

    private static void FlushVocabularyTokenRun(
        ReadOnlySpan<char> vocabulary,
        int classIdx,
        int startStep,
        int endStep,
        float firstProb,
        int totalTimeSteps,
        int targetWidth,
        int resizedContentWidth,
        int orientedWidth,
        int orientedHeight,
        StringBuilder sb,
        List<PpOcrCharacterBox> chars,
        ref bool pendingSpace,
        ref float pendingSpaceProb,
        ref float sumConf,
        ref int confCount,
        ref float minConf)
    {
        int vocabIdx = classIdx - 1;
        if ((uint)vocabIdx >= (uint)vocabulary.Length)
        {
            return;
        }

        EmitDecodedCharacter(
            vocabulary[vocabIdx],
            startStep,
            endStep,
            firstProb,
            totalTimeSteps,
            targetWidth,
            resizedContentWidth,
            orientedWidth,
            orientedHeight,
            sb,
            chars,
            ref pendingSpace,
            ref pendingSpaceProb,
            ref sumConf,
            ref confCount,
            ref minConf);
    }

    private static void EmitDecodedCharacter(
        char ch,
        int startStep,
        int endStep,
        float firstProb,
        int totalTimeSteps,
        int targetWidth,
        int resizedContentWidth,
        int orientedWidth,
        int orientedHeight,
        StringBuilder sb,
        List<PpOcrCharacterBox> chars,
        ref bool pendingSpace,
        ref float pendingSpaceProb,
        ref float sumConf,
        ref int confCount,
        ref float minConf)
    {
        float clampedProb = Math.Clamp(firstProb, 0f, 1f);
        if (ch == ' ')
        {
            if (sb.Length > 0)
            {
                pendingSpace = true;
                pendingSpaceProb = clampedProb;
            }

            return;
        }

        if (pendingSpace && sb.Length > 0)
        {
            sb.Append(' ');
            // Include the emitted space token's first-timestep probability in line MeanConfidence and MinConfidence.
            sumConf += pendingSpaceProb;
            confCount++;
            if (pendingSpaceProb < minConf)
            {
                minConf = pendingSpaceProb;
            }
        }

        sb.Append(ch);
        sumConf += clampedProb;
        confCount++;
        if (clampedProb < minConf)
        {
            minConf = clampedProb;
        }

        float stepToTensorX = targetWidth / (float)Math.Max(1, totalTimeSteps);
        float tensorStartX = startStep * stepToTensorX;
        float tensorEndX = (endStep + 1) * stepToTensorX;
        float tensorToOrigX = orientedWidth / (float)Math.Max(1, resizedContentWidth);

        int maxCol = Math.Max(0, orientedWidth - 1);
        int startCol = Math.Clamp((int)MathF.Floor(tensorStartX * tensorToOrigX), 0, maxCol);
        int endCol = Math.Clamp((int)MathF.Ceiling(tensorEndX * tensorToOrigX), startCol, maxCol);

        chars.Add(new PpOcrCharacterBox(
            ch,
            clampedProb,
            LineIndex: 0,
            StartCol: startCol,
            EndCol: endCol,
            TopRow: 0,
            BottomRow: Math.Max(0, orientedHeight - 1),
            PrecededBySpace: pendingSpace));

        pendingSpace = false;
        pendingSpaceProb = 1.0f;
    }
}
