// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/RotationInvariantSignature.cs
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// 64-Byte (16 <see cref="float"/>) L2-normalized rotation-invariant radial-harmonic edge and intensity signature
/// computed over 8 concentric rings around an ROI center.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = ByteCount)]
public readonly struct RotationInvariantSignature
{
    public const int RingCount = 8;
    public const int FeaturesPerRing = 2;
    public const int FloatCount = RingCount * FeaturesPerRing; // 16 floats
    public const int ByteCount = FloatCount * sizeof(float); // 64 bytes
    private const int AngularSamplesPerRing = 64;
    private const float NormalizationEpsilon = 1e-6f;

    private readonly float _f0, _f1, _f2, _f3, _f4, _f5, _f6, _f7;
    private readonly float _f8, _f9, _f10, _f11, _f12, _f13, _f14, _f15;

    public RotationInvariantSignature(ReadOnlySpan<float> values)
    {
        if (values.Length < FloatCount)
        {
            throw new ArgumentException($"Signature requires {FloatCount} floats.", nameof(values));
        }

        _f0 = values[0];
        _f1 = values[1];
        _f2 = values[2];
        _f3 = values[3];
        _f4 = values[4];
        _f5 = values[5];
        _f6 = values[6];
        _f7 = values[7];
        _f8 = values[8];
        _f9 = values[9];
        _f10 = values[10];
        _f11 = values[11];
        _f12 = values[12];
        _f13 = values[13];
        _f14 = values[14];
        _f15 = values[15];
    }

    public ReadOnlySpan<float> AsSpan()
        => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _f0), FloatCount);

    /// <summary>
    /// Extracts a 64-Byte L2-normalized rotation-invariant signature centered at <paramref name="centerPose"/>
    /// over radius $[r_{\min}, r_{\max}]$ using <see cref="SubpixelInterpolator.SampleBilinear"/>.
    /// </summary>
    public static RotationInvariantSignature Extract(
        ReadOnlyImageView2D<byte> image,
        in FixturePose2D centerPose,
        float roiWidth,
        float roiHeight)
    {
        image.EnsureValid();
        centerPose.EnsureValid();

        float maxRadius = 0.46f * MathF.Min(roiWidth, roiHeight) * centerPose.Scale;
        float minRadius = MathF.Max(2.0f, maxRadius * 0.12f);

        Span<float> rawFeatures = stackalloc float[FloatCount];
        Span<float> ringSamples = stackalloc float[AngularSamplesPerRing];

        // Sample center intensity reference
        float centerVal = SubpixelInterpolator.SampleBilinear(image, centerPose.OriginX, centerPose.OriginY);
        float globalSum = centerVal;
        int globalCount = 1;

        for (int r = 0; r < RingCount; r++)
        {
            float t = (r + 0.5f) / RingCount;
            float radius = minRadius + (t * (maxRadius - minRadius));
            float ringSum = 0f;

            for (int a = 0; a < AngularSamplesPerRing; a++)
            {
                float angle = (a * 2.0f * MathF.PI) / AngularSamplesPerRing;
                (float sinA, float cosA) = MathF.SinCos(angle);
                float wx = centerPose.OriginX + (radius * cosA);
                float wy = centerPose.OriginY + (radius * sinA);
                float val = SubpixelInterpolator.SampleBilinear(image, wx, wy);
                ringSamples[a] = val;
                ringSum += val;
            }

            float ringMean = ringSum / AngularSamplesPerRing;
            globalSum += ringSum;
            globalCount += AngularSamplesPerRing;

            // Compute circular Fourier harmonic magnitudes k = 1, 2, 4 (strictly invariant to 2D rotation)
            float re2 = 0f, im2 = 0f;
            float re4 = 0f, im4 = 0f;
            for (int a = 0; a < AngularSamplesPerRing; a++)
            {
                float diff = ringSamples[a] - ringMean;
                float angle = (a * 2.0f * MathF.PI) / AngularSamplesPerRing;
                (float s2, float c2) = MathF.SinCos(2.0f * angle);
                (float s4, float c4) = MathF.SinCos(4.0f * angle);
                re2 += diff * c2;
                im2 += diff * s2;
                re4 += diff * c4;
                im4 += diff * s4;
            }

            float mag2 = MathF.Sqrt((re2 * re2) + (im2 * im2)) / AngularSamplesPerRing;
            float mag4 = MathF.Sqrt((re4 * re4) + (im4 * im4)) / AngularSamplesPerRing;

            rawFeatures[r * FeaturesPerRing] = ringMean;
            rawFeatures[(r * FeaturesPerRing) + 1] = MathF.Sqrt((mag2 * mag2) + (mag4 * mag4)) * 2.2f;
        }

        // Remove DC illumination offset from the ring means (even indices) so signature is also illumination-shift invariant
        float overallMean = globalSum / globalCount;
        for (int r = 0; r < RingCount; r++)
        {
            rawFeatures[r * FeaturesPerRing] -= overallMean;
        }

        // Unit L2 normalization
        float normSq = 0f;
        for (int i = 0; i < FloatCount; i++)
        {
            normSq += rawFeatures[i] * rawFeatures[i];
        }

        float invNorm = normSq > NormalizationEpsilon ? (1.0f / MathF.Sqrt(normSq)) : 0f;
        for (int i = 0; i < FloatCount; i++)
        {
            rawFeatures[i] *= invNorm;
        }

        return new RotationInvariantSignature(rawFeatures);
    }

    /// <summary>
    /// Computes the cosine similarity $\langle s_1, s_2 \rangle \in [-1, 1]$ between two L2-normalized 64-byte signatures using AVX2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float CosineSimilarity(in RotationInvariantSignature other)
    {
        if (Avx2.IsSupported)
        {
            ref float aRef = ref Unsafe.AsRef(in _f0);
            ref float bRef = ref Unsafe.AsRef(in other._f0);

            Vector256<float> vA0 = Vector256.LoadUnsafe(ref aRef);
            Vector256<float> vB0 = Vector256.LoadUnsafe(ref bRef);
            Vector256<float> vA1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref aRef, 8));
            Vector256<float> vB1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref bRef, 8));

            Vector256<float> acc = Avx.Add(Avx.Multiply(vA0, vB0), Avx.Multiply(vA1, vB1));
            Vector128<float> sum128 = Sse.Add(acc.GetLower(), acc.GetUpper());
            Vector128<float> shuf = Sse.Shuffle(sum128, sum128, 0b_01_00_11_10);
            sum128 = Sse.Add(sum128, shuf);
            shuf = Sse.Shuffle(sum128, sum128, 0b_00_00_00_01);
            return Sse.AddScalar(sum128, shuf).ToScalar();
        }

        ReadOnlySpan<float> a = AsSpan();
        ReadOnlySpan<float> b = other.AsSpan();
        float sum = 0f;
        for (int i = 0; i < FloatCount; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
