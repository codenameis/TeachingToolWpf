namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal sealed record CandidateBatch(List<PoseCandidate> Candidates, bool BudgetExceeded, int EvaluatedTemplates)
{
    public double GridMilliseconds { get; init; }
    public double PoseMilliseconds { get; init; }
    public int CandidatesBeforeRefinement { get; init; }
}
