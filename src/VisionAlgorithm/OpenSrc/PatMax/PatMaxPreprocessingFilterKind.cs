using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VisionAlgorithm.OpenSrc.PatMax;

/// <summary>
/// Preprocessing filter modes actually applied before PatMax training and runtime search,
/// and rendered live on the ROI canvas as parameters are adjusted.
/// </summary>
public enum PatMaxPreprocessingFilterKind
{
    /// <summary>Direct raw grayscale input without extra pre-smoothing or sharpening.</summary>
    None = 0,

    /// <summary>5x5 Binomial Gaussian scale-space smoothing linked to Fine Granularity and FilterSigma.</summary>
    GaussianScaleSpace = 1,

    /// <summary>3x3 Median impulse-noise suppression followed by optional Gaussian smoothing.</summary>
    MedianDenoise = 2,

    /// <summary>Gaussian unsharp-mask edge contrast enhancement before dipole extraction.</summary>
    EdgeContrastEnhance = 3,

    /// <summary>Visualizes the actual thresholded Gaussian-Sobel gradient magnitude response used by PatMax.</summary>
    SobelEdgeResponsePreview = 4
}
