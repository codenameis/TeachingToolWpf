using System.Collections.Immutable;

namespace VisionCanvas.Layers;

/// <summary>이름은 대소문자를 구분한다. 생성 순서대로 겹쳐 표시한다.</summary>
public sealed class DrawingLayerCollection
{
    private readonly Dictionary<string, DrawingLayer> _layers = new(StringComparer.Ordinal);
    private readonly Action _verifyAccess;
    private readonly Action _changed;
    private ImmutableArray<LayerSnapshot> _snapshot = [];

    internal DrawingLayerCollection(Action verifyAccess, Action changed)
    {
        _verifyAccess = verifyAccess;
        _changed = changed;
    }

    public DrawingLayer this[string name]
    {
        get
        {
            _verifyAccess();
            return _layers[name];
        }
    }

    internal ImmutableArray<LayerSnapshot> Snapshot => _snapshot;

    public DrawingLayer Create(string name, LayerSpace space)
    {
        _verifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(space))
        {
            throw new ArgumentOutOfRangeException(nameof(space));
        }
        if (_layers.ContainsKey(name))
        {
            throw new ArgumentException("같은 이름의 레이어가 이미 있습니다.", nameof(name));
        }
        var index = _snapshot.Length;
        var layer = new DrawingLayer(name, space, _verifyAccess, () => Refresh(name, index));
        _layers.Add(name, layer);
        _snapshot = _snapshot.Add(layer.Snapshot);
        _changed();
        return layer;
    }

    private void Refresh(string name, int index)
    {
        _snapshot = _snapshot.SetItem(index, _layers[name].Snapshot);
        _changed();
    }
}
