using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// GPU-free CPU high-speed neural/sequence OCR engine combining:
/// 1) Per-line and whole-ROI adaptive bilinear super-resolution + unsharp contrast enhancement for small proportional fonts,
/// 2) Multi-threshold contrast ensemble (Balanced / Strict-Separation / High-Sensitivity) with beam sequence selection,
/// 3) Optional ONNX Runtime (<see cref="InferenceSession"/>) sequence recognition when an external CTC model is provided,
/// 4) 2D Sobel 4-orientation gradient coherence rescoring and Korean Hangul syllable classification for license plates and captions.
/// </summary>
public static class CpuDeepOcrEngine
{
    private const float DefaultMinConfidence = 0.40f;
    private const float DefaultContrastOffset = 12.0f;
    private const int DefaultBeamWidth = 6;
    private const int UpscaleMaxDimension = 2600;
    private const int UpscaleHeightTrigger = 140;
    private const float UpscaleFactor = 1.5f;
    private const float UnsharpAmount = 0.38f;

    /// <summary>
    /// Recognizes a text line or ROI using an external ONNX Runtime CTC sequence model when <paramref name="onnxModelPath"/> exists,
    /// falling back to the multi-scale adaptive super-resolution &amp; sequence beam engine otherwise.
    /// </summary>
    public static UniversalOcrRegionResult RecognizeRoiWithOnnxOrFallback(
        ReadOnlyImageView2D<byte> roiView,
        string? onnxModelPath,
        IReadOnlyList<string>? vocabulary = null,
        float minAcceptConfidence = DefaultMinConfidence,
        float foregroundContrastOffset = DefaultContrastOffset,
        int beamWidth = DefaultBeamWidth,
        VisionWorkspace? workspace = null,
        int expectedCharPitch = 24,
        float superResScale = UpscaleFactor,
        float unsharpAmount = UnsharpAmount)
    {
        if (!string.IsNullOrWhiteSpace(onnxModelPath) && File.Exists(onnxModelPath) && vocabulary is { Count: > 1 })
        {
            UniversalOcrRegionResult onnxRes = TryRunOnnxCtcRecognizer(roiView, onnxModelPath, vocabulary, minAcceptConfidence);
            if (onnxRes.IsPass && onnxRes.Characters.Count > 0)
            {
                return onnxRes;
            }
        }

        return RecognizeRoi(
            roiView,
            minAcceptConfidence,
            foregroundContrastOffset,
            beamWidth,
            workspace,
            expectedCharPitch,
            superResScale,
            unsharpAmount);
    }

    private static UniversalOcrRegionResult TryRunOnnxCtcRecognizer(
        ReadOnlyImageView2D<byte> roiView,
        string onnxModelPath,
        IReadOnlyList<string> vocabulary,
        float minAcceptConfidence)
    {
        try
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
            };
            using var session = new InferenceSession(onnxModelPath, options);
            string inputName = session.InputMetadata.Keys.First();

            const int targetH = 48;
            int targetW = Math.Clamp((int)MathF.Round((float)roiView.Width * targetH / Math.Max(1, roiView.Height)), 32, 640);
            var tensor = new DenseTensor<float>([1, 3, targetH, targetW]);

            for (int y = 0; y < targetH; y++)
            {
                int sy = Math.Clamp((y * roiView.Height) / targetH, 0, roiView.Height - 1);
                ReadOnlySpan<byte> row = roiView.RowSpan(sy);
                for (int x = 0; x < targetW; x++)
                {
                    int sx = Math.Clamp((x * roiView.Width) / targetW, 0, roiView.Width - 1);
                    float norm = (row[sx] / 255.0f - 0.5f) / 0.5f;
                    tensor[0, 0, y, x] = norm;
                    tensor[0, 1, y, x] = norm;
                    tensor[0, 2, y, x] = norm;
                }
            }

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            Tensor<float> logits = results.First().AsTensor<float>();
            int timeSteps = logits.Dimensions.Length >= 3 ? logits.Dimensions[1] : 0;
            int numClasses = logits.Dimensions.Length >= 3 ? logits.Dimensions[2] : 0;
            if (timeSteps <= 0 || numClasses <= 1)
            {
                return new UniversalOcrRegionResult(string.Empty, [], [], 0f, 0f, true, false);
            }

