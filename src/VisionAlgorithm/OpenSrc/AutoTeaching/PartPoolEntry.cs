// File: src/VisionAlgorithm/OpenSrc/AutoTeaching/PartPoolEntry.cs
using VisionAlgorithm.OpenSrc.Common;
using VisionAlgorithm.OpenSrc.PatMax;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Immutable master entry in <see cref="PartModelPool"/> storing a part's unique <see cref="PartModelId"/>,
/// 64-byte <see cref="RotationInvariantSignature"/>, trained <see cref="PatMaxModel"/>, canonical $16 \times 16$
/// appearance prototype and <see cref="LedoitWolfMahalanobis"/> OOD covariance model, and relative <see cref="ChildRoiTemplateSpec"/> list.
/// </summary>
public sealed class PartPoolEntry : IDisposable
{
    public const int AppearanceGridSize = 16;
    public const int AppearanceDimension = AppearanceGridSize * AppearanceGridSize; // 256
    private const int MaxIcgnSamplePoints = 8192;
    private const int CoarseIcgnIterations = 8;
    private const int FineIcgnIterations = 10;
    private const float MinIcgnGradientSq = 36.0f;

    private readonly float[] _appearancePrototype;
    private readonly List<ChildRoiTemplateSpec> _childSpecs;
    private float[] _icgnLocalX = [];
    private float[] _icgnLocalY = [];
    private float[] _icgnTargetIntensity = [];
    private float[] _icgnCoarseGradX = [];
    private float[] _icgnCoarseGradY = [];
    private float[] _icgnCoarseGradTheta = [];
    private float _coarseInvH00, _coarseInvH01, _coarseInvH02, _coarseInvH11, _coarseInvH12, _coarseInvH22;
    private float[] _icgnGradX = [];
    private float[] _icgnGradY = [];
    private float[] _icgnGradTheta = [];
    private float _invH00, _invH01, _invH02, _invH11, _invH12, _invH22;
    private bool _hasIcgnModel;
    private bool _disposed;

    public PartModelId Id { get; }
    public string Name { get; }
    public float ReferenceWidth { get; }
    public float ReferenceHeight { get; }
    public RotationInvariantSignature Signature { get; private set; }
    public PatMaxModel GeometricModel { get; }
    public ReadOnlySpan<float> AppearancePrototype => _appearancePrototype;
    public LedoitWolfMahalanobis MahalanobisModel { get; }
    public IReadOnlyList<ChildRoiTemplateSpec> ChildSpecs => _childSpecs;
    public int TeachMergeCount { get; private set; } = 1;

    public PartPoolEntry(
        PartModelId id,
        string name,
        float referenceWidth,
        float referenceHeight,
        RotationInvariantSignature signature,
        PatMaxModel geometricModel,
        float[] appearancePrototype,
        LedoitWolfMahalanobis mahalanobisModel,
        IEnumerable<ChildRoiTemplateSpec>? childSpecs = null)
    {
        ArgumentNullException.ThrowIfNull(geometricModel);
        ArgumentNullException.ThrowIfNull(appearancePrototype);
        ArgumentNullException.ThrowIfNull(mahalanobisModel);

        if (appearancePrototype.Length != AppearanceDimension)
        {
            throw new ArgumentException($"Appearance prototype must have length {AppearanceDimension}.", nameof(appearancePrototype));
        }

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? $"Part_{id}" : name;
        ReferenceWidth = referenceWidth;
        ReferenceHeight = referenceHeight;
        Signature = signature;
        GeometricModel = geometricModel;
        _appearancePrototype = appearancePrototype;
        MahalanobisModel = mahalanobisModel;
        _childSpecs = childSpecs is not null ? new List<ChildRoiTemplateSpec>(childSpecs) : [];
    }

