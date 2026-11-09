// File: TeachingReadiness/TeachingReadinessSpec.cs
using System;
using TeachingToolWpf.Fluent2.Common;

namespace TeachingToolWpf.Fluent2.TeachingReadiness;

public readonly record struct TeachingReadinessSpec
{
    public const float DefaultMinimumContrastSpan = 24.0f; // [Domain Why]: Minimum dynamic range (in 8-bit gray levels) required to separate edges from sensor read noise.
    public const float DefaultTargetContrastSpan = 120.0f; // [Domain Why]: Full-scale industrial contrast target for high-SNR template lock.
    public const float DefaultMinimumRmsGradient = 18.0f; // [Domain Why]: Minimum Sobel RMS energy required for sub-pixel parabolic/Gaussian edge localization.
    public const float DefaultTargetRmsGradient = 96.0f; // [Domain Why]: Saturation target for sharp step edges under 3x3 Sobel operator.
    public const float DefaultMinimumIsotropy = 0.08f; // [Math Why]: Structure tensor eigenvalue ratio lambda_min / lambda_max below 0.08 indicates 1D aperture ambiguity.
    public const float DefaultTargetIsotropy = 0.35f; // [Math Why]: Eigenvalue ratio >= 0.35 provides well-conditioned 2D translation + rotation locking.
    public const float DefaultOptimalScoreThreshold = 0.72f; // [Domain Why]: Composite score threshold for OptimalSubPixel teaching readiness.

    public RoiPixelBounds Roi { get; }

    public float MinimumContrastSpan { get; }

    public float TargetContrastSpan { get; }

    public float MinimumRmsGradient { get; }

    public float TargetRmsGradient { get; }

    public float MinimumIsotropy { get; }

    public float TargetIsotropy { get; }

    public float OptimalScoreThreshold { get; }

    public bool IsInitialized { get; }

    private TeachingReadinessSpec(
        RoiPixelBounds roi,
        float minimumContrastSpan,
        float targetContrastSpan,
        float minimumRmsGradient,
        float targetRmsGradient,
        float minimumIsotropy,
        float targetIsotropy,
        float optimalScoreThreshold)
    {
        Roi = roi;
        MinimumContrastSpan = minimumContrastSpan;
        TargetContrastSpan = targetContrastSpan;
        MinimumRmsGradient = minimumRmsGradient;
        TargetRmsGradient = targetRmsGradient;
        MinimumIsotropy = minimumIsotropy;
        TargetIsotropy = targetIsotropy;
        OptimalScoreThreshold = optimalScoreThreshold;
        IsInitialized = true;
    }

    public static TeachingReadinessSpec Create(
        RoiPixelBounds roi,
        float minimumContrastSpan = DefaultMinimumContrastSpan,
        float targetContrastSpan = DefaultTargetContrastSpan,
        float minimumRmsGradient = DefaultMinimumRmsGradient,
        float targetRmsGradient = DefaultTargetRmsGradient,
        float minimumIsotropy = DefaultMinimumIsotropy,
        float targetIsotropy = DefaultTargetIsotropy,
        float optimalScoreThreshold = DefaultOptimalScoreThreshold)
    {
        if (!roi.IsInitialized)
        {
            throw new ArgumentException("ROI bounds must be initialized via RoiPixelBounds.Create.", nameof(roi));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumContrastSpan);
        if (targetContrastSpan < minimumContrastSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(targetContrastSpan), "TargetContrastSpan must be >= MinimumContrastSpan.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumRmsGradient);
        if (targetRmsGradient < minimumRmsGradient)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRmsGradient), "TargetRmsGradient must be >= MinimumRmsGradient.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(minimumIsotropy);
        if ((targetIsotropy <= 0.0f) || (targetIsotropy < minimumIsotropy) || (targetIsotropy > 1.0f))
        {
            throw new ArgumentOutOfRangeException(nameof(targetIsotropy), "TargetIsotropy must be within (0, 1] and >= MinimumIsotropy.");
        }

        if ((optimalScoreThreshold <= 0.0f) || (optimalScoreThreshold > 1.0f))
        {
            throw new ArgumentOutOfRangeException(nameof(optimalScoreThreshold), "OptimalScoreThreshold must be in (0, 1].");
        }

        return new TeachingReadinessSpec(
            roi,
            minimumContrastSpan,
            targetContrastSpan,
            minimumRmsGradient,
            targetRmsGradient,
            minimumIsotropy,
            targetIsotropy,
            optimalScoreThreshold);
    }

    public bool ValidateAgainstImage(int imageWidth, int imageHeight)
    {
        return IsInitialized && Roi.IsWithin(imageWidth, imageHeight);
    }
}