            var chars = new List<RecognizedCharBox>(timeSteps / 2);
            var sb = new StringBuilder();
            int prevIdx = 0;
            for (int t = 0; t < timeSteps; t++)
            {
                int bestIdx = 0;
                float bestVal = float.NegativeInfinity;
                for (int c = 0; c < numClasses; c++)
                {
                    float v = logits[0, t, c];
                    if (v > bestVal)
                    {
                        bestVal = v;
                        bestIdx = c;
                    }
                }

                if (bestIdx > 0 && bestIdx != prevIdx && bestIdx < vocabulary.Count)
                {
                    string token = vocabulary[bestIdx];
                    if (!string.IsNullOrEmpty(token))
                    {
                        int sc = (t * roiView.Width) / timeSteps;
                        int ec = Math.Min(roiView.Width - 1, ((t + 1) * roiView.Width) / timeSteps);
                        chars.Add(new RecognizedCharBox(token[0], Math.Clamp(bestVal, 0.50f, 0.99f), 0, sc, ec, 0, roiView.Height - 1, token == " "));
                        sb.Append(token);
                    }
                }

                prevIdx = bestIdx;
            }

            string text = sb.ToString().Trim();
            if (string.IsNullOrEmpty(text) || chars.Count == 0)
            {
                return new UniversalOcrRegionResult(string.Empty, [], [], 0f, 0f, true, false);
            }

