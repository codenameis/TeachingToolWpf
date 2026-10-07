using VisionCanvas.Core.Editing;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Interaction;

/// <summary>초안은 문서와 분리하고 포인터를 놓을 때 한 번만 이력에 등록한다.</summary>
internal sealed class MaskDrawGesture(InteractionController controller, Roi owner, PointD start) : IGesture
{
    private readonly ViewportTransform _view = controller.Editor.State.Viewport;
    private readonly ImageReference? _image = controller.Editor.State.Image;
    private MaskRegion? _draft;

    public void Move(PointD screen, InputModifiers modifiers)
    {
        var first = _view.ScreenToImage(start);
        if (!owner.Geometry.Contains(first) || (screen - start).Length < 4)
        {
            _draft = null;
            controller.Show(new());
            return;
        }
        var a = Normalize(first);
        var b = Normalize(_view.ScreenToImage(screen));
        var bounds = new BoundsD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        _draft = bounds.Width * owner.Geometry.Width < .01 || bounds.Height * owner.Geometry.Height < .01
            ? null : new(controller.MaskShape, bounds);
        controller.Show(new()
        {
            MaskDraft = _draft
        });
    }

    private PointD Normalize(PointD world)
    {
        var point = owner.Geometry.ToLocal(world);
        return new(Math.Clamp((point.X / owner.Geometry.Width) + .5, 0, 1),
            Math.Clamp((point.Y / owner.Geometry.Height) + .5, 0, 1));
    }

    public void Complete()
    {
        var state = controller.Editor.State;
        if (_draft is not null && ReferenceEquals(_image, state.Image) && state.Selection.Count == 1 &&
            state.Selection.Contains(owner.Id) && state.Rois.TryGetValue(owner.Id, out var current) && current == owner)
        {
            controller.Editor.AddMask(owner.Id, _draft);
            controller.ValidateDraftImage();
        }
    }

    public void Cancel() => controller.Show(new());
}
