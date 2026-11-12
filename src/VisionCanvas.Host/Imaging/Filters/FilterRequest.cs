using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Imaging.Filters;

public sealed record FilterRequest(ImageReference Image, bool IsLive, IReadOnlyList<Roi> Rois, RoiTransformPreview? Transform = null)
{
    public FilterSettings? OverrideSettings
    {
        get; init;
    }

    public ImmutableArray<FilterSettings>? OverridePipeline
    {
        get; init;
    }

    public FilterSettings Settings(Roi roi) => OverrideSettings ?? roi.Filter;

    public ImmutableArray<FilterSettings> Pipeline(Roi roi)
    {
        if (OverridePipeline is { IsDefault: false } explicitPipeline)
        {
            var active = explicitPipeline.Where(static s => s.IsEnabled && s.Kind != RoiFilterKind.None).ToImmutableArray();
            if (!active.IsEmpty)
            {
                return active;
            }
        }

        if (OverrideSettings is { } single)
        {
            return single.IsEnabled && single.Kind != RoiFilterKind.None ? [single] : [];
        }

        return roi.EffectiveFilterPipeline
            .Where(static s => s.IsEnabled && s.Kind != RoiFilterKind.None)
            .ToImmutableArray();
    }

    // Evaluated by the single filter worker after debounce, never materialized per pointer sample.
    public RoiGeometry Geometry(Roi roi) => Transform?.Geometry(roi) ?? roi.Geometry;
}
