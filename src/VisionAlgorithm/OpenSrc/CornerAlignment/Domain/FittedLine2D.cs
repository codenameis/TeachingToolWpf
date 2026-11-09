// File: src/VisionAlgorithm/OpenSrc/CornerAlignment/Domain/FittedLine2D.cs
using System;
using System.Numerics;

namespace VisionAlgorithm.OpenSrc.CornerAlignment.Domain;

/// <summary>
/// 모든 각도에서 특이점이 없는 헤세 법선 방정식(Hessian Normal Form, <c>Nx * x + Ny * y - Rho = 0</c>) 기반 2D 직선 모델.
/// </summary>
public readonly record struct FittedLine2D(
    float Nx,
    float Ny,
    float Rho,
    float TangentX,
    float TangentY,
    int InlierCount,
    float RmsResidual,
    bool IsValid = true)
{
    /// <summary>
    /// 무효 직선 인스턴스.
    /// </summary>
    public static FittedLine2D Invalid => new(0f, 0f, 0f, 0f, 0f, 0, float.PositiveInfinity, IsValid: false);

    /// <summary>
    /// 직선의 단위 법선 벡터 <c>(Nx, Ny)</c>.
    /// </summary>
    public Vector2 Normal => new(Nx, Ny);

    /// <summary>
    /// 직선의 단위 접선 방향 벡터 <c>(TangentX, TangentY)</c>.
    /// </summary>
    public Vector2 Tangent => new(TangentX, TangentY);

    /// <summary>
    /// 직선 접선 방향의 라디안 각도 <c>atan2(TangentY, TangentX)</c>를 반환한다.
    /// </summary>
    public float AngleRad => MathF.Atan2(TangentY, TangentX);

    /// <summary>
    /// 임의의 2D 점에서 직선까지의 부호 있는 직교 거리를 계산한다.
    /// </summary>
    public float SignedDistance(float x, float y) => (Nx * x) + (Ny * y) - Rho;

    /// <summary>
    /// 임의의 2D 점에서 직선까지의 절대 직교 거리를 계산한다.
    /// </summary>
    public float Distance(Vector2 point) => MathF.Abs(SignedDistance(point.X, point.Y));

    /// <summary>
    /// 임의의 2D 점을 직선 위로 직교 투영한 좌표를 반환한다.
    /// </summary>
    public Vector2 ProjectPoint(Vector2 point)
    {
        float d = SignedDistance(point.X, point.Y);
        return new Vector2(point.X - (d * Nx), point.Y - (d * Ny));
    }
}
