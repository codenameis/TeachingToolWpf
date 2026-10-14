namespace VisionCanvas.Core.Editing;

public sealed partial class EditorSession
{
    public Task<PreparedEditorChange> PrepareAttachAsync(IEnumerable<Guid> ids, Guid parentId, CancellationToken token = default)
    {
        var roots = ids.Distinct().ToArray();
        return PrepareChangeAsync("부모 ROI에 연결", before => RoiGroupEditor.Attach(before, roots, parentId, token), token);
    }
}
