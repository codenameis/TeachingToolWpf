namespace CpuMatching.Core;

public sealed record SearchResult(IReadOnlyList<Match> Matches, bool CandidatesTruncated, bool MatchLimitExceeded, long Evaluations)
{
    public long CandidateCount
    {
        get; init;
    }
    public int CandidateBatches
    {
        get; init;
    }

    public SearchTimings? Timings
    {
        get; init;
    }
}
