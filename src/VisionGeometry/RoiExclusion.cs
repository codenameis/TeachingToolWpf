using System.Collections.Immutable;

namespace VisionGeometry;

/// <summary>검사에 전달하는 불변 기하 값. 편집 문서나 렌더 리소스를 참조하지 않는다.</summary>
public sealed record RoiExclusion(RoiGeometry Owner, ImmutableArray<MaskRegion> Regions)
{
    public bool Contains(PointD world)
    {
        var local = Owner.ToLocal(world);
        if (!Owner.Shape.ContainsLocal(local, Owner.Width, Owner.Height, 0))
        {
            return false;
        }
        var normalized = new PointD((local.X / Owner.Width) + .5, (local.Y / Owner.Height) + .5);
        foreach (var region in Regions)
        {
            if (region.ContainsNormalized(normalized))
            {
                return true;
            }
        }
        return false;
    }
}
