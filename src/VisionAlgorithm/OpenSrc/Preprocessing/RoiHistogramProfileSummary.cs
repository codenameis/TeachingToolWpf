namespace VisionAlgorithm.OpenSrc.Preprocessing;

/// <summary>
/// Summary of an ROI's 256-bin grayscale histogram, Otsu optimal threshold, first/second-order moments,
/// and 1D horizontal/vertical center-line intensity and gradient profiles.
/// </summary>
public readonly record struct RoiHistogramProfileSummary(
    int Width,
    int Height,
    byte MinIntensity,
    byte MaxIntensity,
    double MeanIntensity,
    double StdDevIntensity,
    byte OtsuThreshold,
    double OtsuBetweenClassVariance,
    double HorizontalCenterMean,
    double HorizontalPeakGradient,
    double VerticalCenterMean,
    double VerticalPeakGradient,
    int[] HistogramBins);
