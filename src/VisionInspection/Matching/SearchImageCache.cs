using System.Collections.Immutable;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Thread-safe single-entry search crop cache keyed by <see cref="ImageReference"/>, active pre-inspection filter steps, and crop window.
/// </summary>
internal sealed class SearchImageCache
{
    private readonly object _gate = new();
    private ImageReference? _image;
    private ImmutableArray<PreInspectionFilterStep> _filterSteps = [];
    private MatchingCropWindow _window;
    private VisionAlgorithm.ShapeGrayImage? _grayImage;

    public bool TryGet(
        ImageReference image,
        ImmutableArray<PreInspectionFilterStep> activeFilterSteps,
        MatchingCropWindow window,
        out VisionAlgorithm.ShapeGrayImage grayImage)
    {
        lock (_gate)
        {
            if (_image == image &&
                _filterSteps.AsSpan().SequenceEqual(activeFilterSteps.AsSpan()) &&
                _window == window &&
                _grayImage is { } existing)
            {
                grayImage = existing;
                return true;
            }
        }

        grayImage = default!;
        return false;
    }

    public void Store(
        ImageReference image,
        ImmutableArray<PreInspectionFilterStep> activeFilterSteps,
        MatchingCropWindow window,
        VisionAlgorithm.ShapeGrayImage grayImage)
    {
        lock (_gate)
        {
            _image = image;
            _filterSteps = activeFilterSteps.IsDefault ? [] : activeFilterSteps;
            _window = window;
            _grayImage = grayImage;
        }
    }
}
