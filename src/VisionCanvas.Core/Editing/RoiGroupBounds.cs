using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>부모가 있는 모든 ROI의 공간 포함 규칙.</summary>
internal static class RoiGroupBounds
{
    public static void Validate(DocumentState state, IEnumerable<Guid> changedIds, DocumentState? before = null)
    {
        if (!Contains(state, changedIds, before))
        {
            throw new InvalidOperationException("ROI는 상위 그룹 도형 안에 있어야 합니다.");
        }
    }

    public static bool Contains(DocumentState state, IEnumerable<Guid> changedIds, DocumentState? before = null)
    {
        if (state.Snapshot.Children.IsEmpty)
        {
            return true;
        }

        foreach (var id in changedIds)
        {
            var group = state.Rois[id];
            if (group.ParentId is { } parent && !Covered(parent, group))
            {
                return false;
            }
            // 부모의 크기만 줄이면 자식의 형상은 ChangedIds에 포함되지 않는다.
            foreach (var child in state.Snapshot.Children.GetValueOrDefault(id, []))
            {
                if (!Covered(id, state.Rois[child]))
                {
                    return false;
                }
            }
        }
        return true;

        bool Covered(Guid parent, Roi child)
        {
            if (state.Rois[parent].Geometry.Covers(child.Geometry))
            {
                return true;
            }
            if (state.IsGroupRoi(child.Id) || before is null || !before.Rois.TryGetValue(child.Id, out var previous))
            {
                return false;
            }
            // 이전 문서의 밖에 있던 리프는 보존한다. 안에서 확정한 뒤에는 다시 나갈 수 없다.
            return previous.ParentId == parent && !before.Rois[parent].Geometry.Covers(previous.Geometry);
        }
    }

    public static (RoiGeometry Parent, RoiGeometry Child)[] StationaryParents(DocumentState state, IReadOnlySet<Guid> moving)
    {
        if (moving.Count == 0 || moving.Count == state.Rois.Count || state.Snapshot.Children.IsEmpty)
        {
            return [];
        }
        return state.Selection
            .Select(id => state.Rois[id])
            .Where(roi => roi.ParentId is { } parent && !moving.Contains(parent))
            .Where(roi => state.IsGroupRoi(roi.Id) || state.Rois[roi.ParentId!.Value].Geometry.Covers(roi.Geometry))
            .Select(roi => (state.Rois[roi.ParentId!.Value].Geometry, roi.Geometry)).ToArray();
    }

    public static RigidTransform2D Constrain((RoiGeometry Parent, RoiGeometry Child)[] boundaries, RigidTransform2D requested)
    {
        if (boundaries.Length == 0 || requested.IsIdentity)
        {
            return requested;
        }
        if (requested.AngleDegrees == 0)
        {
            var fraction = boundaries.Min(pair => RoiMotionBounds.TranslationFraction(pair.Parent, pair.Child, requested.Translation));
            return new(requested.Pivot, 0, requested.Translation * fraction);
        }
        var angle = boundaries.Select(pair => RoiMotionBounds.RotationLimit(pair.Parent, pair.Child, requested.Pivot, requested.AngleDegrees))
            .MinBy(Math.Abs);
        var translationFraction = requested.Translation == default ? 1 : boundaries.Min(pair =>
            RoiMotionBounds.TranslationFraction(pair.Parent, pair.Child.RotateAround(requested.Pivot, angle), requested.Translation));
        return new(requested.Pivot, angle, requested.Translation * translationFraction);
    }

    public static PointD ConstrainTranslation((RoiGeometry Parent, RoiGeometry Child)[] boundaries, PointD requested, PointD previous, bool allowSlide,
        BoundsD? translationBounds = null)
    {
        if (boundaries.Length == 0 || requested == previous)
        {
            return requested;
        }
        var movement = requested - previous;
        var fraction = 1d;
        for (var i = 0; i < boundaries.Length; i++)
        {
            var pair = boundaries[i];
            var movedChild = previous == default ? pair.Child : pair.Child.Translate(previous);
            var current = RoiMotionBounds.TranslationFraction(pair.Parent, movedChild, movement);
            if (current < fraction)
            {
                fraction = current;
            }
        }

        var contact = previous + (movement * fraction);
        if (!allowSlide || fraction == 1)
        {
            return contact;
        }

        var remaining = requested - contact;
        var best = contact;
        var distance = remaining.Length;
        var slides = new List<PointD>(8);
        foreach (var (parent, child) in boundaries)
        {
            slides.Clear();
            RoiMotionBounds.CollectSlidingTranslations(parent, child.Translate(contact), remaining, slides);
            for (var i = 0; i < slides.Count; i++)
            {
                var slide = slides[i];
                var allowed = ClampSlideFraction(boundaries, contact, slide, translationBounds);
                var candidate = contact + (slide * allowed);
                var error = (requested - candidate).Length;
                if (error < distance)
                {
                    best = candidate;
                    distance = error;
                }
            }
        }
        return best;
    }

    private static double ClampSlideFraction((RoiGeometry Parent, RoiGeometry Child)[] boundaries, PointD contact, PointD slide, BoundsD? translationBounds)
    {
        var allowed = 1d;
        for (var i = 0; i < boundaries.Length; i++)
        {
            var pair = boundaries[i];
            var current = RoiMotionBounds.TranslationFraction(pair.Parent, pair.Child.Translate(contact), slide);
            if (current < allowed)
            {
                allowed = current;
            }
        }
        if (translationBounds is { } image)
        {
            allowed = ClampToImageBounds(contact, slide, image, allowed);
        }
        return Math.Clamp(allowed, 0, 1);
    }

    private static double ClampToImageBounds(PointD contact, PointD slide, BoundsD image, double allowed)
    {
        if (slide.X > 0)
        {
            allowed = Math.Min(allowed, (image.Right - contact.X) / slide.X);
        }
        if (slide.X < 0)
        {
            allowed = Math.Min(allowed, (image.Left - contact.X) / slide.X);
        }
        if (slide.Y > 0)
        {
            allowed = Math.Min(allowed, (image.Bottom - contact.Y) / slide.Y);
        }
        if (slide.Y < 0)
        {
            allowed = Math.Min(allowed, (image.Top - contact.Y) / slide.Y);
        }
        return allowed;
    }
}
