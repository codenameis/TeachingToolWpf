using System.Numerics;
using VisionAlgorithm.OpenSrc.Common;

namespace VisionAlgorithm.OpenSrc.AutoTeaching;

/// <summary>
/// Summary of a part teaching operation in <see cref="PartModelPool.RegisterOrMergeOnTeach"/>.
/// </summary>
public readonly record struct PartTeachRegistrationResult(
    PartModelId AssignedId,
    bool WasMergedIntoExisting,
    float MatchedSimilarity,
    int TotalPoolCount);
