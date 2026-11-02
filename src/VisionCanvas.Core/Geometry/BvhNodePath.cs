using System.Numerics;

namespace VisionCanvas.Core.Geometry;

/// <summary>
/// BVH 트리에서 루트(1)부터 특정 잎(Leaf) 노드까지 내려가는 이진 경로를 비트열로 표현한 값 객체.
/// <para>default(BvhNodePath) 값도 항상 유효한 Root(RawValue == 1)가 되도록 불변식을 보장한다.</para>
/// <para>비트 레이아웃: 최상위 유효 비트(MSB) '1'은 루트 노드를 뜻하며, 그 아래 비트부터 왼쪽 자식은 0, 오른쪽 자식은 1을 차례로 덧붙인다.</para>
/// <para>예: 루트 = 1(0b1), 루트→왼쪽 = 2(0b10), 루트→오른쪽 = 3(0b11), 루트→왼쪽→오른쪽 = 5(0b101).</para>
/// </summary>
public readonly record struct BvhNodePath
{
    /// <summary>64비트 부호 없는 정수에서 안전하게 표현할 수 있는 최대 트리 높이 상한.</summary>
    public const int MaxSupportedHeight = 48;

    private const int MaxDepth = 62;
    private readonly ulong _valueMinusOne;

    private BvhNodePath(ulong rawValue)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rawValue);
        _valueMinusOne = rawValue - 1;
    }

    public static BvhNodePath Root => default;

    public ulong RawValue => _valueMinusOne + 1;

    public int Depth => BitOperations.Log2(RawValue);

    public BvhNodePath LeftChild
    {
        get
        {
            if (Depth >= MaxDepth)
            {
                throw new InvalidOperationException($"BVH 경로 깊이는 {MaxDepth}단계를 넘을 수 없습니다.");
            }

            return new BvhNodePath(RawValue << 1);
        }
    }

    public BvhNodePath RightChild
    {
        get
        {
            if (Depth >= MaxDepth)
            {
                throw new InvalidOperationException($"BVH 경로 깊이는 {MaxDepth}단계를 넘을 수 없습니다.");
            }

            return new BvhNodePath((RawValue << 1) | 1UL);
        }
    }

    public BvhNodePath Left => LeftChild;

    public BvhNodePath Right => RightChild;

    /// <summary>루트 바로 아래 단계에서 시작해 하위 노드로 내려갈 때 방향을 판별할 첫 비트 마스크를 구한다.</summary>
    public ulong FirstStepBit => Depth == 0 ? 0UL : 1UL << (Depth - 1);

    public bool IsRightBranch(ulong stepBit) => (RawValue & stepBit) != 0;
}
