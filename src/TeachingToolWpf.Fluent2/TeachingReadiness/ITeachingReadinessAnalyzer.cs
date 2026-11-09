// File: TeachingReadiness/ITeachingReadinessAnalyzer.cs
using TeachingToolWpf.Fluent2.Common;

namespace TeachingToolWpf.Fluent2.TeachingReadiness;

public interface ITeachingReadinessAnalyzer
{
    TeachingResult<TeachingReadinessReport> Analyze(GrayImageView2D image, in TeachingReadinessSpec spec);
}
