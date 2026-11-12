namespace VisionAlgorithm.OpenSrc.PatternMatching;

internal readonly record struct PoseCandidate(
    MatchPose Pose,
    double Score,
    int Template,
    VerifiedScore? Verification = null,
    double? DetailConsistency = null);
