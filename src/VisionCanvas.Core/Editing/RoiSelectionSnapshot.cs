using VisionCanvas.Core.Geometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>선택된 ROI들의 공간 인덱스와 특성, 변환 준비 상태를 보관하는 불변 스냅샷이다.</summary>
public sealed class RoiSelectionSnapshot
{
    private readonly RoiSnapshot _snapshot;
    private readonly ImageReference? _image;
    private Lazy<RoiSpatialIndex>? _selectedSpatial;
    private Lazy<RoiSelectionFacts>? _selectionFacts;
    private Lazy<RoiTransformSelection>? _transformSelection;

    public RoiIdSet Selection { get; }

    public RoiSelectionSnapshot(RoiSnapshot snapshot, RoiIdSet selection, ImageReference? image)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selection);
        _snapshot = snapshot;
        Selection = selection;
        _image = image;
    }

    public bool IsTransformSelectionPrepared
    {
        get
        {
            var value = Volatile.Read(ref _transformSelection);
            return value?.IsValueCreated == true && value.Value.IsPrepared;
        }
    }

    public RoiTransformSelection GetTransformSelection(DocumentState owner)
    {
        var value = Volatile.Read(ref _transformSelection);
        if (value is null)
        {
            value = new(() => new(owner));
            value = Interlocked.CompareExchange(ref _transformSelection, value, null) ?? value;
        }

        return value.Value;
    }

    public RoiSelectionFacts SelectionFacts
    {
        get
        {
            var value = Volatile.Read(ref _selectionFacts);
            if (value is null)
            {
                value = new(() => new(_snapshot.Rois, Selection, _snapshot));
                value = Interlocked.CompareExchange(ref _selectionFacts, value, null) ?? value;
            }

            return value.Value;
        }
    }

    public RoiSpatialIndex SelectedSpatial
    {
        get
        {
            if (Selection.Count == _snapshot.Rois.Count)
            {
                return _snapshot.Spatial;
            }

            var value = Volatile.Read(ref _selectedSpatial);
            if (value is null)
            {
                value = new(BuildSelectedSpatial);
                value = Interlocked.CompareExchange(ref _selectedSpatial, value, null) ?? value;
            }

            return value.Value;
        }
    }

    internal RoiSelectionSnapshot WithImage(ImageReference? image)
    {
        if (_image == image)
        {
            return this;
        }

        var next = new RoiSelectionSnapshot(_snapshot, Selection, image)
        {
            _selectedSpatial = _selectedSpatial,
            _selectionFacts = _selectionFacts
        };
        return next;
    }

    private RoiSpatialIndex BuildSelectedSpatial()
    {
        var shouldFilterExistingSpatial = _snapshot.IsSpatialCreated
            && Selection.Count > 64
            && Selection.Count > _snapshot.Rois.Count / 8;
        if (shouldFilterExistingSpatial)
        {
            return _snapshot.Spatial.Filter(Selection, include: true);
        }

        return new RoiSpatialIndex(Selection.Select(id => _snapshot.Rois[id]));
    }
}
