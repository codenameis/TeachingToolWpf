// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/SyntheticCornerTestGen/Synthetic8kCornerRenderer.cs
using System;
using System.IO;
using System.Numerics;
using System.Text;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.SyntheticCornerTestGen;

/// <summary>
/// 연속 부호 거리 함수(Signed Distance Function, SDF) 및 가우시안 오차함수(<c>erf</c>) 광학 PSF 적분을 사용하여
/// 0.001 px 이하의 수학적 참값(Ground Truth)을 보장하는 서브픽셀 코너·8K 기판 렌더러 및 PGM/BMP 이미지 저장기.
/// </summary>
public static class Synthetic8kCornerRenderer
{
    public const int Master8kDimension = 8192;
    public const byte DefaultBackgroundIntensity = 36;
    public const byte DefaultForegroundIntensity = 220;

    private const float InvSqrt2 = 0.70710678f;
    private const float ChippingCenterOffsetAlongLeg1 = 76.0f;
    private const float BurrCenterOffsetAlongLeg2 = 88.0f;

    /// <summary>
    /// 지정된 버퍼에 연속 SDF + 가우시안 PSF 적분 기반으로 단일 서브픽셀 코너(모따기, 라운딩, 칩핑, 버 포함)를 렌더링한다.
    /// 코너 내부 사분면은 <c>u &gt;= 0</c>(Leg 1 방향) 및 <c>v &gt;= 0</c>(Leg 2 방향) 영역으로 정의된다.
    /// </summary>
    public static void RenderCornerScene(
        Span<byte> buffer,
        int width,
        int height,
        int stride,
        Vector2 trueCornerApex,
        float orientationRad,
        CornerDefectProfile defectProfile,
        uint deterministicSeed = 1337u,
        byte backgroundGray = DefaultBackgroundIntensity,
        byte foregroundGray = DefaultForegroundIntensity)
    {
        float cosT = MathF.Cos(orientationRad);
        float sinT = MathF.Sin(orientationRad);

        // u 축: Leg 1 진행 방향 (cosT, sinT)
        // v 축: Leg 2 진행 방향 (-sinT, cosT) -> Leg 1에서 내부로 향하는 직교 축
        float psfSigma = MathF.Max(0.45f, defectProfile.OpticalPsfSigma);
        float invSqrt2Sigma = 1.0f / (MathF.Sqrt(2.0f) * psfSigma);
        float contrast = foregroundGray - backgroundGray;

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Slice(y * stride, width);
            float dy = y - trueCornerApex.Y;

            for (int x = 0; x < width; x++)
            {
                float dx = x - trueCornerApex.X;
                float u = (dx * cosT) + (dy * sinT);
                float v = (-dx * sinT) + (dy * cosT);

                float sdf = EvaluateDefectiveCornerSdf(u, v, defectProfile);
                float coverage = 0.5f * (1.0f - ErfApprox(sdf * invSqrt2Sigma));
                float intensity = backgroundGray + (contrast * coverage);

                if (defectProfile.NoiseSigma > 0.0f)
                {
                    float gaussianNoise = SampleDeterministicGaussian(x, y, deterministicSeed) * defectProfile.NoiseSigma;
                    intensity += gaussianNoise;
                }

                int clamped = (int)MathF.Round(Math.Clamp(intensity, 0.0f, 255.0f));
                row[x] = (byte)clamped;
            }
        }
    }

    /// <summary>
    /// 8K(8192×8192) 대면적 기판의 4개 코너(Top-Left, Top-Right, Bottom-Right, Bottom-Left) 중
    /// 지정된 코너의 로컬 윈도우를 전체 8K 월드 좌표계 기준으로 정확히 렌더링한다.
    /// </summary>
    public static void Render8kPanelCornerWindow(
        Span<byte> windowBuffer,
        int windowWidth,
        int windowHeight,
        int windowStride,
        int windowOriginX,
        int windowOriginY,
        Vector2 panelCenterWorld,
        float panelWidth,
        float panelHeight,
        float panelAngleRad,
        CornerDefectProfile defectProfile,
        uint seed = 2026u)
    {
        float cosA = MathF.Cos(panelAngleRad);
        float sinA = MathF.Sin(panelAngleRad);
        float halfW = panelWidth * 0.5f;
        float halfH = panelHeight * 0.5f;

        float psfSigma = MathF.Max(0.45f, defectProfile.OpticalPsfSigma);
        float invSqrt2Sigma = 1.0f / (MathF.Sqrt(2.0f) * psfSigma);
        float contrast = DefaultForegroundIntensity - DefaultBackgroundIntensity;

        for (int wy = 0; wy < windowHeight; wy++)
        {
            Span<byte> row = windowBuffer.Slice(wy * windowStride, windowWidth);
            float worldY = windowOriginY + wy;
            float dy = worldY - panelCenterWorld.Y;

            for (int wx = 0; wx < windowWidth; wx++)
            {
                float worldX = windowOriginX + wx;
                float dx = worldX - panelCenterWorld.X;

                // 기판 중심 기준 로컬 좌표 (lx, ly)
                float lx = (dx * cosA) + (dy * sinA);
                float ly = (-dx * sinA) + (dy * cosA);

                // 가장 가까운 코너로부터의 내부 거리 (u: 가로 내측 거리, v: 세로 내측 거리)
                float u = halfW - MathF.Abs(lx);
                float v = halfH - MathF.Abs(ly);

                float sdf = EvaluateDefectiveCornerSdf(u, v, defectProfile);
                float coverage = 0.5f * (1.0f - ErfApprox(sdf * invSqrt2Sigma));
                float intensity = DefaultBackgroundIntensity + (contrast * coverage);

                if (defectProfile.NoiseSigma > 0.0f)
                {
                    intensity += SampleDeterministicGaussian((int)worldX, (int)worldY, seed) * defectProfile.NoiseSigma;
                }

                row[wx] = (byte)Math.Clamp((int)MathF.Round(intensity), 0, 255);
            }
        }
    }

    /// <summary>
    /// 외부 라이브러리 없이 순수 C# <see cref="FileStream"/>으로 8비트 그레이스케일 영상을 바이너리 PGM(P5) 포맷으로 저장한다.
    /// </summary>
    public static void SaveRoiAsPgm(ReadOnlyImageView2D<byte> image, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        byte[] header = Encoding.ASCII.GetBytes($"P5\n{image.Width} {image.Height}\n255\n");
        fs.Write(header);

        for (int y = 0; y < image.Height; y++)
        {
            fs.Write(image.GetRowSpan(y));
        }
    }

    /// <summary>
    /// 외부 라이브러리 없이 순수 C# <see cref="FileStream"/>으로 8비트 인덱스드 그레이스케일 BMP 파일을 디스크에 저장한다.
    /// </summary>
    public static void SaveRoiAsBmp(ReadOnlyImageView2D<byte> image, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        int width = image.Width;
        int height = image.Height;
        int rowAlignedStride = (width + 3) & ~3;
        int pixelDataSize = rowAlignedStride * height;
        const int headerSize = 14 + 40 + (256 * 4);
        int fileSize = headerSize + pixelDataSize;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write(0);
        bw.Write(headerSize);

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40);
        bw.Write(width);
        bw.Write(-height); // Top-down BMP
        bw.Write((short)1);
        bw.Write((short)8);
        bw.Write(0);
        bw.Write(pixelDataSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(256);
        bw.Write(0);

        // 256-entry grayscale palette
        for (int i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            bw.Write(b);
            bw.Write(b);
            bw.Write(b);
            bw.Write((byte)0);
        }

        Span<byte> pad = stackalloc byte[4];
        pad.Clear();
        int padBytes = rowAlignedStride - width;

        for (int y = 0; y < height; y++)
        {
            bw.Write(image.GetRowSpan(y));
            if (padBytes > 0)
            {
                bw.Write(pad.Slice(0, padBytes));
            }
        }
    }

    /// <summary>
    /// 로컬 코너 좌표 <c>(u, v)</c>에서 모따기(Chamfer), 코너 라운딩(Rounding),
    /// Leg 1 측면 칩핑(Chipping), Leg 2 측면 버(Burr)가 반영된 연속 부호 거리(SDF, 내부 음수 / 외부 양수)를 평가한다.
    /// </summary>
    private static float EvaluateDefectiveCornerSdf(float u, float v, CornerDefectProfile profile)
    {
        float shiftedV = v;
        if (profile.ChippingDepth > 0.0f && profile.ChippingLength > 1.0f)
        {
            float halfLen = profile.ChippingLength * 0.5f;
            float distFromChip = MathF.Abs(u - ChippingCenterOffsetAlongLeg1);
            if (distFromChip < halfLen)
            {
                float phase = MathF.Cos((MathF.PI * 0.5f) * (distFromChip / halfLen));
                // 오목한 깨짐 홈: 내부 경계가 안쪽(+v)으로 파고들어감
                shiftedV -= profile.ChippingDepth * (phase * phase);
            }
        }

        float shiftedU = u;
        if (profile.BurrHeight > 0.0f && profile.BurrLength > 1.0f)
        {
            float halfLen = profile.BurrLength * 0.5f;
            float distFromBurr = MathF.Abs(v - BurrCenterOffsetAlongLeg2);
            if (distFromBurr < halfLen)
            {
                float phase = MathF.Cos((MathF.PI * 0.5f) * (distFromBurr / halfLen));
                // 볼록한 돌기(Burr): 내부 경계가 바깥쪽(-u)으로 튀어나옴
                shiftedU += profile.BurrHeight * (phase * phase);
            }
        }

        float radius = MathF.Max(0.0f, profile.CornerRadius);
        float baseSdf;
        if (radius > 0.0f && shiftedU < radius && shiftedV < radius)
        {
            float qu = radius - shiftedU;
            float qv = radius - shiftedV;
            baseSdf = MathF.Sqrt((qu * qu) + (qv * qv)) - radius;
        }
        else
        {
            baseSdf = MathF.Max(-shiftedU, -shiftedV);
        }

        if (profile.ChamferLength > 0.0f)
        {
            float chamferSdf = (profile.ChamferLength - (shiftedU + shiftedV)) * InvSqrt2;
            baseSdf = MathF.Max(baseSdf, chamferSdf);
        }

        return baseSdf;
    }

    /// <summary>
    /// Abramowitz &amp; Stegun 7.1.26 고정밀 오차함수(<c>erf</c>) 근사 (최대 오차 1.5e-7).
    /// </summary>
    private static float ErfApprox(float x)
    {
        float sign = x < 0.0f ? -1.0f : 1.0f;
        float ax = MathF.Abs(x);

        const float p = 0.3275911f;
        const float a1 = 0.254829592f;
        const float a2 = -0.284496736f;
        const float a3 = 1.421413741f;
        const float a4 = -1.453152027f;
        const float a5 = 1.061405429f;

        float t = 1.0f / (1.0f + (p * ax));
        float poly = (((((a5 * t) + a4) * t + a3) * t + a2) * t + a1) * t;
        float y = 1.0f - (poly * MathF.Exp(-(ax * ax)));
        return sign * y;
    }

    private static float SampleDeterministicGaussian(int x, int y, uint seed)
    {
        uint h1 = HashUInt32((uint)x * 374761393u + (uint)y * 668265263u + seed);
        uint h2 = HashUInt32(h1 ^ 0x9E3779B9u);

        float u1 = Math.Clamp((h1 & 0x00FFFFFFu) / 16777216.0f, 1e-6f, 1.0f - 1e-6f);
        float u2 = (h2 & 0x00FFFFFFu) / 16777216.0f;

        return MathF.Sqrt(-2.0f * MathF.Log(u1)) * MathF.Cos(2.0f * MathF.PI * u2);
    }

    private static uint HashUInt32(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }
}
