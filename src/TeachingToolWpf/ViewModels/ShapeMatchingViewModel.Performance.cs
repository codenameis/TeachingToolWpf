using TeachingToolWpf.Models;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.ViewModels;

public sealed partial class ShapeMatchingViewModel
{
    public IReadOnlyList<InspectionComputeOption> ComputeModes
    {
        get;
    } =
    [
        new(InspectionComputeMode.Cpu, "CPU"),
        new(InspectionComputeMode.GpuPreprocessing, "CPU+CUDA · GPU 후보 계산 + CPU 정밀 검사")
    ];

    public InspectionComputeMode ComputeMode
    {
        get => ActiveSearchSettings.Options.ComputeMode;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { ComputeMode = value });
    }

    public double ClutterWeight
    {
        get => ActiveSearchSettings.Options.ClutterWeight;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { ClutterWeight = value });
    }

    public IReadOnlyList<InspectionSpeedOption> SpeedProfiles
    {
        get;
    } =
    [
        new(InspectionSpeedProfile.Balanced, "기본 후보 예산"),
        new(InspectionSpeedProfile.Precise, "확대 후보 예산"),
        new(InspectionSpeedProfile.Custom, "사용자 설정")
    ];

    public InspectionSpeedProfile SpeedProfile
    {
        get
        {
            foreach (var profile in new[] { InspectionSpeedProfile.Balanced, InspectionSpeedProfile.Precise })
            {
                if (ApplyProfile(ActiveSearchSettings.Options, profile) == ActiveSearchSettings.Options)
                {
                    return profile;
                }
            }
            return InspectionSpeedProfile.Custom;
        }
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (value != InspectionSpeedProfile.Custom)
            {
                ChangeSearchOptions(ApplyProfile(ActiveSearchSettings.Options, value));
                Notify(nameof(MaximumCandidates));
                Notify(nameof(MaximumVerifiedCandidates));
                Notify(nameof(MaximumCoarseVerifiedCandidates));
                Notify(nameof(Refine));
            }
            Notify(nameof(SpeedProfile));
        }
    }

    public int MaximumParallelism
    {
        get => ActiveSearchSettings.Options.MaximumParallelism;
        set => ChangeSearchOptions(ActiveSearchSettings.Options with { MaximumParallelism = value });
    }

    public double SearchTimeoutSeconds
    {
        get => ActiveSearchSettings.SearchTimeoutSeconds;
        set => ChangeSearchSettings(ActiveSearchSettings with { SearchTimeoutSeconds = value });
    }

    private SearchOptions ApplyProfile(SearchOptions options, InspectionSpeedProfile profile)
    {
        var (candidates, verified, coarse, refine) = profile switch
        {
            InspectionSpeedProfile.Fast or InspectionSpeedProfile.Balanced => (2048, 64, 256, true),
            InspectionSpeedProfile.Precise => (8192, 256, 1024, true),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        return IsShapeMethod ? options with
        {
            MaximumCandidates = candidates,
            MaximumVerifiedCandidates = verified,
            MaximumCoarseVerifiedCandidates = coarse,
            Refine = refine
        } : options with
        {
            MaximumCandidates = candidates
        };
    }
}
