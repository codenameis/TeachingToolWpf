namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Selects the geometric transformation model used by <see cref="ImageWarpingEngine"/>.
/// </summary>
public enum WarpTransformKind
{
    Affine = 0,
    PerspectiveKeystone = 1,
    PolarAnnularUnwrap = 2,
    BrownConradyUndistort = 3
}
