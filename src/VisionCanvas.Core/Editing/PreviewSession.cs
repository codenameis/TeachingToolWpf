using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>드래그 이동·회전·크기 조절의 임시 시작 상태와 변환 미리보기 수명주기를 담당한다.</summary>
internal sealed class PreviewSession
{
    private DocumentState? _start;
    private RoiTransformSelection? _transformSelection;

    public long Epoch { get; private set; }

    public DocumentState? Start => _start;

    public RoiTransformPreview? TransformPreview { get; private set; }

    public bool IsActive => _start is not null;

    public bool HasChanges(DocumentState current)
        => _start is { } before &&
           (TransformPreview is { Transform.IsIdentity: false } || !current.SameContent(before));

    public void Begin(DocumentState start)
    {
        Epoch++;
        _start = start;
    }

    public DocumentState RequireStart()
        => _start ?? throw new InvalidOperationException("편집 preview가 시작되지 않았습니다.");

    public bool TryUpdateTranslation(PointD delta, bool allowSlide)
    {
        var before = RequireStart();
        if (delta == default && _transformSelection is null)
        {
            return false;
        }

        _transformSelection ??= before.TransformSelection;
        var previous = TransformPreview is { Transform.AngleDegrees: 0 } preview
            ? preview.Transform.Translation
            : default;
        return SetTransform(new(default, 0, _transformSelection.Constrain(delta, previous, allowSlide)));
    }

    public bool TryUpdateRotation(PointD pivot, double degrees)
    {
        var before = RequireStart();
        if (degrees == 0 && _transformSelection is null)
        {
            return false;
        }

        _transformSelection ??= before.TransformSelection;
        var constrainedDegrees = _transformSelection.ConstrainRotation(pivot, degrees);
        return SetTransform(new(pivot, constrainedDegrees));
    }

    public void Clear()
    {
        TransformPreview = null;
        _transformSelection = null;
        _start = null;
        Epoch++;
    }

    private bool SetTransform(RigidTransform2D transform)
    {
        if (TransformPreview?.Transform == transform)
        {
            return false;
        }

        TransformPreview = new(_transformSelection!, transform);
        return true;
    }
}
