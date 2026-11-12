using System.Collections.Frozen;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>Immutable selection and indexes reused by every sample of a rigid edit.</summary>
public sealed class RoiTransformSelection
{
    public DocumentState Before { get; }

    public IReadOnlySet<Guid> Affected { get; }

    private readonly Lazy<RoiSpatialIndex> _moving;
    private readonly Lazy<RoiSpatialIndex> _stationary;
    private readonly Lazy<RoiSpatialIndex> _movingSearch;
    private readonly Lazy<RoiSpatialIndex> _stationarySearch;
    private RotationConstraint? _rotationConstraint;
    private readonly Lazy<(RoiGeometry Parent, RoiGeometry Child)[]> _groupBoundaries;

    public RoiSpatialIndex Moving => _moving.Value;

    public RoiSpatialIndex Stationary => _stationary.Value;

    public RoiSpatialIndex MovingSearch => _movingSearch.Value;

    public RoiSpatialIndex StationarySearch => _stationarySearch.Value;

    public BoundsD Bounds { get; }

    public bool IsPrepared
    {
        get
        {
            var cached = Volatile.Read(ref _rotationConstraint);
            return _groupBoundaries.IsValueCreated
                && _moving.IsValueCreated
                && _stationary.IsValueCreated
                && _movingSearch.IsValueCreated
                && _stationarySearch.IsValueCreated
                && cached is { } constraint
                && constraint.Pivot == Bounds.Center
                && constraint.Limits.IsValueCreated;
        }
    }

    public RoiTransformSelection(DocumentState before)
    {
        Before = before;
        // Expanded membership never changes during a gesture; freeze once instead of building persistent tree nodes.
        Affected = ResolveAffectedSet(before);
        _groupBoundaries = new(() => RoiGroupBounds.StationaryParents(before, Affected));
        Bounds = before.SelectedSpatial.Bounds;
        var allSelected = Affected.Count == before.Rois.Count;
        _movingSearch = new(() => BuildMovingSearchIndex(before, Affected, allSelected));
        _stationarySearch = new(() => BuildStationarySearchIndex(before, Affected, allSelected));
        _moving = new(() => BuildMovingSpatialIndex(before, Affected, allSelected));
        _stationary = new(() => BuildStationarySpatialIndex(before, Affected, allSelected));
    }

    private static IReadOnlySet<Guid> ResolveAffectedSet(DocumentState before)
    {
        if (before.Selection.Count == before.Rois.Count || before.Snapshot.Children.Count == 0)
        {
            return before.Selection;
        }

        return RoiHierarchy.Subtree(before, RoiHierarchy.EffectiveRoots(before)).ToFrozenSet();
    }

    private static RoiSpatialIndex BuildMovingSearchIndex(DocumentState before, IReadOnlySet<Guid> affected, bool allSelected)
    {
        if (allSelected)
        {
            return before.Snapshot.SearchSpatial;
        }

        return before.Snapshot.SearchSpatial.Filter(affected, include: true);
    }

    private static RoiSpatialIndex BuildStationarySearchIndex(DocumentState before, IReadOnlySet<Guid> affected, bool allSelected)
    {
        if (allSelected)
        {
            return new([]);
        }

        return before.Snapshot.SearchSpatial.Filter(affected, include: false);
    }

    private static RoiSpatialIndex BuildMovingSpatialIndex(DocumentState before, IReadOnlySet<Guid> affected, bool allSelected)
    {
        if (allSelected)
        {
            return before.Snapshot.Spatial;
        }

        if (ReferenceEquals(affected, before.Selection) || affected.SetEquals(before.Selection))
        {
            return before.SelectedSpatial;
        }

        var shouldFilterExistingSpatial = before.Snapshot.IsSpatialCreated
            && affected.Count > 64
            && affected.Count > before.Rois.Count / 8;
        if (shouldFilterExistingSpatial)
        {
            return before.Snapshot.Spatial.Filter(affected, include: true);
        }

        return new(affected.Select(id => before.Rois[id]));
    }

    private static RoiSpatialIndex BuildStationarySpatialIndex(DocumentState before, IReadOnlySet<Guid> affected, bool allSelected)
    {
        if (allSelected)
        {
            return new([]);
        }

        if (affected.Count <= 64)
        {
            return before.Snapshot.Spatial.WithChanges(affected.Select(id => (id, (Roi?)null)));
        }

        if (before.Snapshot.IsSpatialCreated)
        {
            return before.Snapshot.Spatial.Filter(affected, include: false);
        }

        return new(before.Rois.Values.Where(r => !affected.Contains(r.Id)));
    }

    /// <summary>드래그/변환 중 즉시 참조하는 공간 인덱스와 회전 경계를 백그라운드에서 미리 준비한다.</summary>
    public void Warm(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _ = Moving;
        token.ThrowIfCancellationRequested();
        _ = Stationary;
        token.ThrowIfCancellationRequested();
        _ = MovingSearch;
        token.ThrowIfCancellationRequested();
        _ = StationarySearch;
        token.ThrowIfCancellationRequested();
        _ = RotationLimits(Bounds.Center);
        token.ThrowIfCancellationRequested();
    }

