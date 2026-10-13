using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Editing;

/// <summary>반복 배치 편집의 불변 상태와 한 gesture당 한 번의 Undo/Redo를 소유한다.</summary>
public sealed class RepeatedRoiSession
{
    private readonly List<(RepeatedRoiGrid Before, RepeatedRoiGrid After, int? Selection)> _history = [];
    private int _cursor;
    private RepeatedRoiGrid? _before;
    private RoiSpatialIndex? _constraint;
    private RotationRange? _rotationLimits;
    private const long MaximumHistoryBytes = 1024L * 1024 * 1024;

    public RepeatedRoiGrid Grid
    {
        get; private set;
    }
    public ImageReference Image
    {
        get;
    }
    public int? SelectedIndex
    {
        get; private set;
    }
    public ViewportTransform Viewport { get; private set; } = ViewportTransform.Identity;
    public RoiGeometry SelectionGeometry => SelectedIndex is { } index ? Grid.Geometry(index) : Grid.Frame;
    public bool IsPreviewing => _before is not null;
    public bool CanUndo => !IsPreviewing && _cursor > 0;
    public bool CanRedo => !IsPreviewing && _cursor < _history.Count;
    public event Action? Changed;

    public RepeatedRoiSession(RepeatedRoiGrid grid, ImageReference image)
    {
        if (!RoiImageBounds.Fits(grid.Frame.Bounds, image))
        {
            throw new ArgumentException("격자가 이미지 밖에 있습니다.", nameof(grid));
        }
        Grid = grid;
        Image = image;
    }

    public void SetViewport(ViewportTransform view)
    {
        Viewport = view;
        Changed?.Invoke();
    }

    public void Select(int? index)
    {
        if (index is { } value)
        {
            _ = Grid.LocalGeometry(value);
        }
        Cancel();
        SelectedIndex = index;
        Changed?.Invoke();
    }

    public void Begin()
    {
        if (_before is not null)
        {
            throw new InvalidOperationException("이미 편집 중입니다.");
        }
        _before = Grid;
        var geometries = SelectedIndex is null ? Grid.ConstraintGeometries() : [SelectionGeometry];
        _constraint = new(geometries.Select((geometry, index) => new Roi(
            new Guid(index, 0, 0, new byte[8]), "", geometry, null, false, index)));
        _rotationLimits = null;
    }

    public void PreviewTranslation(PointD requested)
    {
        if (!requested.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }
        var before = _before ?? throw new InvalidOperationException("편집을 먼저 시작하세요.");
        var bounds = _constraint!.Bounds;
        var delta = new PointD(Math.Clamp(requested.X, -bounds.Left, Image.Width - bounds.Right),
            Math.Clamp(requested.Y, -bounds.Top, Image.Height - bounds.Bottom));
        Apply(before, new(default, 0, delta));
    }

    public void PreviewRotation(double degrees)
    {
        var before = _before ?? throw new InvalidOperationException("편집을 먼저 시작하세요.");
        var geometry = SelectedIndex is { } index ? before.Geometry(index) : before.Frame;
        var limits = _rotationLimits ??= _constraint!.RotationLimits(geometry.Center, Image);
        Apply(before, new(geometry.Center, limits.Clamp(degrees)));
    }

    private void Apply(RepeatedRoiGrid before, RigidTransform2D transform)
    {
        Grid = SelectedIndex is { } index
            ? before.WithGeometry(index, RoiTransformSelection.TransformGeometry(before.Geometry(index), transform))
            : before.Transform(transform);
        Changed?.Invoke();
    }

    public void SetGeometry(RoiGeometry geometry)
    {
        if (SelectedIndex is not { } index)
        {
            throw new InvalidOperationException("개별 ROI를 먼저 선택하세요.");
        }
        if (!RoiImageBounds.Fits(geometry.Bounds, Image))
        {
            throw new ArgumentException("ROI는 이미지 안에 있어야 합니다.", nameof(geometry));
        }
        Begin();
        Grid = Grid.WithGeometry(index, geometry);
        Complete();
    }

    public void Complete()
    {
        var before = _before;
        _before = null;
        _constraint = null;
        if (before is not null && Grid != before)
        {
            if (EstimateBytes(before, Grid) > MaximumHistoryBytes)
            {
                Grid = before;
                Changed?.Invoke();
                throw new InvalidOperationException("편집 한 건이 1 GiB 이력 예산을 초과하여 적용하지 않았습니다.");
            }
            _history.RemoveRange(_cursor, _history.Count - _cursor);
            _history.Add((before, Grid, SelectedIndex));
            var bytes = _history.Sum(edit => EstimateBytes(edit.Before, edit.After));
            while (_history.Count > 200 || bytes > MaximumHistoryBytes)
            {
                bytes -= EstimateBytes(_history[0].Before, _history[0].After);
                _history.RemoveAt(0);
            }
            _cursor = _history.Count;
        }
        Changed?.Invoke();
    }

    private static long EstimateBytes(RepeatedRoiGrid before, RepeatedRoiGrid after)
        => 512L + (((long)before.Overrides.Count + after.Overrides.Count) * 256);

    public void Cancel()
    {
        if (_before is { } before)
        {
            Grid = before;
            _before = null;
            _constraint = null;
            Changed?.Invoke();
        }
    }

    public void Undo()
    {
        if (CanUndo)
        {
            var edit = _history[--_cursor];
            Grid = edit.Before;
            SelectedIndex = edit.Selection;
            Changed?.Invoke();
        }
    }

    public void Redo()
    {
        if (CanRedo)
        {
            var edit = _history[_cursor++];
            Grid = edit.After;
            SelectedIndex = edit.Selection;
            Changed?.Invoke();
        }
    }
}
