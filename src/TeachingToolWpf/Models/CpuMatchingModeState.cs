using TeachingToolWpf.Imaging.Matching;

namespace TeachingToolWpf.Models;

internal sealed class CpuMatchingModeState
{
    public CpuTemplate? Template
    {
        get; set;
    }
    public Guid? TeachingRoiId
    {
        get; set;
    }
    public bool IsMaskDirty
    {
        get; set;
    }
    public CpuMatching.Core.SearchOptions Options { get; set; } = new() { MinimumAngle = -30, MaximumAngle = 30 };
    public MatchingSearchSettings Search
    {
        get; set;
    } = new()
    {
        UseSelection = true,
        Options = new()
        {
            MinimumScore = .75,
            MaximumMatches = 256,
            MaximumCandidates = 2048
        }
    };
}