    public PointD Constrain(PointD requested, PointD previous = default, bool allowSlide = false)
    {
        if (!requested.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        if (Before.Image is not { } image || Affected.Count == 0)
        {
            return RoiGroupBounds.ConstrainTranslation(_groupBoundaries.Value, requested, previous, allowSlide);
        }

        var bounds = Moving.Bounds;
        var fraction = 1d;
        if (requested.X > 0)
        {
            fraction = Math.Min(fraction, (image.Width - bounds.Right) / requested.X);
        }

        if (requested.X < 0)
        {
            fraction = Math.Min(fraction, -bounds.Left / requested.X);
        }

        if (requested.Y > 0)
        {
            fraction = Math.Min(fraction, (image.Height - bounds.Bottom) / requested.Y);
        }

        if (requested.Y < 0)
        {
            fraction = Math.Min(fraction, -bounds.Top / requested.Y);
        }

        var imageBounds = new BoundsD(-bounds.Left, -bounds.Top, image.Width - bounds.Right, image.Height - bounds.Bottom);
        // 부모 벽을 따라 이동할 때 이미지 밖의 X 입력 때문에 유효한 Y 이동까지 축소하지 않는다.
        PointD target;
        if (allowSlide && _groupBoundaries.Value.Length > 0)
        {
            target = new PointD(
                Math.Clamp(requested.X, imageBounds.Left, imageBounds.Right),
                Math.Clamp(requested.Y, imageBounds.Top, imageBounds.Bottom));
        }
        else
        {
            target = requested * Math.Clamp(fraction, 0, 1);
        }

        return RoiGroupBounds.ConstrainTranslation(_groupBoundaries.Value, target, previous, allowSlide, imageBounds);
    }

    public double ConstrainRotation(PointD pivot, double degrees)
        => RotationLimits(pivot).Clamp(degrees);

    public RotationRange RotationLimits(PointD pivot)
    {
        _ = _groupBoundaries.Value;
        if (!pivot.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(pivot));
        }

        var cached = Volatile.Read(ref _rotationConstraint);
        if (cached?.Pivot != pivot)
        {
            cached = new(pivot, new(() => ComputeRotationLimits(pivot)));
            Interlocked.Exchange(ref _rotationConstraint, cached);
        }

        return cached.Limits.Value;
    }

    private RotationRange ComputeRotationLimits(PointD pivot)
    {
        var minimum = double.NegativeInfinity;
        var maximum = double.PositiveInfinity;
        if (Before.Image is { } image)
        {
            var ordinary = Moving.RotationLimits(pivot, image);
            minimum = ordinary.MinimumDegrees;
            maximum = ordinary.MaximumDegrees;
        }

        foreach (var (parent, child) in _groupBoundaries.Value)
        {
            var negative = RoiMotionBounds.RotationLimit(parent, child, pivot, -360);
            var positive = RoiMotionBounds.RotationLimit(parent, child, pivot, 360);
            var negativeLimit = negative <= -360 ? double.NegativeInfinity : negative;
            var positiveLimit = positive >= 360 ? double.PositiveInfinity : positive;
            minimum = Math.Max(minimum, negativeLimit);
            maximum = Math.Min(maximum, positiveLimit);
        }

        return new RotationRange(minimum, maximum);
    }

    public DocumentState Apply(RigidTransform2D transform, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ApplyCore(RoiGroupBounds.Constrain(_groupBoundaries.Value, transform), token);
    }

    internal DocumentState ApplyPreview(RigidTransform2D transform, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // 미리보기는 각 포인터 구간을 이미 검사했다. 시작점의 직선으로 다시 제한하면 우회 이동이 되튄다.
        if (_groupBoundaries.Value.Any(pair => !pair.Parent.Covers(TransformGeometry(pair.Child, transform))))
        {
            throw new InvalidOperationException("ROI 미리보기가 상위 그룹 경계를 벗어났습니다.");
        }

        return ApplyCore(transform, token);
    }

    private DocumentState ApplyCore(RigidTransform2D transform, CancellationToken token)
    {
        if (transform.IsIdentity || Affected.Count == 0)
        {
            return Before;
        }

        var rois = Before.Rois.ToBuilder();
        foreach (var id in Affected)
        {
            token.ThrowIfCancellationRequested();
            var roi = rois[id];
            rois[id] = roi with
            {
                Geometry = TransformGeometry(roi.Geometry, transform)
            };
        }

        // 큰 단일 그룹은 소수의 고정 조상만 제외한 같은 공간 이웃을 함께 옮긴다. 전체 정렬 대신 경계만 갱신한다.
        var shouldRefit = Affected.Count == Before.Rois.Count
            || (Before.Selection.Count == 1 && Affected.Count > 4096 && Affected.Count > Before.Rois.Count / 2);
        if (shouldRefit)
        {
            return Before.WithRigidRois(rois.ToImmutable(), Affected, token);
        }

        return Before.WithRois(rois.ToImmutable(), Affected);
    }

    internal static RoiGeometry TransformGeometry(RoiGeometry geometry, RigidTransform2D transform)
    {
        if (transform.IsIdentity)
        {
            return geometry;
        }

        return geometry.WithFrame(transform.Apply(geometry.Center), geometry.Width, geometry.Height, geometry.Angle + transform.AngleDegrees);
    }

    private sealed record RotationConstraint(PointD Pivot, Lazy<RotationRange> Limits);
}
