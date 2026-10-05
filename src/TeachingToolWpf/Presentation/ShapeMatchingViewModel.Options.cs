using System.Runtime.CompilerServices;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Presentation;

public sealed partial class ShapeMatchingViewModel
{
    public double MinimumAngle
    {
        get => ActiveShapeState.Training.MinimumAngle;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MinimumAngle = value });
    }

    public double MaximumAngle
    {
        get => ActiveShapeState.Training.MaximumAngle;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MaximumAngle = value });
    }

    public double AngleStep
    {
        get => ActiveShapeState.Training.AngleStep;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { AngleStep = value });
    }

    public bool EnableScale
    {
        get => ActiveShapeState.Training.EnableScale;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { EnableScale = value });
    }

    public double MinimumScale
    {
        get => ActiveShapeState.Training.MinimumScale;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MinimumScale = value });
    }

    public double MaximumScale
    {
        get => ActiveShapeState.Training.MaximumScale;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MaximumScale = value });
    }

    public double ScaleStep
    {
        get => ActiveShapeState.Training.ScaleStep;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { ScaleStep = value });
    }

    public bool IgnorePolarity
    {
        get => ActiveShapeState.Training.IgnorePolarity;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { IgnorePolarity = value });
    }

    public int MaximumFeatures
    {
        get => ActiveShapeState.Training.MaximumFeatures;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MaximumFeatures = value });
    }

    public float MinimumGradient
    {
        get => ActiveShapeState.Training.MinimumGradient;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MinimumGradient = value });
    }

    public int MaximumTemplates
    {
        get => ActiveShapeState.Training.MaximumTemplates;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MaximumTemplates = value });
    }

    public double MinimumFeatureContrastRatio
    {
        get => ActiveShapeState.Training.MinimumFeatureContrastRatio;
        set => ChangeTrainingSettings(ActiveShapeState.Training with { MinimumFeatureContrastRatio = value });
    }

    public double MinimumScore
    {
        get => ActiveSearchSettings.Options.MinimumScore;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MinimumScore = value });
    }

    public int MaximumMatches
    {
        get => ActiveSearchSettings.Options.MaximumMatches;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MaximumMatches = value });
    }

    public double CandidateThreshold
    {
        get => ActiveSearchSettings.Options.CandidateThreshold;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { CandidateThreshold = value });
    }

    public int MaximumCandidates
    {
        get => ActiveSearchSettings.Options.MaximumCandidates;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MaximumCandidates = value });
    }

    public int MaximumVerifiedCandidates
    {
        get => ActiveSearchSettings.Options.MaximumVerifiedCandidates;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MaximumVerifiedCandidates = value });
    }

    public int MaximumCoarseVerifiedCandidates
    {
        get => ActiveSearchSettings.Options.MaximumCoarseVerifiedCandidates;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MaximumCoarseVerifiedCandidates = value });
    }

    public double MaximumOverlap
    {
        get => ActiveSearchSettings.Options.MaximumOverlap;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MaximumOverlap = value });
    }

    public bool Refine
    {
        get => ActiveSearchSettings.Options.Refine;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { Refine = value });
    }

    /// <summary>기존 진단 호출의 프리셋 호환용. 새 패널은 실제 수치 예산을 직접 편집한다.</summary>
    public bool UseExpandedCandidateBudget
    {
        get => MaximumCandidates == 4096 && MaximumVerifiedCandidates == 256;
        set
        {
            ChangeSearchOptions(ActiveSearchSettings.Options with
            {
                MaximumCandidates = value ? 4096 : 2048,
                MaximumVerifiedCandidates = value ? 256 : 64
            });
            Notify(nameof(MaximumCandidates));
            Notify(nameof(MaximumVerifiedCandidates));
        }
    }

    private void ChangeTrainingSettings(ShapeTrainingSettings settings, [CallerMemberName] string? property = null)
    {
        if (ActiveShapeState.Training == settings)
        {
            return;
        }
        ActiveShapeState.Training = settings;
        Notify(property);
        Invalidate();
        if (Model is not null)
        {
            ResetResult(IsTrainingDirty ? "학습 설정 변경 · 기존 모델을 보관합니다. 다시 학습한 뒤 검사하세요." :
                "학습 설정 복원 · 저장된 모델로 다시 검사할 수 있습니다.");
        }
        Notify(nameof(IsTrainingDirty));
        Notify(nameof(ModelSummary));
        RefreshCommands();
    }

    private void ChangeSearchOptions(SearchOptions options, [CallerMemberName] string? property = null)
        => ChangeSearchSettings(ActiveSearchSettings with
        {
            Options = options
        }, property);

    private void ChangeSearchSettings(MatchingSearchSettings settings, [CallerMemberName] string? property = null)
    {
        if (ActiveSearchSettings == settings)
        {
            return;
        }
        ActiveSearchSettings = settings;
        Notify(property);
        Notify(nameof(SpeedProfile));
        if (property is nameof(MaximumCandidates) or nameof(MaximumVerifiedCandidates))
        {
            Notify(nameof(UseExpandedCandidateBudget));
        }
        Invalidate();
        RefreshCommands();
    }

    private SearchOptions CreateTemplateSearchOptions() => new()
    {
        MinimumScore = MinimumScore,
        MaximumMatches = MaximumMatches,
        MaximumCandidates = MaximumCandidates,
        MaximumOverlap = MaximumOverlap,
        EvaluateGeometry = false
    };
}
