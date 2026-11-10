namespace VisionAlgorithm.OpenSrc.PhotometricStereo;

/// <summary>
/// Result of 4-directional Lambertian Photometric Stereo surface normal reconstruction ($\mathbf{n} = (n_x, n_y, n_z)$),
/// albedo estimation $\rho(x,y)$, Mean Curvature $H = \frac{1}{2}(\partial n_x/\partial x + \partial n_y/\partial y)$,
/// Gaussian Curvature $K$, and micro-dent/scratch surface defect segmentation.
/// </summary>
public readonly record struct PhotometricStereoResult(
    int Width,
    int Height,
    double MeanAlbedo,
    double MaxAbsMeanCurvature,
    double MaxAbsGaussianCurvature,
    int DefectPixelCount,
    int DefectRegionCount,
    double PeakDefectX,
    double PeakDefectY,
    double PeakDefectCurvature,
    byte[] AlbedoMap,
    byte[] CurvatureMap);
