using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// 2D grid of <see cref="AttractionCell"/> values in model pattern coordinates backed by <see cref="ArrayPool{T}"/>.
/// </summary>
public sealed class AttractionField : IDisposable
{
    private AttractionCell[]? _cells;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public float OriginX { get; }
    public float OriginY { get; }
    public float CellSize { get; }
    public float InvCellSize { get; }
    public bool IsEmpty => Width == 0 || Height == 0;

    public Span<AttractionCell> Cells
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _cells.AsSpan(0, Height * Stride);
        }
    }

    private AttractionField(int width, int height, int stride, float originX, float originY, float cellSize)
    {
        Width = width;
        Height = height;
        Stride = stride;
        OriginX = originX;
        OriginY = originY;
        CellSize = cellSize;
        InvCellSize = cellSize > 0f ? 1f / cellSize : 0f;

        var totalCells = checked(height * stride);
        _cells = totalCells == 0 ? [] : ArrayPool<AttractionCell>.Shared.Rent(totalCells);
    }

    public static AttractionField Empty { get; } = new(0, 0, 0, 0f, 0f, 1f);

    public static AttractionField Rent(int width, int height, float originX, float originY, float cellSize = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);

        return new AttractionField(width, height, width, originX, originY, cellSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly AttractionCell CellAt(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return ref _cells![(y * Stride) + x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySampleNearest(float modelX, float modelY, out AttractionCell cell)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gridX = (int)MathF.Round((modelX - OriginX) * InvCellSize);
        var gridY = (int)MathF.Round((modelY - OriginY) * InvCellSize);
        if ((uint)gridX >= (uint)Width || (uint)gridY >= (uint)Height)
        {
            cell = AttractionCell.Unassigned;
            return false;
        }

        cell = _cells![(gridY * Stride) + gridX];
        return cell.HasNearestDipole;
    }

    public void Dispose()
    {
        if (_disposed || IsEmpty)
        {
            return;
        }

        _disposed = true;
        var array = _cells;
        _cells = null;
        if (array is { Length: > 0 })
        {
            ArrayPool<AttractionCell>.Shared.Return(array);
        }
    }
}
