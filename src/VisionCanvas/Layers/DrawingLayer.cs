using System.Collections.Immutable;
using VisionGeometry;

namespace VisionCanvas.Layers;

internal sealed record LayerItem(Guid Id, CanvasGraphic Graphic)
{
    // 불변 형상의 윤곽은 추가/교체할 때만 만든다. pan/zoom 프레임마다 배열을 만들지 않는다.
    public ImmutableArray<PointD> Outline { get; } = Graphic is ShapeGraphic shape ? shape.Geometry.Outline.ToImmutableArray() : [];
}
internal sealed record LayerSnapshot(LayerSpace Space, bool IsVisible, ImmutableSortedDictionary<long, LayerItem> Items);

/// <summary>생성한 Canvas의 UI 스레드에서 변경한다. 일괄 변경은 검증 성공 후 한 번만 게시한다.</summary>
public sealed class DrawingLayer
{
    private readonly Action _verifyAccess;
    private readonly Action _changed;
    private ImmutableDictionary<Guid, long> _orders = ImmutableDictionary<Guid, long>.Empty;
    private long _nextOrder;

    internal DrawingLayer(string name, LayerSpace space, Action verifyAccess, Action changed)
    {
        Name = name;
        Space = space;
        _verifyAccess = verifyAccess;
        _changed = changed;
        Snapshot = new(space, true, ImmutableSortedDictionary<long, LayerItem>.Empty);
    }

    public string Name
    {
        get;
    }
    public LayerSpace Space
    {
        get;
    }
    public int Count => Snapshot.Items.Count;
    internal LayerSnapshot Snapshot
    {
        get; private set;
    }

    public bool IsVisible
    {
        get => Snapshot.IsVisible;
        set
        {
            _verifyAccess();
            if (value == Snapshot.IsVisible)
            {
                return;
            }
            Snapshot = Snapshot with
            {
                IsVisible = value
            };
            _changed();
        }
    }

    public Guid Add(CanvasGraphic graphic) => AddRange([graphic])[0];

    public ImmutableArray<Guid> AddRange(IEnumerable<CanvasGraphic> graphics) => SetItems(graphics, false);

    /// <summary>기존 항목을 한 번에 교체한다. 반환한 새 ID를 사용해야 한다.</summary>
    public ImmutableArray<Guid> ReplaceAll(IEnumerable<CanvasGraphic> graphics) => SetItems(graphics, true);

    private ImmutableArray<Guid> SetItems(IEnumerable<CanvasGraphic> graphics, bool replace)
    {
        _verifyAccess();
        ArgumentNullException.ThrowIfNull(graphics);
        var items = (replace ? ImmutableSortedDictionary<long, LayerItem>.Empty : Snapshot.Items).ToBuilder();
        var orders = (replace ? ImmutableDictionary<Guid, long>.Empty : _orders).ToBuilder();
        var nextOrder = replace ? 0 : _nextOrder;
        var ids = ImmutableArray.CreateBuilder<Guid>();
        foreach (var graphic in graphics)
        {
            ArgumentNullException.ThrowIfNull(graphic);
            graphic.Validate(Space);
            var id = Guid.NewGuid();
            items.Add(nextOrder, new(id, graphic));
            orders.Add(id, nextOrder);
            nextOrder = checked(nextOrder + 1);
            ids.Add(id);
        }
        if (ids.Count == 0 && (!replace || Count == 0))
        {
            return [];
        }
        // 열거/검증 실패 때 현재 항목과 렌더 스냅샷을 함께 보존한다.
        _orders = orders.ToImmutable();
        _nextOrder = nextOrder;
        Snapshot = Snapshot with
        {
            Items = items.ToImmutable()
        };
        _changed();
        return ids.ToImmutable();
    }

    public bool Update(Guid id, CanvasGraphic graphic)
    {
        _verifyAccess();
        ArgumentNullException.ThrowIfNull(graphic);
        graphic.Validate(Space);
        if (!_orders.TryGetValue(id, out var order))
        {
            return false;
        }
        Snapshot = Snapshot with
        {
            Items = Snapshot.Items.SetItem(order, new(id, graphic))
        };
        _changed();
        return true;
    }

    public bool Remove(Guid id)
    {
        _verifyAccess();
        if (!_orders.TryGetValue(id, out var order))
        {
            return false;
        }
        _orders = _orders.Remove(id);
        Snapshot = Snapshot with
        {
            Items = Snapshot.Items.Remove(order)
        };
        _changed();
        return true;
    }

    public void Clear() => ReplaceAll([]);
}
