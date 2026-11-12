using VisionGeometry;

namespace VisionCanvas.Core.Editing;

public sealed record RoiTransformPreview(RoiTransformSelection Selection, RigidTransform2D Transform)
{
    public RoiGeometry Geometry(Roi roi)
        => Selection.Affected.Contains(roi.Id)
            ? RoiTransformSelection.TransformGeometry(roi.Geometry, Transform)
            : roi.Geometry;
}