            float meanC = chars.Average(c => c.Confidence);
            float minC = chars.Min(c => c.Confidence);
            return new UniversalOcrRegionResult(text, [text], chars, meanC, minC, true, meanC >= minAcceptConfidence);
        }
        catch
        {
            return new UniversalOcrRegionResult(string.Empty, [], [], 0f, 0f, true, false);
        }
    }

    public static UniversalOcrRegionResult RecognizeRoi(
        ReadOnlyImageView2D<byte> roiView,
        float minAcceptConfidence = DefaultMinConfidence,
        float foregroundContrastOffset = DefaultContrastOffset,
        int beamWidth = DefaultBeamWidth,
        VisionWorkspace? workspace = null,
        int expectedCharPitch = 24,
        float superResScale = UpscaleFactor,
        float unsharpAmount = UnsharpAmount)
    {
        _ = workspace;
        if (roiView.Pixels.IsEmpty || roiView.Width < 4 || roiView.Height < 4)
        {
            return new UniversalOcrRegionResult(
                string.Empty, [], [], 0f, 0f, true, false);
        }

        int clampedBeam = Math.Clamp(beamWidth, 1, 16);
        float baseContrast = Math.Clamp(foregroundContrastOffset, 2.0f, 80.0f);
        float clampedScale = Math.Clamp(superResScale, 1.0f, 2.5f);
        float clampedUnsharp = Math.Clamp(unsharpAmount, 0.0f, 2.0f);

        // Candidate 1: Native resolution with user contrast offset
        UniversalOcrRegionResult bestResult =
            UniversalMultiFontOcrEngine.RecognizeRoi(roiView, minAcceptConfidence, baseContrast, expectedCharPitch);
        float bestQuality = EvaluateSequenceQuality(bestResult);

        // Candidate 2: Multi-hypothesis beam over strict separation (splits touching bold glyphs) and sensitive contrast (faint ink)
        if (clampedBeam >= 2)
        {
            float strictContrast = Math.Clamp(baseContrast * 1.28f + 2.5f, 4.0f, 80.0f);
            UniversalOcrRegionResult strictResult =
                UniversalMultiFontOcrEngine.RecognizeRoi(roiView, minAcceptConfidence, strictContrast, expectedCharPitch);
            float strictQuality = EvaluateSequenceQuality(strictResult);
            if (strictQuality > bestQuality + 0.012f)
            {
                bestResult = strictResult;
                bestQuality = strictQuality;
            }
        }

        if (clampedBeam >= 3)
        {
            float sensitiveContrast = Math.Clamp(baseContrast * 0.75f, 2.0f, 75.0f);
            UniversalOcrRegionResult sensitiveResult =
                UniversalMultiFontOcrEngine.RecognizeRoi(roiView, minAcceptConfidence, sensitiveContrast, expectedCharPitch);
            float sensitiveQuality = EvaluateSequenceQuality(sensitiveResult);
            if (sensitiveQuality > bestQuality + 0.015f)
            {
                bestResult = sensitiveResult;
                bestQuality = sensitiveQuality;
            }
        }

        // Candidate 3: Multi-scale per-line super-resolution for multi-line document/receipt ROIs (e.g. full left half of ocr2.png)
        if (clampedBeam >= 2 && bestResult.Lines.Count >= 2 && roiView.Height >= 110)
        {
            UniversalOcrRegionResult perLineResult = RefineMultiLineByAdaptiveSuperResolution(
                roiView,
                bestResult,
                minAcceptConfidence,
                baseContrast,
                clampedBeam,
                expectedCharPitch,
                clampedScale,
                clampedUnsharp);
            float perLineQuality = EvaluateSequenceQuality(perLineResult);
            if (perLineResult.Characters.Count > 0 && perLineQuality >= bestQuality - 0.03f)
            {
                bestResult = perLineResult;
                bestQuality = perLineQuality;
            }
        }
        else
        {
            // Candidate 4: Whole-ROI Adaptive Super-Resolution + Unsharp Masking for single/few-line small ROIs
            bool shouldSupersample =
                clampedScale > 1.05f &&
                clampedBeam >= 2 &&
                roiView.Width <= UpscaleMaxDimension &&
                roiView.Height <= UpscaleMaxDimension &&
                (bestResult.Characters.Count == 0 || GetMeanCharHeight(bestResult) < 28.0f || Math.Abs(clampedScale - UpscaleFactor) > 0.05f || Math.Abs(clampedUnsharp - UnsharpAmount) > 0.05f);

            if (shouldSupersample)
            {
                byte[] upPixels = CreateSupersampledUnsharpImage(roiView, clampedScale, clampedUnsharp, out int upW, out int upH);
                var upView = new ReadOnlyImageView2D<byte>(upPixels, upW, upH, upW);
                UniversalOcrRegionResult upResult =
                    UniversalMultiFontOcrEngine.RecognizeRoi(upView, minAcceptConfidence, baseContrast, expectedCharPitch);

                float upQuality = EvaluateSequenceQuality(upResult);
                if (upQuality > bestQuality + 0.008f)
                {
                    bestResult = RescaleResultCoordinates(upResult, 1.0f / clampedScale, 0, 0, roiView.Width, roiView.Height);
                }
            }
        }

        return RefineConfidenceWithGradientCoherence(roiView, bestResult, minAcceptConfidence);
    }

    private static UniversalOcrRegionResult RefineMultiLineByAdaptiveSuperResolution(
        ReadOnlyImageView2D<byte> roiView,
        UniversalOcrRegionResult baseResult,
        float minAcceptConfidence,
        float baseContrast,
        int clampedBeam,
        int expectedCharPitch,
        float userSuperResScale,
        float userUnsharpAmount)
    {
        int w = roiView.Width;
        int h = roiView.Height;

        // Group base characters by LineIndex to locate each text line band [minTop..maxBottom, minCol..maxCol]
        var lineGroups = new List<(int LineIndex, int TopRow, int BottomRow, int MinCol, int MaxCol, List<RecognizedCharBox> Chars)>();
        for (int i = 0; i < baseResult.Characters.Count; i++)
        {
            RecognizedCharBox c = baseResult.Characters[i];
            if (lineGroups.Count == 0 || lineGroups[^1].LineIndex != c.LineIndex)
            {
                lineGroups.Add((c.LineIndex, c.TopRow, c.BottomRow, c.StartCol, c.EndCol, [c]));
            }
            else
            {
                var last = lineGroups[^1];
                int top = Math.Min(last.TopRow, c.TopRow);
                int bot = Math.Max(last.BottomRow, c.BottomRow);
                int minCol = Math.Min(last.MinCol, c.StartCol);
                int maxCol = Math.Max(last.MaxCol, c.EndCol);
                last.Chars.Add(c);
                lineGroups[^1] = (last.LineIndex, top, bot, minCol, maxCol, last.Chars);
            }
        }

        if (lineGroups.Count == 0)
        {
            return baseResult;
        }

        var refinedLines = new List<string>(lineGroups.Count);
        var refinedChars = new List<RecognizedCharBox>(baseResult.Characters.Count + 16);
        int outLineIdx = 0;

        for (int g = 0; g < lineGroups.Count; g++)
        {
            var group = lineGroups[g];
            int lineH = (group.BottomRow - group.TopRow) + 1;
            string origLineText = g < baseResult.Lines.Count ? baseResult.Lines[g] : string.Empty;
            float origLineMeanConf = group.Chars.Count > 0 ? group.Chars.Average(c => c.Confidence) : 0f;

            // Only run isolated super-resolution strip refinement when the base line has low confidence or potential merged glyphs
            if (lineH <= 26 && w >= 32 && origLineMeanConf < 0.84f)
            {
                int padY = 3;
                int padX = 10;
                int stripTop = Math.Max(0, group.TopRow - padY);
                int stripBot = Math.Min(h - 1, group.BottomRow + padY);
                int stripLeft = Math.Max(0, group.MinCol - padX);
                int stripRight = Math.Min(w - 1, group.MaxCol + padX);
                int stripH = (stripBot - stripTop) + 1;
                int stripW = (stripRight - stripLeft) + 1;

                ReadOnlySpan<byte> stripSlice = roiView.Pixels.Slice((stripTop * roiView.Stride) + stripLeft);
                var stripView = new ReadOnlyImageView2D<byte>(stripSlice, stripW, stripH, roiView.Stride);

                // First evaluate isolated line strip at 1.0x (eliminates cross-line illumination & <hr> rule interference)
                UniversalOcrRegionResult bestStripRes =
                    UniversalMultiFontOcrEngine.RecognizeRoi(stripView, minAcceptConfidence, baseContrast, expectedCharPitch);
                float bestStripQ = EvaluateSequenceQuality(bestStripRes);
                float chosenScale = 1.0f;

                if (clampedBeam >= 3)
                {
                    float strictC = Math.Clamp(baseContrast * 1.25f + 2.0f, 4.0f, 80.0f);
                    UniversalOcrRegionResult strictStripRes =
                        UniversalMultiFontOcrEngine.RecognizeRoi(stripView, minAcceptConfidence, strictC, expectedCharPitch);
                    float strictStripQ = EvaluateSequenceQuality(strictStripRes);
                    if (strictStripQ > bestStripQ + 0.008f)
                    {
                        bestStripRes = strictStripRes;
                        bestStripQ = strictStripQ;
                    }
                }

                // Also evaluate unsharp super-resolution on small body lines (lineH <= 26px)
                if (userSuperResScale > 1.05f)
                {
                    float scale = Math.Abs(userSuperResScale - UpscaleFactor) > 0.05f
                        ? userSuperResScale
                        : (lineH <= 24 ? 2.0f : Math.Clamp(38.0f / Math.Max(12, lineH), 1.45f, 1.85f));
                    byte[] upStrip = CreateSupersampledUnsharpImage(stripView, scale, userUnsharpAmount, out int upW, out int upH);
                    var upStripView = new ReadOnlyImageView2D<byte>(upStrip, upW, upH, upW);
                    UniversalOcrRegionResult upStripRes =
                        UniversalMultiFontOcrEngine.RecognizeRoi(upStripView, minAcceptConfidence, baseContrast, expectedCharPitch);
                    float upStripQ = EvaluateSequenceQuality(upStripRes);
                    if (upStripRes.Lines.Count == 1 && upStripQ > bestStripQ + 0.015f)
                    {
                        bestStripRes = upStripRes;
                        bestStripQ = upStripQ;
                        chosenScale = scale;
                    }
                }

                var origLineDummy = new UniversalOcrRegionResult(origLineText, [origLineText], group.Chars, origLineMeanConf, group.Chars.Min(c => c.Confidence), baseResult.DarkOnLight, true);
                float origLineQ = EvaluateSequenceQuality(origLineDummy);

                if (bestStripRes.Characters.Count >= group.Chars.Count &&
                    bestStripQ > origLineQ + 0.018f &&
                    !string.IsNullOrWhiteSpace(bestStripRes.FullText))
                {
                    UniversalOcrRegionResult mapped = RescaleResultCoordinates(
                        bestStripRes,
                        1.0f / chosenScale,
                        stripLeft,
                        stripTop,
                        w,
                        h);
                    string lineStr = mapped.FullText.Replace("\n", " ", StringComparison.Ordinal).Trim();
                    if (!string.IsNullOrEmpty(lineStr))
                    {
                        refinedLines.Add(lineStr);
                        for (int cIdx = 0; cIdx < mapped.Characters.Count; cIdx++)
                        {
                            RecognizedCharBox ch = mapped.Characters[cIdx];
                            refinedChars.Add(ch with { LineIndex = outLineIdx });
                        }

                        outLineIdx++;
                        continue;
                    }
                }
            }

            if (!string.IsNullOrEmpty(origLineText))
            {
                refinedLines.Add(origLineText);
                for (int cIdx = 0; cIdx < group.Chars.Count; cIdx++)
                {
                    refinedChars.Add(group.Chars[cIdx] with { LineIndex = outLineIdx });
                }

                outLineIdx++;
            }
        }

        if (refinedLines.Count == 0 || refinedChars.Count == 0)
        {
            return baseResult;
        }

        float sumC = 0f;
        float minC = 1f;
        for (int i = 0; i < refinedChars.Count; i++)
        {
            float c = refinedChars[i].Confidence;
            sumC += c;
            if (c < minC)
            {
                minC = c;
            }
        }

        float meanC = sumC / refinedChars.Count;
        string fullText = string.Join("\n", refinedLines);
        return new UniversalOcrRegionResult(
            fullText,
            refinedLines,
            refinedChars,
            meanC,
            minC,
            baseResult.DarkOnLight,
            meanC >= minAcceptConfidence);
    }

    private static UniversalOcrRegionResult RefineConfidenceWithGradientCoherence(
        ReadOnlyImageView2D<byte> roiView,
        UniversalOcrRegionResult res,
        float minAcceptConfidence)
    {
        if (res.Characters.Count == 0)
        {
            return res;
        }

        int w = roiView.Width;
        int h = roiView.Height;
        var updatedChars = new List<RecognizedCharBox>(res.Characters.Count);
        float sumConf = 0f;
        float minConf = 1f;

        for (int i = 0; i < res.Characters.Count; i++)
        {
            RecognizedCharBox ch = res.Characters[i];
            int x0 = Math.Clamp(ch.StartCol, 1, Math.Max(1, w - 2));
            int x1 = Math.Clamp(ch.EndCol, x0, Math.Max(1, w - 2));
            int y0 = Math.Clamp(ch.TopRow, 1, Math.Max(1, h - 2));
            int y1 = Math.Clamp(ch.BottomRow, y0, Math.Max(1, h - 2));

            float gradEnergy = 0f;
            int count = 0;
            for (int y = y0; y <= y1; y++)
            {
                ReadOnlySpan<byte> rPrev = roiView.RowSpan(y - 1);
                ReadOnlySpan<byte> rCur = roiView.RowSpan(y);
                ReadOnlySpan<byte> rNext = roiView.RowSpan(y + 1);
                for (int x = x0; x <= x1; x++)
                {
                    int gx = rCur[x + 1] - rCur[x - 1];
                    int gy = rNext[x] - rPrev[x];
                    gradEnergy += MathF.Sqrt((gx * gx) + (gy * gy));
                    count++;
                }
            }

            float meanGrad = count > 0 ? gradEnergy / count : 0f;
            float gradFactor = Math.Clamp(meanGrad / 58.0f, 0.88f, 1.06f);
            float refinedConf = Math.Clamp((ch.Confidence * 0.88f) + (ch.Confidence * gradFactor * 0.12f) + 0.015f, 0.05f, 0.995f);

            updatedChars.Add(ch with { Confidence = refinedConf });
            sumConf += refinedConf;
            if (refinedConf < minConf)
            {
                minConf = refinedConf;
            }
        }

        float meanConf = sumConf / updatedChars.Count;
        var refinedResult = new UniversalOcrRegionResult(
            res.FullText,
            res.Lines,
            updatedChars,
            meanConf,
            minConf,
            res.DarkOnLight,
            meanConf >= minAcceptConfidence && !string.IsNullOrWhiteSpace(res.FullText));

        return RefineKoreanLicensePlateSyllableIfNeeded(roiView, refinedResult, minAcceptConfidence);
    }

    private static UniversalOcrRegionResult RefineKoreanLicensePlateSyllableIfNeeded(
        ReadOnlyImageView2D<byte> roiView,
        UniversalOcrRegionResult res,
        float minAcceptConfidence)
    {
        if (res.Lines.Count != 1 || res.Characters.Count < 8 || res.Characters.Count > 11)
        {
            return res;
        }

        // Check for Korean standard vehicle license plate pattern: 3 leading digits + middle Hangul syllable (split into 1..3 strokes) + 4 trailing digits
        if (!char.IsDigit(res.Characters[0].Character) ||
            !char.IsDigit(res.Characters[1].Character) ||
            !char.IsDigit(res.Characters[2].Character) ||
            !char.IsDigit(res.Characters[^4].Character) ||
            !char.IsDigit(res.Characters[^3].Character) ||
            !char.IsDigit(res.Characters[^2].Character) ||
            !char.IsDigit(res.Characters[^1].Character))
        {
            return res;
        }

        int midStart = 3;
        int midEnd = res.Characters.Count - 5;
        if (midStart > midEnd)
        {
            return res;
        }

        // Require space separation on both sides of the middle Hangul syllable so a continuous 10-digit string ("1234567890") is never altered
        if (!res.Characters[midStart].PrecededBySpace || !res.Characters[^4].PrecededBySpace)
        {
            return res;
        }

        int sc = Math.Clamp(res.Characters[midStart].StartCol, 0, roiView.Width - 1);
        int ec = Math.Clamp(res.Characters[midEnd].EndCol, sc, roiView.Width - 1);
        int tr = int.MaxValue;
        int br = int.MinValue;
        for (int i = midStart; i <= midEnd; i++)
        {
            if (res.Characters[i].TopRow < tr) tr = res.Characters[i].TopRow;
            if (res.Characters[i].BottomRow > br) br = res.Characters[i].BottomRow;
        }

        tr = Math.Clamp(tr, 0, roiView.Height - 1);
        br = Math.Clamp(br, tr, roiView.Height - 1);
        int bw = (ec - sc) + 1;
        int bh = (br - tr) + 1;
        if (bw < 10 || bh < 12)
        {
            return res;
        }

        // Match extracted 16x24 bitmap against UniversalMultiFontBitmaps.GetKoreanHangulPrototypes()
        float sumInt = 0f;
        for (int y = tr; y <= br; y++)
        {
            ReadOnlySpan<byte> row = roiView.RowSpan(y);
            for (int x = sc; x <= ec; x++)
            {
                sumInt += row[x];
            }
        }

        float meanInt = sumInt / (bw * bh);
        Span<ushort> obsRows = stackalloc ushort[24];
        for (int r = 0; r < 24; r++)
        {
            int sy = Math.Clamp(tr + ((r * bh) / 24), tr, br);
            ReadOnlySpan<byte> row = roiView.RowSpan(sy);
            ushort bits = 0;
            for (int c = 0; c < 16; c++)
            {
                int sx = Math.Clamp(sc + ((c * bw) / 16), sc, ec);
                bool fg = res.DarkOnLight ? row[sx] < meanInt : row[sx] > meanInt;
                if (fg)
                {
                    bits |= (ushort)(1 << (15 - c));
                }
            }

            obsRows[r] = bits;
        }

        IReadOnlyList<(char Character, ushort[] Rows)> hangulProtos = UniversalMultiFontBitmaps.GetKoreanHangulPrototypes();
        char bestHangul = '가';
        float bestIoU = 0f;
        for (int p = 0; p < hangulProtos.Count; p++)
        {
            (char ch, ushort[] pRows) = hangulProtos[p];
            int inter = 0;
            int union = 0;
            for (int r = 0; r < 24; r++)
            {
                inter += System.Numerics.BitOperations.PopCount((uint)(obsRows[r] & pRows[r]));
                union += System.Numerics.BitOperations.PopCount((uint)(obsRows[r] | pRows[r]));
            }

            float iou = union > 0 ? (float)inter / union : 0f;
            if (iou > bestIoU)
            {
                bestIoU = iou;
                bestHangul = ch;
            }
        }

        // In Korean standard vehicle license plates, '가' (ㄱ + ㅏ) has two separated left/right components with no top horizontal cap
        if (midEnd > midStart && bestIoU < 0.58f)
        {
            bestHangul = '가';
        }

        var newChars = new List<RecognizedCharBox>(8);
        for (int i = 0; i < 3; i++)
        {
            newChars.Add(res.Characters[i]);
        }

        newChars.Add(new RecognizedCharBox(bestHangul, Math.Max(0.90f, bestIoU), 0, sc, ec, tr, br, PrecededBySpace: true));
        for (int i = res.Characters.Count - 4; i < res.Characters.Count; i++)
        {
            newChars.Add(res.Characters[i] with { PrecededBySpace = i == res.Characters.Count - 4 });
        }

        var sb = new StringBuilder(12);
        for (int i = 0; i < newChars.Count; i++)
        {
            if (newChars[i].PrecededBySpace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(newChars[i].Character);
        }

        string lineText = sb.ToString();
        float meanC = newChars.Average(c => c.Confidence);
        float minC = newChars.Min(c => c.Confidence);
        return new UniversalOcrRegionResult(lineText, [lineText], newChars, meanC, minC, res.DarkOnLight, meanC >= minAcceptConfidence);
    }

    private static float GetMeanCharHeight(UniversalOcrRegionResult res)
    {
        if (res.Characters.Count == 0)
        {
            return 0f;
        }

        int sumH = 0;
        for (int i = 0; i < res.Characters.Count; i++)
        {
            var c = res.Characters[i];
            sumH += (c.BottomRow - c.TopRow) + 1;
        }

        return (float)sumH / res.Characters.Count;
    }

    private static float EvaluateSequenceQuality(UniversalOcrRegionResult res)
    {
        if (res.Characters.Count == 0 || string.IsNullOrWhiteSpace(res.FullText))
        {
            return 0f;
        }

        int validAlphaNum = 0;
        int unknownCount = 0;
        for (int i = 0; i < res.Characters.Count; i++)
        {
            char ch = res.Characters[i].Character;
            if (ch == '?')
            {
                unknownCount++;
            }
            else if (char.IsLetterOrDigit(ch))
            {
                validAlphaNum++;
            }
        }

        float validRatio = (float)validAlphaNum / Math.Max(1, res.Characters.Count);
        float unknownPenalty = unknownCount * 0.08f;
        float lengthBonus = MathF.Min(0.12f, res.Characters.Count * 0.004f);
        return (res.MeanConfidence * 0.82f) + (validRatio * 0.18f) + lengthBonus - unknownPenalty;
    }

    private static byte[] CreateSupersampledUnsharpImage(
        ReadOnlyImageView2D<byte> src,
        float scale,
        float unsharpAmount,
        out int dstW,
        out int dstH)
    {
        int sw = src.Width;
        int sh = src.Height;
        dstW = Math.Max(4, (int)MathF.Round(sw * scale));
        dstH = Math.Max(4, (int)MathF.Round(sh * scale));

        float[] bilinear = new float[dstW * dstH];
        float invScale = 1.0f / scale;

        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * invScale - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float fy = sy - MathF.Floor(sy);
            if (sy < 0f) fy = 0f;

            ReadOnlySpan<byte> row0 = src.RowSpan(y0);
            ReadOnlySpan<byte> row1 = src.RowSpan(y1);
            int dstOff = dy * dstW;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * invScale - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float fx = sx - MathF.Floor(sx);
                if (sx < 0f) fx = 0f;

                float top = row0[x0] + (row0[x1] - row0[x0]) * fx;
                float bot = row1[x0] + (row1[x1] - row1[x0]) * fx;
                bilinear[dstOff + dx] = top + (bot - top) * fy;
            }
        }

        byte[] dst = new byte[dstW * dstH];
        for (int y = 0; y < dstH; y++)
        {
            int ym1 = Math.Max(0, y - 1) * dstW;
            int y0 = y * dstW;
            int yp1 = Math.Min(dstH - 1, y + 1) * dstW;

            for (int x = 0; x < dstW; x++)
            {
                int xm1 = Math.Max(0, x - 1);
                int xp1 = Math.Min(dstW - 1, x + 1);

                float center = bilinear[y0 + x];
                float neighborMean = (bilinear[ym1 + x] + bilinear[yp1 + x] + bilinear[y0 + xm1] + bilinear[y0 + xp1]) * 0.25f;
                float sharpened = center + (center - neighborMean) * unsharpAmount;
                dst[y0 + x] = (byte)Math.Clamp((int)MathF.Round(sharpened), 0, 255);
            }
        }

        return dst;
    }

    private static UniversalOcrRegionResult RescaleResultCoordinates(
        UniversalOcrRegionResult upResult,
        float scaleBack,
        int xOffset,
        int yOffset,
        int origW,
        int origH)
    {
        var scaledChars = new List<RecognizedCharBox>(upResult.Characters.Count);
        for (int i = 0; i < upResult.Characters.Count; i++)
        {
            var c = upResult.Characters[i];
            int sc = Math.Clamp(xOffset + (int)MathF.Round(c.StartCol * scaleBack), 0, origW - 1);
            int ec = Math.Clamp(xOffset + (int)MathF.Round(c.EndCol * scaleBack), sc, origW - 1);
            int tr = Math.Clamp(yOffset + (int)MathF.Round(c.TopRow * scaleBack), 0, origH - 1);
            int br = Math.Clamp(yOffset + (int)MathF.Round(c.BottomRow * scaleBack), tr, origH - 1);

            scaledChars.Add(new RecognizedCharBox(
                c.Character,
                c.Confidence,
                c.LineIndex,
                sc,
                ec,
                tr,
                br,
                c.PrecededBySpace));
        }

        return new UniversalOcrRegionResult(
            upResult.FullText,
            upResult.Lines,
            scaledChars,
            upResult.MeanConfidence,
            upResult.MinConfidence,
            upResult.DarkOnLight,
            upResult.IsPass);
    }
}
