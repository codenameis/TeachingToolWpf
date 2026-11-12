using System.Diagnostics;
using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

public interface ICpuTemplateMatchingService
{
    Task<CpuTemplate> TrainAsync(ImageReference image, bool live, RoiGeometry roi, CpuTemplateMethod method, CancellationToken token, RoiExclusion? exclusion = null, CpuTemplateParameters? parameters = null);
    Task<CpuSearchOutput> SearchAsync(ImageReference image, bool live, CpuTemplate template, RoiGeometry? region, CpuSearchOptions options, CancellationToken token, RoiExclusion? exclusion = null, bool useCuda = false);
    Task<CpuTemplate?> PreviewAsync(ImageReference image, bool live, RoiGeometry roi, CpuTemplateMethod method, CancellationToken token, RoiExclusion? exclusion = null, CpuTemplateParameters? parameters = null)
        => Task.FromResult<CpuTemplate?>(null);
}
