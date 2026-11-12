using System.Numerics;
using System.Runtime.CompilerServices;

namespace VisionAlgorithm.OpenSrc.Calibration;

/// <summary>
/// Camera intrinsic matrix $K$ and Brown-Conrady radial ($k_1, k_2, k_3$) and decentering tangential ($p_1, p_2$)
/// lens distortion parameters (Brown, "Decentering Distortion of Lenses", Photogrammetric Engineering 32(3):444-462, 1966).
/// </summary>
public readonly record struct BrownConradyModel
{
    private readonly bool _isInitialized;

    public float FocalLengthX { get; }
    public float FocalLengthY { get; }
    public float PrincipalPointX { get; }
    public float PrincipalPointY { get; }
    public float K1 { get; }
    public float K2 { get; }
    public float K3 { get; }
    public float P1 { get; }
    public float P2 { get; }
    public bool IsValid => _isInitialized && FocalLengthX > 0f && FocalLengthY > 0f;

    public BrownConradyModel(
        float focalLengthX,
        float focalLengthY,
        float principalPointX,
        float principalPointY,
        float k1,
        float k2,
        float p1 = 0f,
        float p2 = 0f,
        float k3 = 0f)
    {
        if (!float.IsFinite(focalLengthX) || focalLengthX <= 0f || !float.IsFinite(focalLengthY) || focalLengthY <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(focalLengthX), "Focal lengths must be finite and positive.");
        }

        FocalLengthX = focalLengthX;
        FocalLengthY = focalLengthY;
        PrincipalPointX = principalPointX;
        PrincipalPointY = principalPointY;
        K1 = k1;
        K2 = k2;
        K3 = k3;
        P1 = p1;
        P2 = p2;
        _isInitialized = true;
    }

    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("BrownConradyModel was not initialized via its constructor.");
        }
    }
}
