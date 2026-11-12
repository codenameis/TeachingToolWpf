using System;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// Configurable thresholds and segmentation parameters for <see cref="TaughtFontOcvVerifier"/>.
/// All tunable values are exposed with named constant defaults and validated upon construction.
/// </summary>
public sealed record OcvVerificationOptions
{
    public const float DefaultPassMatchScore = 0.72f;
    public const float DefaultReviewMatchScore = 0.56f;
    public const float DefaultMinContrastGv = 22.0f;
    public const float DefaultReviewContrastGv = 34.0f;
    public const float DefaultMaxMissingStrokeRatio = 0.32f;
    public const float DefaultReviewMissingStrokeRatio = 0.18f;
    public const float DefaultMaxBleedRatio = 0.30f;
    public const float DefaultReviewBleedRatio = 0.18f;
    public const float DefaultMinConfusionMargin = 0.01f;
    public const PrintMethodType DefaultPrintMethod = PrintMethodType.ContinuousLaser;
    public const int DefaultMinCharWidth = 3;
    public const int DefaultMaxCharWidth = 64;
    public const int DefaultExpectedPitch = 16;
    public const int DefaultDotClosingRadius = 0;
    public const float DefaultForegroundContrastOffset = 14.0f;
    public const bool DefaultDarkOnLight = true;
    public const int DefaultMaxLineCharacters = 64;
    public const int UpperBoundMaxLineCharacters = 255;
    public const int DefaultRectifiedWidth = 0;
    public const int DefaultRectifiedHeight = 0;

    public OcvVerificationOptions(
        float PassMatchScore = DefaultPassMatchScore,
        float ReviewMatchScore = DefaultReviewMatchScore,
        float MinContrastGv = DefaultMinContrastGv,
        float ReviewContrastGv = DefaultReviewContrastGv,
        float MaxMissingStrokeRatio = DefaultMaxMissingStrokeRatio,
        float ReviewMissingStrokeRatio = DefaultReviewMissingStrokeRatio,
        float MaxBleedRatio = DefaultMaxBleedRatio,
        float ReviewBleedRatio = DefaultReviewBleedRatio,
        float MinConfusionMargin = DefaultMinConfusionMargin,
        PrintMethodType PrintMethod = DefaultPrintMethod,
        int MinCharWidth = DefaultMinCharWidth,
        int MaxCharWidth = DefaultMaxCharWidth,
        int ExpectedPitch = DefaultExpectedPitch,
        int DotClosingRadiusX = DefaultDotClosingRadius,
        int DotClosingRadiusY = DefaultDotClosingRadius,
        float ForegroundContrastOffset = DefaultForegroundContrastOffset,
        bool DarkOnLight = DefaultDarkOnLight,
        int MaxLineCharacters = DefaultMaxLineCharacters,
        int RectifiedWidth = DefaultRectifiedWidth,
        int RectifiedHeight = DefaultRectifiedHeight)
    {
        this.PassMatchScore = PassMatchScore;
        this.ReviewMatchScore = ReviewMatchScore;
        this.MinContrastGv = MinContrastGv;
        this.ReviewContrastGv = ReviewContrastGv;
        this.MaxMissingStrokeRatio = MaxMissingStrokeRatio;
        this.ReviewMissingStrokeRatio = ReviewMissingStrokeRatio;
        this.MaxBleedRatio = MaxBleedRatio;
        this.ReviewBleedRatio = ReviewBleedRatio;
        this.MinConfusionMargin = MinConfusionMargin;
        this.PrintMethod = PrintMethod;
        this.MinCharWidth = MinCharWidth;
        this.MaxCharWidth = MaxCharWidth;
        this.ExpectedPitch = ExpectedPitch;
        this.DotClosingRadiusX = DotClosingRadiusX;
        this.DotClosingRadiusY = DotClosingRadiusY;
        this.ForegroundContrastOffset = ForegroundContrastOffset;
        this.DarkOnLight = DarkOnLight;
        this.MaxLineCharacters = MaxLineCharacters;
        this.RectifiedWidth = RectifiedWidth;
        this.RectifiedHeight = RectifiedHeight;

        EnsureValid();
    }

    public float PassMatchScore { get; init; }
    public float ReviewMatchScore { get; init; }
    public float MinContrastGv { get; init; }
    public float ReviewContrastGv { get; init; }
    public float MaxMissingStrokeRatio { get; init; }
    public float ReviewMissingStrokeRatio { get; init; }
    public float MaxBleedRatio { get; init; }
    public float ReviewBleedRatio { get; init; }
    public float MinConfusionMargin { get; init; }
    public PrintMethodType PrintMethod { get; init; }
    public int MinCharWidth { get; init; }
    public int MaxCharWidth { get; init; }
    public int ExpectedPitch { get; init; }
    public int DotClosingRadiusX { get; init; }
    public int DotClosingRadiusY { get; init; }
    public float ForegroundContrastOffset { get; init; }
    public bool DarkOnLight { get; init; }
    public int MaxLineCharacters { get; init; }
    public int RectifiedWidth { get; init; }
    public int RectifiedHeight { get; init; }

    public void EnsureValid()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PassMatchScore, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PassMatchScore, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReviewMatchScore, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReviewMatchScore, 1f);
        if (PassMatchScore < ReviewMatchScore)
        {
            throw new ArgumentException(
                $"PassMatchScore ({PassMatchScore:F3}) must be greater than or equal to ReviewMatchScore ({ReviewMatchScore:F3}).",
                nameof(PassMatchScore));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MinContrastGv, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReviewContrastGv, MinContrastGv);

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMissingStrokeRatio, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMissingStrokeRatio, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReviewMissingStrokeRatio, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReviewMissingStrokeRatio, 1f);
        if (MaxMissingStrokeRatio < ReviewMissingStrokeRatio)
        {
            throw new ArgumentException(
                $"MaxMissingStrokeRatio ({MaxMissingStrokeRatio:F3}) must be greater than or equal to ReviewMissingStrokeRatio ({ReviewMissingStrokeRatio:F3}).",
                nameof(MaxMissingStrokeRatio));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBleedRatio, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBleedRatio, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReviewBleedRatio, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReviewBleedRatio, 1f);
        if (MaxBleedRatio < ReviewBleedRatio)
        {
            throw new ArgumentException(
                $"MaxBleedRatio ({MaxBleedRatio:F3}) must be greater than or equal to ReviewBleedRatio ({ReviewBleedRatio:F3}).",
                nameof(MaxBleedRatio));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MinConfusionMargin, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinConfusionMargin, 1f);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinCharWidth, 1);
        if (MaxCharWidth < MinCharWidth)
        {
            throw new ArgumentException(
                $"MaxCharWidth ({MaxCharWidth}) must be greater than or equal to MinCharWidth ({MinCharWidth}).",
                nameof(MaxCharWidth));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ExpectedPitch, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(DotClosingRadiusX, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(DotClosingRadiusY, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ForegroundContrastOffset, 0f);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLineCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxLineCharacters, UpperBoundMaxLineCharacters);
        ArgumentOutOfRangeException.ThrowIfLessThan(RectifiedWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(RectifiedHeight, 0);
    }
}