    internal void MergeRetaughtSpecs(
        in RotationInvariantSignature newSignature,
        ReadOnlySpan<ChildRoiTemplateSpec> additionalChildSpecs)
    {
        TeachMergeCount++;

        // Blend signature with running average and L2-renormalize
        Span<float> blended = stackalloc float[RotationInvariantSignature.FloatCount];
        ReadOnlySpan<float> oldSpan = Signature.AsSpan();
        ReadOnlySpan<float> newSpan = newSignature.AsSpan();
        float wOld = (float)(TeachMergeCount - 1) / TeachMergeCount;
        float wNew = 1.0f / TeachMergeCount;
        float normSq = 0f;

        for (int i = 0; i < RotationInvariantSignature.FloatCount; i++)
        {
            float v = (oldSpan[i] * wOld) + (newSpan[i] * wNew);
            blended[i] = v;
            normSq += v * v;
        }

        float invNorm = normSq > 1e-12f ? (1.0f / MathF.Sqrt(normSq)) : 1.0f;
        for (int i = 0; i < RotationInvariantSignature.FloatCount; i++)
        {
            blended[i] *= invNorm;
        }

        Signature = new RotationInvariantSignature(blended);

        for (int i = 0; i < additionalChildSpecs.Length; i++)
        {
            ChildRoiTemplateSpec candidate = additionalChildSpecs[i];
            bool exists = false;
            for (int j = 0; j < _childSpecs.Count; j++)
            {
                if (string.Equals(_childSpecs[j].ChildName, candidate.ChildName, StringComparison.Ordinal))
                {
                    _childSpecs[j] = candidate;
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                _childSpecs.Add(candidate);
            }
        }
    }

    internal void BuildIcgnTemplate(
        ReadOnlyImageView2D<byte> goldenImage,
        in FixturePose2D teachPose,
        float roiWidth,
        float roiHeight)
    {
        float halfW = roiWidth * 0.45f;
        float halfH = roiHeight * 0.45f;
        const float step = 1.0f;
        const float coarseDelta = 2.0f;
        const float fineDelta = 1.0f;

        List<(float lx, float ly, float t, float cgx, float cgy, float cgt, float fgx, float fgy, float fgt)> raw = new(4096);

        for (float ly = -halfH; ly <= halfH; ly += step)
        {
            for (float lx = -halfW; lx <= halfW; lx += step)
            {
                var p0 = teachPose.LocalToWorld(lx, ly);
                float t0 = SubpixelInterpolator.SampleBilinear(goldenImage, p0.X, p0.Y);

                var cPxP = teachPose.LocalToWorld(lx + coarseDelta, ly);
                var cPxM = teachPose.LocalToWorld(lx - coarseDelta, ly);
                var cPyP = teachPose.LocalToWorld(lx, ly + coarseDelta);
                var cPyM = teachPose.LocalToWorld(lx, ly - coarseDelta);
                float cgx = (SubpixelInterpolator.SampleBilinear(goldenImage, cPxP.X, cPxP.Y) -
                             SubpixelInterpolator.SampleBilinear(goldenImage, cPxM.X, cPxM.Y)) / (2.0f * coarseDelta);
                float cgy = (SubpixelInterpolator.SampleBilinear(goldenImage, cPyP.X, cPyP.Y) -
                             SubpixelInterpolator.SampleBilinear(goldenImage, cPyM.X, cPyM.Y)) / (2.0f * coarseDelta);

                var fPxP = teachPose.LocalToWorld(lx + fineDelta, ly);
                var fPxM = teachPose.LocalToWorld(lx - fineDelta, ly);
                var fPyP = teachPose.LocalToWorld(lx, ly + fineDelta);
                var fPyM = teachPose.LocalToWorld(lx, ly - fineDelta);
                float fgx = (SubpixelInterpolator.SampleBilinear(goldenImage, fPxP.X, fPxP.Y) -
                             SubpixelInterpolator.SampleBilinear(goldenImage, fPxM.X, fPxM.Y)) / (2.0f * fineDelta);
                float fgy = (SubpixelInterpolator.SampleBilinear(goldenImage, fPyP.X, fPyP.Y) -
                             SubpixelInterpolator.SampleBilinear(goldenImage, fPyM.X, fPyM.Y)) / (2.0f * fineDelta);

                if ((cgx * cgx) + (cgy * cgy) >= MinIcgnGradientSq || (fgx * fgx) + (fgy * fgy) >= MinIcgnGradientSq)
                {
                    float cgt = (-ly * cgx) + (lx * cgy);
                    float fgt = (-ly * fgx) + (lx * fgy);
                    raw.Add((lx, ly, t0, cgx, cgy, cgt, fgx, fgy, fgt));
                }
            }
        }

        if (raw.Count < 16)
        {
            _hasIcgnModel = false;
            return;
        }

        int count = Math.Min(raw.Count, MaxIcgnSamplePoints);
        _icgnLocalX = new float[count];
        _icgnLocalY = new float[count];
        _icgnTargetIntensity = new float[count];
        _icgnCoarseGradX = new float[count];
        _icgnCoarseGradY = new float[count];
        _icgnCoarseGradTheta = new float[count];
        _icgnGradX = new float[count];
        _icgnGradY = new float[count];
        _icgnGradTheta = new float[count];

        for (int i = 0; i < count; i++)
        {
            int srcIdx = (int)((long)i * raw.Count / count);
            var item = raw[srcIdx];
            _icgnLocalX[i] = item.lx;
            _icgnLocalY[i] = item.ly;
            _icgnTargetIntensity[i] = item.t;
            _icgnCoarseGradX[i] = item.cgx;
            _icgnCoarseGradY[i] = item.cgy;
            _icgnCoarseGradTheta[i] = item.cgt;
            _icgnGradX[i] = item.fgx;
            _icgnGradY[i] = item.fgy;
            _icgnGradTheta[i] = item.fgt;
        }

        bool okCoarse = Invert3x3Hessian(
            _icgnCoarseGradX, _icgnCoarseGradY, _icgnCoarseGradTheta,
            out _coarseInvH00, out _coarseInvH01, out _coarseInvH02,
            out _coarseInvH11, out _coarseInvH12, out _coarseInvH22);
        bool okFine = Invert3x3Hessian(
            _icgnGradX, _icgnGradY, _icgnGradTheta,
            out _invH00, out _invH01, out _invH02,
            out _invH11, out _invH12, out _invH22);

        _hasIcgnModel = okCoarse && okFine;
    }

    private static bool Invert3x3Hessian(
        ReadOnlySpan<float> gx,
        ReadOnlySpan<float> gy,
        ReadOnlySpan<float> gt,
        out float inv00, out float inv01, out float inv02,
        out float inv11, out float inv12, out float inv22)
    {
        double h00 = 1e-3, h01 = 0.0, h02 = 0.0;
        double h11 = 1e-3, h12 = 0.0, h22 = 1e-3;
        for (int i = 0; i < gx.Length; i++)
        {
            h00 += gx[i] * gx[i];
            h01 += gx[i] * gy[i];
            h02 += gx[i] * gt[i];
            h11 += gy[i] * gy[i];
            h12 += gy[i] * gt[i];
            h22 += gt[i] * gt[i];
        }

        double c00 = (h11 * h22) - (h12 * h12);
        double c01 = -((h01 * h22) - (h12 * h02));
        double c02 = (h01 * h12) - (h11 * h02);
        double c11 = (h00 * h22) - (h02 * h02);
        double c12 = -((h00 * h12) - (h01 * h02));
        double c22 = (h00 * h11) - (h01 * h01);

        double det = (h00 * c00) + (h01 * c01) + (h02 * c02);
        if (Math.Abs(det) <= 1e-9)
        {
            inv00 = inv01 = inv02 = inv11 = inv12 = inv22 = 0f;
            return false;
        }

        double invDet = 1.0 / det;
        inv00 = (float)(c00 * invDet);
        inv01 = (float)(c01 * invDet);
        inv02 = (float)(c02 * invDet);
        inv11 = (float)(c11 * invDet);
        inv12 = (float)(c12 * invDet);
        inv22 = (float)(c22 * invDet);
        return true;
    }

    internal FixturePose2D RefinePoseIcgn(
        ReadOnlyImageView2D<byte> runtimeImage,
        FixturePose2D pose)
    {
        if (!_hasIcgnModel || _icgnLocalX.Length == 0)
        {
            return pose;
        }

        // Stage A: Coarse-stencil ICGN (wide capture basin +-2.5 px, +-2.5 deg)
        pose = RunIcgnStage(
            runtimeImage, pose, CoarseIcgnIterations,
            _icgnCoarseGradX, _icgnCoarseGradY, _icgnCoarseGradTheta,
            _coarseInvH00, _coarseInvH01, _coarseInvH02, _coarseInvH11, _coarseInvH12, _coarseInvH22,
            maxStepTranslation: 1.25f, maxStepAngle: 0.035f);

        // Stage B: Fine-stencil ICGN (high sub-pixel precision)
        pose = RunIcgnStage(
            runtimeImage, pose, FineIcgnIterations,
            _icgnGradX, _icgnGradY, _icgnGradTheta,
            _invH00, _invH01, _invH02, _invH11, _invH12, _invH22,
            maxStepTranslation: 0.35f, maxStepAngle: 0.010f);

        // Stage C: Direct gradient-free parabolic SSD minimization over (u, v, theta) to eliminate 8-bit gradient quantization bias
        ReadOnlySpan<float> posSteps = [0.32f, 0.16f, 0.08f];
        ReadOnlySpan<float> angSteps = [0.0045f, 0.0020f, 0.0009f];
        for (int s = 0; s < posSteps.Length; s++)
        {
            float hp = posSteps[s];
            float ha = angSteps[s];

            float e0 = EvaluateSsd(runtimeImage, in pose);
            float eUm = EvaluateSsd(runtimeImage, pose.Compose(new FixturePose2D(-hp, 0f, 0f)));
            float eUp = EvaluateSsd(runtimeImage, pose.Compose(new FixturePose2D(+hp, 0f, 0f)));
            float du = SubpixelInterpolator.ComputeParabolicPeakOffset(-eUm, -e0, -eUp) * hp;
            pose = pose.Compose(new FixturePose2D(du, 0f, 0f));

            e0 = EvaluateSsd(runtimeImage, in pose);
            float eVm = EvaluateSsd(runtimeImage, pose.Compose(new FixturePose2D(0f, -hp, 0f)));
            float eVp = EvaluateSsd(runtimeImage, pose.Compose(new FixturePose2D(0f, +hp, 0f)));
            float dv = SubpixelInterpolator.ComputeParabolicPeakOffset(-eVm, -e0, -eVp) * hp;
            pose = pose.Compose(new FixturePose2D(0f, dv, 0f));

            e0 = EvaluateSsd(runtimeImage, in pose);
            float eAm = EvaluateSsd(runtimeImage, pose.Compose(new FixturePose2D(0f, 0f, -ha)));
            float eAp = EvaluateSsd(runtimeImage, pose.Compose(new FixturePose2D(0f, 0f, +ha)));
            float dAng = SubpixelInterpolator.ComputeParabolicPeakOffset(-eAm, -e0, -eAp) * ha;
            pose = pose.Compose(new FixturePose2D(0f, 0f, dAng));
        }

        return pose;
    }

    private float EvaluateSsd(
        ReadOnlyImageView2D<byte> runtimeImage,
        in FixturePose2D pose)
    {
        int n = _icgnLocalX.Length;
        float cos = pose.CosTheta * pose.ScaleX;
        float sin = pose.SinTheta * pose.ScaleX;
        float cx = pose.CenterX;
        float cy = pose.CenterY;
        float ssd = 0f;

        for (int i = 0; i < n; i++)
        {
            float lx = _icgnLocalX[i];
            float ly = _icgnLocalY[i];
            float wx = cx + (lx * cos) - (ly * sin);
            float wy = cy + (lx * sin) + (ly * cos);
            float err = SubpixelInterpolator.SampleBilinear(runtimeImage, wx, wy) - _icgnTargetIntensity[i];
            ssd += err * err;
        }

        return ssd;
    }

    private FixturePose2D RunIcgnStage(
        ReadOnlyImageView2D<byte> runtimeImage,
        FixturePose2D pose,
        int maxIterations,
        ReadOnlySpan<float> gx,
        ReadOnlySpan<float> gy,
        ReadOnlySpan<float> gt,
        float inv00, float inv01, float inv02,
        float inv11, float inv12, float inv22,
        float maxStepTranslation,
        float maxStepAngle)
    {
        int n = _icgnLocalX.Length;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            float bx = 0f;
            float by = 0f;
            float bTheta = 0f;
            float cos = pose.CosTheta * pose.ScaleX;
            float sin = pose.SinTheta * pose.ScaleX;
            float cx = pose.CenterX;
            float cy = pose.CenterY;

            for (int i = 0; i < n; i++)
            {
                float lx = _icgnLocalX[i];
                float ly = _icgnLocalY[i];
                float wx = cx + (lx * cos) - (ly * sin);
                float wy = cy + (lx * sin) + (ly * cos);

                float val = SubpixelInterpolator.SampleBilinear(runtimeImage, wx, wy);
                float err = val - _icgnTargetIntensity[i];
                bx += gx[i] * err;
                by += gy[i] * err;
                bTheta += gt[i] * err;
            }

            float du = -((inv00 * bx) + (inv01 * by) + (inv02 * bTheta));
            float dv = -((inv01 * bx) + (inv11 * by) + (inv12 * bTheta));
            float dTheta = -((inv02 * bx) + (inv12 * by) + (inv22 * bTheta));

            du = Math.Clamp(du, -maxStepTranslation, maxStepTranslation);
            dv = Math.Clamp(dv, -maxStepTranslation, maxStepTranslation);
            dTheta = Math.Clamp(dTheta, -maxStepAngle, maxStepAngle);

            pose = pose.Compose(new FixturePose2D(du, dv, dTheta));

            if ((du * du) + (dv * dv) < 1e-8f && MathF.Abs(dTheta) < 1e-6f)
            {
                break;
            }
        }

        return pose;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GeometricModel.Dispose();
    }
}
