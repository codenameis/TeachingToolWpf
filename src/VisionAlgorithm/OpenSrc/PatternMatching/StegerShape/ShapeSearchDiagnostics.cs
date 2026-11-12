using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record ShapeSearchDiagnostics(double PreprocessingMilliseconds, double CandidateMilliseconds,
    double VerificationMilliseconds, double RefinementMilliseconds, double SuppressionMilliseconds,
    int TemplatesEvaluated, int Candidates, bool CandidateBudgetExceeded, bool VerificationBudgetExceeded,
    long EstimatedWorkspaceBytes, string SearchPolicy, bool MatchLimitExceeded = false)
{
    public double CandidateGridMilliseconds
    {
        get; init;
    }
    public double CandidatePoseMilliseconds
    {
        get; init;
    }
    public int CandidatesBeforeRefinement
    {
        get; init;
    }
}
