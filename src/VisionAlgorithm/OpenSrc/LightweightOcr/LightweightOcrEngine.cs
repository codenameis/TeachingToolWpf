// File: src/VisionAlgorithm/OpenSrc/LightweightOcr/LightweightOcrEngine.cs
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

namespace VisionAlgorithm.OpenSrc.LightweightOcr;

/// <summary>
/// Fixture-anchored 1D column-feature CTC sequence recognizer operating on taught <see cref="LightweightSequenceModel"/> prototypes.
/// </summary>
public static class LightweightOcrEngine
{
    /// <summary>
    /// Builds a <see cref="LightweightSequenceModel"/> from golden character samples using SimpleShot CL2N prototype weights.
    /// </summary>
    public static LightweightSequenceModel TrainPrototypeSequenceModel(ReadOnlySpan<GoldenCharSample> samples)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("At least one character sample is required.", nameof(samples));
        }

        int charCount = samples.Length;
        int numClasses = charCount + 2; // [Math Why]: Index 0 = CTC Blank, 1..charCount = taught characters, last = ' ' (space)
        int dim = LightweightSequenceModel.FeatureDimension;

        char[] charset = new char[numClasses];
        charset[LightweightSequenceModel.BlankTokenIndex] = '\0';

        float[] rawFeatures = new float[charCount * dim];
        for (int i = 0; i < charCount; i++)
        {
            GoldenCharSample s = samples[i];
            charset[i + 1] = s.Character;
            ReadOnlyImageView2D<byte> view = new(s.Pixels, s.Width, s.Height, s.Stride);
            SimdColumnFeatureEncoder.ExtractWindowFeature(
                view,
                s.DarkOnLight,
                rawFeatures.AsSpan(i * dim, dim));
        }

        charset[numClasses - 1] = ' ';

        float[] globalMean = new float[dim];
        SimpleShotNormalizer.ComputeGlobalMean(rawFeatures, charCount, dim, globalMean);

        float[] weights = new float[numClasses * dim];
        for (int i = 0; i < charCount; i++)
        {
            SimpleShotNormalizer.CenterAndL2Normalize(
                rawFeatures.AsSpan(i * dim, dim),
                globalMean,
                weights.AsSpan((i + 1) * dim, dim));
        }

        return new LightweightSequenceModel(charset, weights, globalMean);
    }

    /// <summary>
    /// Recognizes a text line anchored by <paramref name="fixture"/> from a raw 8-bit image buffer in a single pass.
    /// </summary>
    public static LightweightOcrResult RecognizeLine(
        ReadOnlySpan<byte> image,
        int width,
        int height,
        int stride,
        in FixturePose2D fixture,
        LightweightSequenceModel model,
        in LightweightOcrParams ocrParams,
        VisionWorkspace workspace)
    {
        ReadOnlyImageView2D<byte> view = new(image, width, height, stride);
        return RecognizeLine(view, in fixture, model, in ocrParams, workspace);
    }

    /// <summary>
    /// Recognizes a text line anchored by <paramref name="fixture"/> from a <see cref="ReadOnlyImageView2D{T}"/> in a single pass.
    /// </summary>
    public static LightweightOcrResult RecognizeLine(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D fixture,
        LightweightSequenceModel model,
        in LightweightOcrParams ocrParams,
        VisionWorkspace workspace)
    {
        image.EnsureValid();
        fixture.EnsureValid();
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(workspace);

        int stripW = ocrParams.StripWidth;
        int stripH = ocrParams.StripHeight;
        int pixelCount = checked(stripW * stripH);

        Span<byte> rectifiedStrip = workspace.GetByteBufferA(pixelCount);
        Span<float> warpScratch = workspace.GetFloatBufferA(pixelCount);
        SimdAffineWarper.WarpAffineBilinear(image, in fixture, stripW, stripH, rectifiedStrip, warpScratch);

        int timeSteps = stripW / Math.Max(1, ocrParams.ColumnStride);
        Span<float> logits = workspace.GetFloatBufferB(checked(timeSteps * model.NumClasses));

        ReadOnlyImageView2D<byte> stripView = new(rectifiedStrip, stripW, stripH, stripW);
        int actualSteps = SimdColumnFeatureEncoder.EncodeStripToLogits(
            stripView,
            model,
            in ocrParams,
            workspace,
            logits);

        string text = ocrParams.UsePrefixBeamSearch
            ? CtcSequenceDecoder.DecodePrefixBeam(logits, actualSteps, model.Charset, ocrParams.BeamWidth, out float confidence)
            : CtcSequenceDecoder.DecodeGreedy(logits, actualSteps, model.Charset, out confidence);

        bool isPass = !string.IsNullOrEmpty(text) && confidence >= ocrParams.MinConfidence;
        return new LightweightOcrResult(text, confidence, actualSteps, fixture, isPass);
    }
}
