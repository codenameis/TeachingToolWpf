using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Models;

/// <summary>서비스의 자동 피라미드 보정과 구별하여 사용자가 학습한 조건을 보관한다.</summary>
internal sealed record ShapeTrainingSettings
{
    public double MinimumAngle { get; init; } = -180;
    public double MaximumAngle { get; init; } = 180;
    public double AngleStep { get; init; } = 10;
    public bool EnableScale
    {
        get; init;
    }
    public double MinimumScale { get; init; } = .9;
    public double MaximumScale { get; init; } = 1.1;
    public double ScaleStep { get; init; } = .1;
    public bool IgnorePolarity
    {
        get; init;
    }
    public int MaximumFeatures { get; init; } = 160;
    public float MinimumGradient { get; init; } = 8;
    public double MinimumFeatureContrastRatio { get; init; } = .2;
    public int MaximumTemplates { get; init; } = 2048;
    public bool InspectRotation
    {
        get; init;
    }

    public TrainingOptions ToOptions(bool buildGeometricModel) => new()
    {
        MinimumAngleDegrees = InspectRotation && !buildGeometricModel ? -180 : MinimumAngle,
        MaximumAngleDegrees = InspectRotation && !buildGeometricModel ? 180 : MaximumAngle,
        AngleStepDegrees = AngleStep,
        MinimumScale = EnableScale ? MinimumScale : 1,
        MaximumScale = EnableScale ? MaximumScale : 1,
        ScaleStep = ScaleStep,
        Polarity = IgnorePolarity ? ContrastPolarity.IgnoreLocal : ContrastPolarity.Preserve,
        MaximumFeatures = MaximumFeatures,
        MinimumGradient = MinimumGradient,
        MinimumFeatureContrastRatio = MinimumFeatureContrastRatio,
        MaximumTemplates = MaximumTemplates,
        BuildGeometricModel = buildGeometricModel,
        BuildClutterModel = true
    };
}

internal sealed record MatchingSearchSettings
{
    public RotationAcceptance Rotation { get; init; } = new();
    public double SearchTimeoutSeconds
    {
        get; init;
    }
    public SearchOptions Options { get; init; } = new();
    public bool UseSelection
    {
        get; init;
    }
    public bool LimitFitError
    {
        get; init;
    }
    public double MaximumFitErrorPixels { get; init; } = 3;
    public bool LimitClutter
    {
        get; init;
    }
    public double MaximumClutterRatio { get; init; } = .3;
}

internal sealed class ShapeMatchingModeState
{
    public Guid? TeachingRoiId
    {
        get; set;
    }
    public ImmutableArray<MaskRegion> TeachingMasks { get; set; } = [];
    public bool IsMaskDirty
    {
        get; set;
    }
    public ShapeModel? Model
    {
        get; set;
    }
    public RoiGeometry? TeachingRegion
    {
        get; set;
    }
    public ShapeTrainingSettings Training { get; set; } = new();
    public ShapeTrainingSettings? TrainedSettings
    {
        get; set;
    }
    public MatchingSearchSettings Search
    {
        get; set;
    } = new()
    {
        Options = new()
        {
            MaximumMatches = 256,
            MaximumParallelism = Math.Clamp(Environment.ProcessorCount, 1, 16),
            ClutterWeight = 1
        }
    };
}

internal sealed class TemplateMatchingModeState
{
    public Guid? TeachingRoiId
    {
        get; set;
    }
    public ImmutableArray<MaskRegion> TeachingMasks { get; set; } = [];
    public bool IsMaskDirty
    {
        get; set;
    }
    public CorrelationTemplate? Model
    {
        get; set;
    }
    public RoiGeometry? TeachingRegion
    {
        get; set;
    }
    public MatchingSearchSettings Search
    {
        get; set;
    } = new()
    {
        Options = new()
        {
            MaximumMatches = 256
        }
    };
}
